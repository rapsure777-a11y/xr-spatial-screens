# Plan: pairing, LAN transport and one-click host (2026-10-11)

Goal: a customer with a Steam Frame and a Windows PC connects with no adb, no developer mode and no scripts. This gates `main` and the Steam page (see STEAM_PAGE_PREP.md). Builds on the design in `PAIRING_LAN_DESIGN.md` (branch `claude/hardware-video`, proposal only). Working branch: `claude/pairing-lan`, from `develop`. Loopback + adb stays as a permanent fallback.

## Proven architecture (from the spike, 2026-10-11; evidence in `PAIRING_SPIKE_RESULTS.md` on `claude/pairing-spike`, research only, not to be merged)
On the Frame: mDNS works (multicast lock + manifest permission); TLS with a pinned server certificate works, **TLS 1.2 only** (negotiated `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`); the headset **cannot create a client certificate**, and ECDSA and AES-GCM are unavailable; HMAC-SHA256, SHA-256 and secure random work. So the plan **drops mutual TLS** and uses:

**server-pinned TLS (1.2) -> visual code comparison -> random 256-bit device secret -> HMAC challenge on every reconnect**

Rules (security review inputs):
1. **Pairing only inside a pairing window.** The host accepts enrollment only while the user has explicitly opened a 2-minute window on the PC (3 wrong attempts close it). A TLS connection alone never enrolls a device.
2. **Fresh code per ceremony.** The 6-digit string is derived from SHA-256(server certificate fingerprint || pairing-session nonce chosen by the host for this window), shown on both screens; never one permanent number per PC.
3. **Device secret:** 32 random bytes from the headset's secure RNG, sent to the host only inside the pinned TLS channel during an open window. Used directly as the HMAC key (no PBKDF2: it is already high-entropy). Host stores it protected with **DPAPI** (never plaintext); the headset stores it in app-private storage with the pinned host fingerprint.
4. **Reconnect authentication:** host sends a fresh random challenge (at least 16 bytes, from a CSPRNG, never reused, rejected if replayed); headset answers `HMAC-SHA256(secret, "XRSS-AUTH-v1" || host-id || device-id || challenge)`; host compares in **constant time**. Failures are rate-limited per address and logged. Until it passes, the connection can request nothing (no window list, no input, no pixels).
5. **Revocation:** removing a device deletes its secret on the host; the headset is refused from then on and must re-pair.
6. **Dev path unchanged:** loopback + `adb reverse` stays plain TCP and selectable by address; the transport interface hides which mode is in use.

Step 1 (spike) is DONE. Next: reviewer inspects this design, then step 2 (host core) on this branch.

## Decisions to make first (defaults I will use unless told otherwise)
| Question | Default |
|---|---|
| TLS 1.3 + pinned self-signed certs vs Noise | TLS 1.2 with pinned server certificate (spike: 1.3 and client certificates are not available on the Frame) |
| PAKE vs numeric comparison for pairing | Numeric comparison (fresh 6-digit string per pairing session, see Proven architecture); no extra crypto library |
| Confirm every connection on the PC | Only at pairing; paired headset reconnects silently, PC shows a "headset connected" indicator |
| Remote typing over LAN | Allowed once paired; PC-side switch "Allow typing" (default on) |
| Pairing expiry | None at first; host lists and revokes devices; reconsider 30 days idle |
| Port exposure | LAN bind only while a device is paired or a pairing window is open; private network profile only |

## Architecture rules (reviewer checks)
1. **Dev path preserved.** Loopback + `adb reverse` stays selectable by address (plain TCP, no pairing) and keeps working unchanged; `host.ps1` and the supervisor are not removed. Tests for it keep running.
2. **Pairing isolated under the transport.** All TLS, certificates, pairing and discovery live behind one transport interface beneath `RemoteHost` (headset) and the connection listener in `XrssStream` (PC). The framed protocol (`XRS2`, `XRSL`, `XRSS`, ...), capture, input, audio, cursor and panel code do not know whether the link is loopback or TLS. A feature file that mentions a certificate or a pairing code is a bug.
3. **Consumer flow is four steps:** discover the PC (mDNS, typed address as backup), confirm the matching 6-digit code on both screens, trust once (both sides store the fingerprint), reconnect automatically after that with no prompts. Anything that adds a step to this flow needs a reason written in the doc.

## Steps (each ends with a check you can run)
1. **Spike (DONE 2026-10-11).** In a throwaway Android build, run `SslStream` mutual TLS with self-signed pinning and mDNS (`_xrss._tcp`) discovery against a PC test server. Check: Frame connects over Wi-Fi and over the dongle network; report whether Mono TLS 1.3 and mDNS work. Fallbacks: manual IP entry; Noise.
2. **Host core.** LAN listener wrapped in TLS, host certificate (DPAPI), paired-device store, pairing window (2 min, 3 wrong tries closes it), refusal log, protocol version byte. Check: unit tests with a scripted client (unpaired refused, wrong code refused, replay refused, revoked drops live link); existing loopback tests unchanged.
3. **Headset side.** Client key, discovery, "Pair with PC" screen (uses existing laser keyboard), connect with pinned host, palette state "Not paired / Connecting / Connected", reconnect (last address, then mDNS, then typed). Check: EditMode/PlayMode tests; pair and reconnect on the Frame over the dongle link first.
4. **One-click host app.** Tray app: Start/Stop, Pair a headset (shows code and LAN address), paired list with Remove / Remove all, "headset connected" indicator, firewall rule for the one port (private profile) created by the installer, autostart option. Replaces `host.ps1` for customers.
5. **Measure.** Latency and bandwidth: dongle vs Wi-Fi 6/6E, JPEG tiers. Set defaults and write honest requirements for the store page. Check: numbers recorded in `Docs/LAN_MEASUREMENTS.md`.
6. **Distribution.** Decide how the APK reaches the Frame (Steam depot vs helper that installs it); installer for the PC host (bundled .NET 8, capture helper, firewall rule); uninstall removes paired list and rule.
7. **Hardening and release check.** Rate limiting, refusal logging, stolen-headset story (revoke on PC), security review of the diff (`/security-review`), full tests, then headset smoke test. Only then promote `develop` to `main`.

## Out of scope for this plan
HEVC hardware video (stays on `claude/hardware-video`, revisit after LAN works), per-window sound, captions/clarity boost, QUIC/UDP.

## Risks
- Mono TLS or mDNS may not work on the Frame (the spike answers this in step 1).
- Wi-Fi adds latency and jitter versus the 1 ms dongle link; the product may need to say "dongle recommended".
- A network-exposed host can click and type on the PC: pairing must be correct before the port ever opens.
- Distribution rules for Steam Frame apps are unconfirmed.
