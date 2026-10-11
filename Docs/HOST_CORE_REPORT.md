# Host Core implementation report (2026-10-11, branch `claude/pairing-lan`)

Scope: the secure Windows-side pairing/authentication foundation and a clean boundary under the application protocol. Not in this phase: headset pairing UI, tray app, installer, firewall logic, mDNS advertising, Steam distribution, HEVC, QUIC, Wi-Fi tuning, any new feature. Not merged to `develop`.

## Final pairing ceremony
Commit-then-reveal short authentication string over the not-yet-trusted TLS 1.2 channel, confirmed by a person on BOTH the PC and the headset. Full reasoning (why the first draft, a hash of fingerprint plus a host nonce, was rejected: an attacker can grind about a million candidates until two six-digit codes collide) is in `PAIRING_PLAN.md` under "Pairing ceremony (final)". Summary:

1. A person opens the 2-minute pairing window on the PC. Headset sends `Hello(mode=pair, deviceId)`.
2. Headset sends `C = SHA-256("XRSS-SAS-commit-v1" || Nc)` for a random `Nc`.
3. Host sends a random `Nh` (chosen after `C` is fixed).
4. Headset reveals `Nc`; host checks it against `C`.
5. Both compute `SAS = first 4 bytes of SHA-256("XRSS-SAS-v1" || len(F) || F || Nc || Nh || deviceId)` mod 1,000,000, where `F` is the certificate fingerprint each side actually used (the host its own, the headset the one TLS showed it). A man in the middle makes the two `F` differ.
6. The user confirms the six digits match on the PC (`yes`) and on the headset. A mismatch or cancel on either side is a failed attempt.
7. Only then does the headset send its random 32-byte device secret (and label) inside TLS; the host enrolls it after the PC confirmation too, closes the window, and answers `Ok`.

Per attempt a man in the middle succeeds with probability about 1 in 1,000,000; three failures close the window and a person must reopen it. Only SHA-256 and the secure RNG are used, both proven on the Frame. No new primitive, no managed crypto library.

## Files
Added (all under `Tools/`):
- `XrssTransport/` (class library, net8.0): `AuthCrypto.cs` (all formulas), `AuthProtocol.cs` (framing, messages, version), `HostIdentity.cs`, `DeviceStore.cs`, `PairingWindow.cs`, `RateLimiter.cs`, `SecureListener.cs` (TLS plus the authentication state machine), `LanInterfaces.cs`, `Protector.cs` (DPAPI), `TransportLog.cs`.
- `XrssTransport.Tests/` (xunit): `Harness.cs` (scripted headset client, fake clock, host fixture), `HostCoreTests.cs`, `HostProcessTests.cs` (real host executable end to end).
- `XrssStream/TransportHost.cs`: the only glue between the host application and the transport (creates the pieces, hands authenticated connections to `Session`, admin commands, one-session-at-a-time rule).

Changed:
- `XrssStream/Program.cs`: `Session` takes any `Stream` (plain TCP stream by default, so the dev path is byte-identical); `Main` gained `--transport dev|lan|both` (default `dev`) and `--lan-port` (default 5610); one `RunOfflineCommand` call at the top. No capture, input, audio, cursor or protocol line changed.
- `XrssStream/XrssStream.csproj`: project reference to `XrssTransport`.
- `.gitignore`, `Docs/PAIRING_PLAN.md`, this report.

Nothing in `Assets/` (the headset app) changed. No feature file references certificates, device secrets or pairing codes.

## Authentication state machine (per connection, `SecureListener`)
```
Connected --(rate-limit check)--> TLS 1.2 handshake --> TLS established
  --> Authentication pending: only AuthProtocol messages are read; any other first bytes -> Reject(ProtocolError), close
      Hello(version != 1)            -> Reject(UnsupportedVersion, supported=1), logged, close
      Hello(mode=authenticate)       -> Challenge -> Response -> Ok            => Authenticated
      Hello(mode=pair)               -> window closed? Reject(PairingClosed)
                                        else Commit -> HostNonce -> Reveal -> code shown -> Confirm|Cancel + PC yes/no -> Ok => Authenticated
  --> Authenticated: stream handed to the unchanged application protocol (XRS2/XRSL/XRSS/...) via OnAuthenticated
```
Until the last state, no `Session` exists for the connection: no window list, no frames, cursor, audio or input is reachable (test: a raw `'W'` after TLS gets `Reject(ProtocolError)` and nothing else). Every step has a 10 s timeout. At most 32 connections may be unauthenticated at once.

## Certificate storage
`%LOCALAPPDATA%\XrSpatialScreens\host\host-identity.dat` (override with `XRSS_HOST_DIR`). Content is DPAPI-protected JSON `{version, hostId, pfx(base64), createdUtc}`: a random 16-byte host id and a self-signed ECDSA P-256 certificate (20 years). The file is not readable JSON on disk (tested). `Regenerate()` creates a new host id and certificate, which orphans every paired device (their entries carry the old host id), so they must pair again; no migration by design. A corrupt or unreadable file creates a new identity (fail closed: devices must re-pair). The certificate is loaded with the default key set because Windows' TLS stack cannot use an in-memory-only server key; in testing, dozens of short-lived identities left only 2 key files in the user's DPAPI-protected key store, so keys are normally cleaned up when the certificate is disposed; a host killed without cleanup can leave one behind.

## Device-store format (`devices.json`, version 1)
```json
{ "Version": 1, "Devices": [ { "DeviceId": "<32 hex>", "Label": "Living room", "HostId": "<32 hex>",
  "SecretProtected": "<base64 of DPAPI(32-byte secret)>", "PairedUtc": "...", "LastConnectedUtc": "..." } ] }
```
Atomic writes (temp file then replace). Removal deletes the entry; there is no "revoked but kept" state. `List()` never returns the protected blob. An unreadable store starts empty (fail closed).

## How DPAPI is used
`System.Security.Cryptography.ProtectedData`, scope CurrentUser, with a fixed application entropy string `XrSpatialScreens.Transport.v1`. Used for the host identity blob and for each device secret separately. The raw secret exists in memory only while enrolling or verifying an HMAC and is cleared afterwards. Limit: any code running as the same Windows user can ask DPAPI to decrypt (the threat model excludes malware on the PC). The headset's own storage of its secret is the headset phase.

## Pairing-window behaviour
Opened explicitly (`pair [seconds]` on the host console, default 120 s). One ceremony at a time (a second connection gets `Reject(Busy)`). Closed by expiry, success, `pair-close`, or the third failed ceremony (the fourth attempt gets `Reject(PairingClosed)`). Closing is immediate: enrollment re-checks `IsCurrent(session)` at the moment of writing, so a window closed during the ceremony stores nothing. A connection that ends after the code was shown without success counts as a failure (a prober cannot retry for free); one that ends before a code is shown does not. Enrollment attempts from an address with no open window are rate-limited separately. Logs: `pairing opened`, `pairing expired`, `pairing completed`, `pairing refused (reason), failure n of 3`, `pairing closed: too many failures`. No secret, code, nonce, challenge or full device id is logged (tested); the code appears only on an interactive console (or when `XRSS_SHOW_CODE=1` for tests).

## Reconnect authentication
Host sends a fresh 32-byte CSPRNG challenge with its host id. Headset answers `HMAC-SHA256(secret, "XRSS-AUTH-v1" || hostId || deviceId || challenge)`. Host compares in constant time (`CryptographicOperations.FixedTimeEquals`). A challenge is accepted once and expires after 30 s; a recorded response replayed on a new connection fails because the new challenge differs (tested). An unknown device is checked against a random dummy secret, so unknown and wrong-secret are indistinguishable (same reject, same flow). The secret is used directly as the HMAC key (no PBKDF2).

## Revocation
`Remove(deviceId)` and `RemoveAll()` delete the entries (and so the secrets) and raise `Removed`; `SecureListener` closes every live connection of that device immediately; the old secret never authenticates again and the device must pair again. Host console: `devices`, `remove <id prefix>`, `remove-all`. Offline (host not running): `XrssStream --devices | --remove-device ID | --remove-all-devices | --regenerate-identity` (a running host does not see offline edits; use its console).

## Protocol versioning
Authentication protocol version byte, currently 1, independent of any application feature. Unknown version: `Reject(UnsupportedVersion)` carrying the supported version, a log line, connection closed, listener unaffected (tested).

## Rate limiting
- Authentication: 5 failures from one address in 60 s lock that address out for 60 s (connections dropped before TLS). Any success clears the count; four typos then success never locks (tested).
- Enrollment: separate limiter, so a person pressing "Pair" before opening the window cannot lock out their own reconnects.
- Connection flood: more than 20 connection attempts from one address in 10 s are dropped; a headset reconnecting every few seconds is never affected (tested).
- Unauthenticated connection cap: 32 at once.

## Transport selection
`--transport dev` (default, unchanged: plain TCP on 127.0.0.1 for `adb reverse`, `host.ps1` and the supervisor untouched), `lan` (paired TLS listener only), `both`. The LAN listener binds only up, private (RFC 1918) IPv4 interfaces, and only while at least one device is paired or a pairing window is open; otherwise no port is exposed. One headset session at a time: a new paired-LAN connection replaces an older paired-LAN one (so a headset that dropped off Wi-Fi reconnects at once); a dev session is never kicked and a LAN connection is refused while one is active.

## Test results
- Host Core xunit: **42 passed, 0 failed** (three consecutive runs). Covers: window open/close/expire; one ceremony at a time; three failures close it and the fourth attempt is refused; reopening resets failures; enrollment outside or after the window refused; window closed mid-ceremony stores nothing; PC-user reject and headset cancel are failures; man-in-the-middle sees a different code; wrong reveal breaks the commitment; secret persisted protected and never plaintext; identity persists and is protected; regenerate invalidates devices; valid HMAC succeeds and reaches the application protocol; wrong HMAC, wrong device id, wrong host id fail; replayed response and reused challenge fail; revoked device fails and its live connection drops; remove-all invalidates; unauthenticated client cannot reach the protocol; unknown auth version refused; failed-auth lockout with normal reconnects unaffected; nothing sensitive in logs; TLS is 1.2 and a wrong pin is refused by the client; the managed listener binds only when something is paired or a window is open; private-address filter.
- Real host executable end to end (`HostProcessTests`): paired a scripted headset against `XrssStream.exe --transport lan` pinned to the printed fingerprint, saw the PC show the same code, confirmed, received the real `XRSL` window list over TLS, reconnected by HMAC (replacing the first session), `remove-all` dropped the live connection and closed the port, the old secret was useless afterwards, and no secret appeared in the host output. Dev mode: plain loopback client got `XRSL` with no TLS and no pairing, and no secure listener started.
- Existing suites unchanged: Unity EditMode 72/72, PlayMode 35 passed, 0 failed, 1 skipped (pre-existing).

## Remaining risks and open items
- The headset side does not exist yet (secret storage, pinned fingerprint, discovery, pairing screen, reconnect). The auth client formulas are in `AuthCrypto.cs`/`Harness.cs` and use only primitives proven on the Frame, but the headset implementation is untested on the Frame.
- Six-digit comparison relies on the user actually comparing; the commitment makes grinding impossible, not inattention. Success chance per attempt about 1e-6 with 3 attempts per window.
- DPAPI protects against other users and offline copying, not malware running as the same Windows user.
- TLS 1.2 only (Frame limit); forward-secret ECDHE with AES-256-GCM observed.
- Pairing a device id that already exists replaces its secret (requires both confirmations); the PC does not yet show "this replaces an existing device".
- Admin surface is a console/stdin interface; a supervised hidden host has no console, so the tray app (next phase) must provide pair/confirm/list/remove.
- Offline admin commands are not seen by a running host.
- Windows firewall rule, mDNS advertising, and Wi-Fi latency are untouched and unmeasured.
- A single process serves one headset at a time (existing behaviour).
