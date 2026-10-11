# Plan: pairing, LAN transport and one-click host (2026-10-11)

Goal: a customer with a Steam Frame and a Windows PC connects with no adb, no developer mode and no scripts. This gates `main` and the Steam page (see STEAM_PAGE_PREP.md). Builds on the design in `PAIRING_LAN_DESIGN.md` (branch `claude/hardware-video`, proposal only). Working branch: `claude/pairing-lan`, from `develop`. Loopback + adb stays as a permanent fallback.

## Proven architecture (from the spike, 2026-10-11; evidence in `PAIRING_SPIKE_RESULTS.md` on `claude/pairing-spike`, research only, not to be merged)
On the Frame: mDNS works (multicast lock + manifest permission); TLS with a pinned server certificate works, **TLS 1.2 only** (negotiated `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`); the headset **cannot create a client certificate**, and ECDSA and AES-GCM are unavailable; HMAC-SHA256, SHA-256 and secure random work. So the plan **drops mutual TLS** and uses:

**server-pinned TLS (1.2) -> visual code comparison -> random 256-bit device secret -> HMAC challenge on every reconnect**

Rules (security review inputs):
1. **Pairing only inside a pairing window.** The host accepts enrollment only while the user has explicitly opened a 2-minute window on the PC (3 wrong attempts close it). A TLS connection alone never enrolls a device.
2. **Fresh, commitment-protected code per ceremony.** The 6-digit string comes from a hash over the certificate fingerprint and nonces from BOTH sides, exchanged commit-then-reveal so neither side (nor a man in the middle) can choose inputs after seeing the other's. The first draft (hash of fingerprint and a host nonce only) was rejected; see "Pairing ceremony (final)".
3. **Device secret:** 32 random bytes from the headset's secure RNG, sent to the host only inside the pinned TLS channel during an open window. Used directly as the HMAC key (no PBKDF2: it is already high-entropy). Host stores it protected with **DPAPI** (never plaintext); the headset stores it in app-private storage with the pinned host fingerprint.
4. **Reconnect authentication:** host sends a fresh random challenge (at least 16 bytes, from a CSPRNG, never reused, rejected if replayed); headset answers `HMAC-SHA256(secret, "XRSS-AUTH-v1" || host-id || device-id || challenge)`; host compares in **constant time**. Failures are rate-limited per address and logged. Until it passes, the connection can request nothing (no window list, no input, no pixels).
5. **Revocation:** removing a device deletes its secret on the host; the headset is refused from then on and must re-pair.
6. **Dev path unchanged:** loopback + `adb reverse` stays plain TCP and selectable by address; the transport interface hides which mode is in use.

Step 1 (spike) is DONE. Step 2 (Host Core) is implemented (`HOST_CORE_REPORT.md`). Step 3 (headset client) is implemented and demonstrated on the real Steam Frame: discover, pair, stream, drop, automatic reconnect, revocation (`HEADSET_PAIRING_REPORT.md`; 78 transport tests, Unity EditMode 82, PlayMode 35). Steps 4 onward (tray app, installer and firewall rule, distribution) are not started.

## Pairing ceremony (final)
**Why the first draft was wrong.** A 6-digit code is only about 20 bits. If the code is a hash of inputs the attacker can still choose after seeing the other side's value (its own certificate, or a nonce), the attacker simply tries about a million candidates (a million P-256 keys or random nonces take seconds) until both victims see the same six digits. The user would then confirm a code that "matches" while a man in the middle sits in the channel. Truncating to 6 digits is only safe if every input is **committed to before the other side's value is known**. This is the standard short-authentication-string construction used by ZRTP and by Bluetooth numeric comparison (hash commitment, then reveal). It needs only SHA-256 and a secure RNG, both proven on the Frame, so no managed crypto library is added and no custom primitive is invented.

**Steps** (all inside the not-yet-trusted TLS 1.2 channel; the headset records the certificate fingerprint `F` it was shown by the TLS handshake, the host uses its own certificate fingerprint):
1. Pairing window is open on the PC (explicit, 2 minutes). Headset sends `Hello(mode=pair, deviceId)`.
2. Headset draws a random 32-byte `Nc` and sends the **commitment** `C = SHA-256("XRSS-SAS-commit-v1" || Nc)`. `Nc` stays secret.
3. Host draws a random 32-byte `Nh` and sends it (with its host id). It is chosen after `C` is fixed.
4. Headset **reveals** `Nc`. Host checks it against `C`; a mismatch is a failed attempt.
5. Both sides compute `SAS = first 4 bytes of SHA-256("XRSS-SAS-v1" || len-prefixed(F) || Nc || Nh || deviceId)`, taken as an unsigned integer modulo 1,000,000, shown as six digits. Host uses its own certificate fingerprint, headset the one it saw. With a man in the middle those two fingerprints differ.
6. The code appears on the PC and in the headset. The user confirms **on both devices** that the codes match (PC: "Yes, same code"; headset: "Same code"). A mismatch on either side cancels and counts as a failure.
7. Only after the headset user confirms does the headset send its random 32-byte device secret (and a label), still inside TLS. The host stores it (DPAPI) only after the PC-side confirmation has also arrived, then closes the window and answers `Ok`.

**Why the attacker cannot grind.** The headset commits to `Nc` before it sees anything from the host, and the host's `Nh` is chosen after `C` is fixed. A man in the middle must pick its commitment toward the host and its `Nh'` toward the headset before it learns the real `Nc`, and its certificate is already fixed by the TLS handshake before any of this starts. Whatever it chooses, the two displayed codes are then independent 6-digit values: success probability per attempt is about 1 in 1,000,000. An attacker cannot retry quickly: every failed or cancelled ceremony counts, three failures close the window, and the window must be reopened by a person at the PC. A headset-impersonating attacker who connects during an open window gets nothing without the PC user confirming a code the attacker cannot show them on a real headset.

**What stays out.** No PAKE, no managed crypto library, no cryptography beyond SHA-256, HMAC-SHA256 and the secure RNG. If review prefers a stronger exchange later (for example SPAKE2 via a managed library), only the ceremony inside the transport layer changes.

## Decisions to make first (defaults I will use unless told otherwise)
| Question | Default |
|---|---|
| TLS 1.3 + pinned self-signed certs vs Noise | TLS 1.2 with pinned server certificate (spike: 1.3 and client certificates are not available on the Frame) |
| PAKE vs numeric comparison for pairing | Commitment-based 6-digit comparison confirmed on both PC and headset (see Pairing ceremony (final)); no extra crypto library |
| Confirm every connection on the PC | Only at pairing; paired headset reconnects silently, PC shows a "headset connected" indicator |
| Remote typing over LAN | Allowed once paired; PC-side switch "Allow typing" (default on) |
| Pairing expiry | None at first; host lists and revokes devices; reconsider 30 days idle |
| Port exposure | LAN bind only while a device is paired or a pairing window is open; private network profile only |

## Architecture rules (reviewer checks)
1. **Dev path preserved.** Loopback + `adb reverse` stays selectable by address (plain TCP, no pairing) and keeps working unchanged; `host.ps1` and the supervisor are not removed. Tests for it keep running.
2. **Pairing isolated under the transport.** All TLS, certificates, pairing and discovery live behind one transport interface beneath `RemoteHost` (headset) and the connection listener in `XrssStream` (PC). The framed protocol (`XRS2`, `XRSL`, `XRSS`, ...), capture, input, audio, cursor and panel code do not know whether the link is loopback or TLS. A feature file that mentions a certificate or a pairing code is a bug.
3. **Consumer flow is four steps:** discover the PC (mDNS, typed address as backup), confirm the matching 6-digit code on both screens, trust once (both sides store the fingerprint), reconnect automatically after that with no prompts. Anything that adds a step to this flow needs a reason written in the doc.

## Steps (each ends with a check you can run)
1. **Spike (DONE 2026-10-11, research evidence only on `claude/pairing-spike`).** Pinned TLS 1.2, mDNS and available crypto were verified on the Frame over the dongle network. mDNS answers were also seen across the router network. Wi-Fi latency has NOT been benchmarked yet (step 5).
2. **Host core (this phase, see HOST_CORE_REPORT.md).** LAN listener wrapped in TLS 1.2, host identity (DPAPI), paired-device store (DPAPI secrets), pairing window (2 min, 3 failures closes it), commitment ceremony, HMAC reconnect, revocation, rate limiting, auth protocol version byte, dev transport untouched. Check: unit tests with a scripted client (unpaired refused, wrong code refused, replay refused, revoked drops live link); existing loopback tests unchanged.
3. **Headset side (DONE, see HEADSET_PAIRING_REPORT.md).** Device secret and paired-host credentials (pinned fingerprint, device id, label), discovery, "Pair with PC" screen (uses existing laser keyboard), connect with pinned host, palette state "Not paired / Connecting / Connected", reconnect (last address, then mDNS, then typed). Check: EditMode/PlayMode tests; pair and reconnect on the Frame over the dongle link first.
4. **One-click host app.** Tray app: Start/Stop, Pair a headset (shows code and LAN address), paired list with Remove / Remove all, "headset connected" indicator, firewall rule for the one port (private profile) created by the installer, autostart option. Replaces `host.ps1` for customers.
5. **Measure.** Latency and bandwidth: dongle vs Wi-Fi 6/6E, JPEG tiers. Set defaults and write honest requirements for the store page. Check: numbers recorded in `Docs/LAN_MEASUREMENTS.md`.
6. **Distribution.** Decide how the APK reaches the Frame (Steam depot vs helper that installs it); installer for the PC host (bundled .NET 8, capture helper, firewall rule); uninstall removes paired list and rule.
7. **Hardening and release check.** Rate limiting, refusal logging, stolen-headset story (revoke on PC), security review of the diff (`/security-review`), full tests, then headset smoke test. Only then promote `develop` to `main`.

## Out of scope for this plan
HEVC hardware video (stays on `claude/hardware-video`, revisit after LAN works), per-window sound, captions/clarity boost, QUIC/UDP.

## Risks
- TLS 1.2 only on the Frame (no 1.3, no client certificates, no ECDSA/AES-GCM in the runtime); the design avoids all of those, but any future feature needing them requires a managed crypto library.
- Wi-Fi latency and multicast reliability under power-save are unmeasured.
- Wi-Fi adds latency and jitter versus the 1 ms dongle link; the product may need to say "dongle recommended".
- A network-exposed host can click and type on the PC: pairing must be correct before the port ever opens.
- Distribution rules for Steam Frame apps are unconfirmed.
