# Headset pairing report (2026-10-11, branch `claude/pairing-lan`)

Scope: the real Steam Frame client for the pairing/authentication work whose Windows side is `HOST_CORE_REPORT.md`. Result, demonstrated on the actual Frame: **discover the PC, pair, stream, disconnect, reconnect automatically, stream again**, plus revocation. Not merged to `develop`. Not built (out of scope): tray app, installer, firewall logic, Steam deployment, HEVC, QUIC, new XR features.

## Architecture boundary (kept)
`RemoteHost` (the application protocol) asks one object, `PcLink.TryConnect`, for a `Stream`. In **Dev** mode that is the old plain TCP connection to 127.0.0.1:5600 through `adb reverse`, byte-for-byte the previous behaviour. In **Paired PC** mode it is an authenticated TLS 1.2 stream. Panels, crop, depth, SBS, cursor, audio, keyboard, controller input and layouts were not touched and do not know about TLS, fingerprints, codes, secrets or challenges. The only edits outside the new `Transport` assembly are in `RemoteHost.cs` (stream type, the connect call, a reconnect-delay hook) and the palette (the PC connection page and one button).

## Files
New assembly `Assets/Scripts/Transport` (`XrSpatial.Transport`):
- `Shared/AuthCrypto.cs`, `Shared/AuthProtocol.cs`, `Shared/MdnsPacket.cs`: SHARED SOURCE, compiled by both the PC host (linked into `Tools/XrssTransport`) and the headset, so every formula, message layout and the mDNS wire format exist once. Vectors: `Docs/PAIRING_TEST_VECTORS.md`.
- `Client/` (pure C#, no Unity types; the same files are compiled into the .NET test project and tested against the real host): `ClientEnv.cs` (platform interface), `ClientStore.cs` (device identity, paired hosts), `SecureChannel.cs` (TLS with pinning), `HostAuthClient.cs` (reconnect auth and the pairing ceremony), `MdnsDiscovery.cs`, `AddressProbe.cs`, `PcLink.cs` (supervisor and state machine), `VectorSelfTest.cs`.
- `Unity/UnityClientEnv.cs`: Android paths, the multicast lock, logging; `PcLinkHost` (the app's single `PcLink`).
- `Assets/Plugins/Android/XrssNet.androidlib`: manifest permissions (multicast state, Wi-Fi state, network state).
- App: `ConnectionPage.cs` (what the connection page says and offers), palette changes (`PalettePanel.cs`).
- Capture: `RemoteHost.cs`. Asmdefs updated to reference the new assembly.
Host (PC) additions needed by the client: `MdnsAdvertiser.cs` (advertises `_xrss._tcp` while the secure listener is bound), a 15-minute revocation grace period, a graceful close after a refusal (see "Bugs found on the way").
Tests: `Tools/XrssTransport.Tests/ClientTests.cs`, `Assets/Tests/EditMode/PairingClientTests.cs`.

## Storage (honest statement)
- **Device identity:** random 16-byte application-scoped id (never a serial or hardware id) and a label ("Steam Frame"), file `device.txt`. Changes only through the explicit reset.
- **Paired host record** (`paired-hosts.txt`, key=value blocks): host id, certificate SHA-256 fingerprint, device id, the 32-byte device secret (base64), last address and port, every address seen for that PC, display name, auth protocol version, paired and last-connected dates, and a "PC removed this headset" flag. Written atomically, and only after the PC answered `Ok` to enrollment.
- **Where:** on the Frame, the app's INTERNAL files directory (`Context.getFilesDir()`, `/data/user/0/<package>/files/xrss-pairing`). That is app-private storage: other apps cannot read it, and with adb on this non-debuggable build `run-as` is refused (verified). It is **not the Android Keystore**: the secret is a plain file inside that private directory. Anyone who roots the headset or extracts the app data can read it. Keystore wrapping (JNI to `AndroidKeyStore` and an AES key) is a possible hardening step and was not done.
- Non-secret settings (`link.txt`: connection mode and typed address) live in the normal app data folder so a developer can set them with adb.
- Logging: no secret, challenge, HMAC, nonce or full device id is logged (tested on both sides; the host logs 8 hex characters of a device id). Sensitive byte arrays are cleared after use where practical.

## Discovery
`_xrss._tcp` over mDNS, as proven by the spike: Wi-Fi multicast lock (acquired for a search only, released after), join 224.0.0.251 on every local IPv4 interface, send PTR queries about once a second, parse PTR/SRV/TXT/A including name compression, merge duplicates and several addresses per PC (`MdnsCollector`). The PC's TXT carries `id` (host id) and `name`. If port 5353 cannot be bound the client uses an ephemeral port with the unicast-response bit. Runs on a worker thread, never on the Unity main thread; every search has a timeout. Typed address fallback with a keypad on the connection page. The PC advertises only while its secure listener is bound.

## TLS validation
TLS 1.2 via `SslStream`, as proven on the Frame. For a paired PC the presented certificate's SHA-256 must equal the saved fingerprint or the handshake fails (`PinMismatchException`); there is no fallback that skips the check. For first-time pairing the certificate is not yet trusted: the fingerprint is recorded and is authenticated by the six-digit code the user compares. Timeouts on connect, handshake and every message read; the socket is closed when one expires. Tested: wrong fingerprint rejected, correct accepted, a different PC at the old address is never trusted and never receives an authentication attempt.

## SAS pairing
Exactly the commitment ceremony of `PAIRING_PLAN.md`: Hello(pair), `C = SHA-256("XRSS-SAS-commit-v1" || Nc)`, host nonce, reveal, code from the fingerprint THIS connection presented. The code is shown large (`165 978` style) with two buttons, **Same code** and **Doesn't match**; nothing is automatic (a test asserts that building the screen confirms nothing). Only after the user presses Same code does the headset create the random 32-byte secret and send it through the pinned channel; the record is saved only after the PC replies `Ok` (it waits up to 130 s for the PC user's confirmation). Cancel, mismatch, PC refusal, timeout and a closed pairing window each store nothing on either side (tested).

## Reconnect authentication
TLS with pin, Hello(authenticate), receive host id and a fresh challenge, **refuse to answer if the host id differs from the saved one**, send `HMAC-SHA256(secret, "XRSS-AUTH-v1" || host-id || device-id || challenge)`, wait for `Ok`, then hand the stream to `RemoteHost`. Same bytes and order as the host (shared source); the formulas match the independent vectors on the Frame itself (a startup self-test logs `auth vectors match`).

## Connection selection and retry
Order for a paired PC: saved addresses (probed together and tried fastest first), then mDNS for the matching host id, then the typed address. **Address ranking** was added after the first Frame run (below). After any successful connect the client quietly learns the PC's other addresses in the background. Trust follows the saved fingerprint and host id, never an address. Retry: immediately after an unexpected drop, then 0.5, 1, 2, 3, 5, 5, then every 10 s. States and plain messages: Dev, Not paired, Searching, Connecting, Pairing, Connected, Reconnecting, Error with PC not found, Wrong PC / identity changed, Pairing window closed, Code rejected, Connection lost, PC removed this headset, Unsupported version, Please wait.

## Revocation
When a PC whose certificate and host id both match answers "unknown device", the headset records that, stops retrying (no HMAC hammering, even across app restarts), and shows "This PC no longer trusts this headset. Pair again." Trust is not cleared behind the user's back and the headset never enrols by itself; **Pair again** (or **Forget this PC**) is the user's action. To make this reachable the PC now stays up for 15 minutes after any removal (and keeps advertising), because otherwise removing the last device closed the PC's port and the headset could only say "PC not found".

## UI flow
Palette, Stream and Sound section: a **PC: <state>** button (it took the free slot, so the palette is not taller). Pages: Dev (button to switch to Paired PC) → Not paired (Search again, a list of found PCs, Type address, Use Dev) → Pairing (connecting, code, waiting for PC, cancel) → Paired (Done) → Connected (Forget this PC, Use Dev) → Reconnecting / can't reach (Retry now, Pair again, Type address, Forget). The page opens by itself once when the PC needs the user (not paired, or removed). A working link stays out of the way. A paired reconnect shows nothing.

## Frame test results (real Steam Frame over adb 10.35.78.1; PC "TITAN"; host `--transport both`)
| Test | Result |
|---|---|
| Dev-mode regression with the first client build | App connected the old adb way (`remote: connected`), self-test `auth vectors match` on the Frame |
| Discover + pair | Frame found TITAN by mDNS, user read 230105 on the headset, PC showed 230105, both confirmed, "connected" |
| Normal features over the paired link | User confirmed: window list, streaming, laser click, keyboard, PC cursor and sound all worked |
| First feel | User: "way more latency" (see address finding below) |
| Reconnect after app restart | No code, 145 ms total; with the fast address: 139 to 148 ms |
| Fast address | After the fix the PC saw the Frame at 10.35.78.1 (dongle) instead of 10.0.0.171; user: "yeah, that was good" |
| Drop and recovery | PC host killed for about 10 s then restarted: headset reconnected by itself about 1.4 s after the PC advertised again (via discovery, TLS 23 ms, auth 14 ms); user: "they came back" |
| Revocation | `remove-all` on the PC: live connection dropped immediately; the headset reconnected once, was refused, stopped (1 refusal counted on the PC) and showed Pair again |
| Pair again (twice, with new codes 693306 and 431777) | Worked, including from the revoked state |
| Dev-mode regression with the final build | `mode=dev`: connected the old way, host's dev listener saw it, no pairing activity |
| Final build in Paired mode | `connected via saved address`, no code |

## Measured connection times (Frame, final build)
- Discovery: a reconnect that had to use discovery after the PC restarted spent 1394 ms in it (that includes waiting for the PC to start advertising); the pairing screen's search runs a full 6 s window to collect results (time to the first answer was not logged separately).
- Probe (TCP connect) per address: dongle 2 to 5 ms, router path 7 to 27 ms.
- TLS handshake: 98 to 110 ms on the Frame for a reconnect (155 ms on the first run), 23 ms in the reconnect after the PC restart.
- HMAC authentication round trip: 7 to 9 ms (14 to 69 ms in earlier runs).
- Full reconnect: 139 to 148 ms with the saved fast address; 284 ms over the slow router path; 2.9 s after a PC restart (includes discovery).
- Frame rate after connecting and streaming: **144 fps, slowest frame 7 ms, 0 of 720 frames over 12.5 ms**, the same as Dev mode. Authentication happens once per connection; after it the only extra cost is TLS record encryption on the stream (the user judged the dongle-path feel good; stream latency was not measured numerically in this phase).

## Bugs found on the real Frame (and fixed)
1. **Latency on the router path.** Pairing connected over the PC's router-network address because it was listed first; the Frame measured 7 to 27 ms there against 2 to 5 ms on the dongle link. Now every known address is probed (TCP connect time, all at once, with a short grace so a dead address costs nothing) and tried fastest first; the PC's other addresses are learned in the background after a successful connect.
2. **PC vanished after removal.** Removing the last device closed the PC's port, so the headset could only report "PC not found". The PC now stays reachable for 15 minutes after a removal.
3. **Lost refusal.** After refusing a client the host closed its socket with the client's next message still unread, so Windows reset the connection and the refusal could be lost ("Other" instead of "pairing closed"). The host now lets the client finish reading before closing. Found as a flaky test, fixed at the cause.

## Automated test totals
- PC transport and client suite (`Tools/XrssTransport.Tests`, includes the real client code against the real host listener and the real host executable): **78 passed, 0 failed** (three consecutive runs).
- Unity EditMode: **82 passed** (72 before, 10 new). Unity PlayMode: **35 passed, 0 failed, 1 skipped** (unchanged).
- Covered: device id persists; paired-host data persists; correct and wrong fingerprint; vectors for SAS, commitment, HMAC and fingerprint (independent Python values, asserted on both sides and on the Frame at startup); enrolment persists only after the final Ok; cancelled, refused, failed and window-closed pairing store nothing; wrong host id; reconnect; revoked device handled without retry loops and across restarts; stale address falls back to discovery; address ranking; typed address; discovery of a different host id not trusted; a different PC at the old address never sees an authentication attempt; dev mode bypasses pairing; secrets never in logs; mDNS parse (compression, garbage, goodbye) and a real advertiser-to-discovery round trip.

## Remaining risks
- The device secret is in app-private storage, not the Android Keystore.
- First-time pairing relies on the user actually comparing the six digits; the ceremony makes grinding impossible, not inattention. The PC-side confirmation in these tests was typed by the agent after the user read the headset digits aloud, a faithful but not identical stand-in for a person at the PC.
- Connecting over the router network works but is slow; the product should keep recommending the dongle link, and Wi-Fi performance was not benchmarked in depth (only connect times).
- mDNS depends on multicast working on the user's network (Wi-Fi power save, client isolation, VLANs); the typed address is the fallback. The Windows firewall rule for the PC host was already present on this PC from earlier prompts; creating it for a customer is the installer's job (not built).
- Pairing a device id that already exists on the PC replaces its secret (both confirmations required); the PC does not yet say so.
- The PC-side pairing UI is still the host console (`pair`, `yes`, `no`, `devices`, `remove-all`); the tray app is the next phase.
- Palette length: the new PC page is separate, and the main palette is back to its previous height, but the palette as a whole is long (user feedback, noted; splitting it into tabs is not done).
- Feature request noted, not built (outside this phase): duplicate a screen with all its settings.
