# Plan: pairing, LAN transport and one-click host (2026-10-11)

Goal: a customer with a Steam Frame and a Windows PC connects with no adb, no developer mode and no scripts. This gates `main` and the Steam page (see STEAM_PAGE_PREP.md). Builds on the design in `PAIRING_LAN_DESIGN.md` (branch `claude/hardware-video`, proposal only). Working branch: `claude/pairing-lan`, from `develop`. Loopback + adb stays as a permanent fallback.

## Decisions to make first (defaults I will use unless told otherwise)
| Question | Default |
|---|---|
| TLS 1.3 + pinned self-signed certs vs Noise | TLS, pinned (boring, reviewed); switch only if the spike fails on the Frame |
| PAKE vs numeric comparison for pairing | Numeric comparison (6-digit string shown on both sides, user confirms); no extra crypto library |
| Confirm every connection on the PC | Only at pairing; paired headset reconnects silently, PC shows a "headset connected" indicator |
| Remote typing over LAN | Allowed once paired; PC-side switch "Allow typing" (default on) |
| Pairing expiry | None at first; host lists and revokes devices; reconsider 30 days idle |
| Port exposure | LAN bind only while a device is paired or a pairing window is open; private network profile only |

## Steps (each ends with a check you can run)
1. **Spike (first, decides everything).** In a throwaway Android build, run `SslStream` mutual TLS with self-signed pinning and mDNS (`_xrss._tcp`) discovery against a PC test server. Check: Frame connects over Wi-Fi and over the dongle network; report whether Mono TLS 1.3 and mDNS work. Fallbacks: manual IP entry; Noise.
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
