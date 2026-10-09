# Design: LAN connection with pairing and encryption (for HQ review)

Status: proposal, nothing implemented. Branch `claude/hardware-video`. Date: 2026-10-08.

## 1. Why, and whether it is needed yet

Today the headset app talks to the PC host (`XrssStream`) over `127.0.0.1:5600`. The host listens on loopback only; the headset reaches it through `adb reverse`, which needs the Frame in developer mode, adb over IP, and a tunnel that must be re-created whenever adb restarts (the host supervisor now does that automatically).

What a direct LAN connection gives:
- No adb and no tunnel: the app starts and finds the PC by itself. Fewer moving parts and fewer failures like the dropped tunnel seen on 2026-10-08.
- Works for anyone with a Frame, not only a developer-mode machine. Required for any release beyond this PC.
- The PC can be anywhere on the network (including Wi-Fi), the headset needs no USB dongle link to it. Cost: Wi-Fi latency and jitter instead of the dongle's 1 ms.
- Lets a second headset or a second PC exist later.

What it costs: a network-exposed host that can **type and click on the PC** (`SendInput`, keyboard injection) and **stream the screen and sound**. Without authentication, anyone on the same network could drive the PC. So pairing and encryption are a precondition for opening the port, not an extra.

Is it needed yet? **No, for the current single-user setup**: the loopback tunnel is private by construction and the auto-reconnect makes it dependable. It becomes necessary when (a) the app is to run without adb, (b) anyone else will use it, or (c) wired USB/dongle latency is not available. Recommendation: do the design review now, implement after the remaining headset-quality work (HEVC judgement), keep the loopback mode as a permanent, always-secure fallback.

## 2. Threat model (short)

Attackers considered: another device on the same Wi-Fi/LAN; a guest; malware on a different machine in the house. Not considered: an attacker already running code on the PC or the Frame.

Assets: control of the PC's mouse and keyboard; the content of every streamed window; the PC's audio.

Must hold:
1. An unpaired device can never send input, receive pixels, or see window titles.
2. A passive observer on the network learns nothing useful (window titles, typed text, pixels).
3. A recorded session cannot be replayed to control the PC.
4. Pairing cannot be completed by someone who is merely on the network (man in the middle).
5. The user can see who is paired and revoke a device.

## 3. Proposed design

### 3.1 Transport
Keep the existing framed protocol (`XRS2`, `XRSL`, `XRSS`, `XRSA`, `XRSY`, `XRSV` and the one-letter requests) unchanged and wrap the whole TCP stream in **TLS 1.3** (.NET `SslStream` on the PC; on the Frame, .NET `SslStream` in the Unity Mono runtime, to be verified). One TCP connection, as today, so the multi-stream design is untouched. Loopback mode stays plain TCP (it never leaves the machine and the tunnel is the trust boundary), selected by address.

Alternative considered: Noise (XX/IK) with a small hand-written transport. Smaller and simpler key handling, but it means shipping a custom crypto layer; TLS 1.3 with pinned self-signed certificates is boring and well reviewed. Preferred: TLS with certificate pinning. HQ may overrule.

### 3.2 Identity and pinning
- The host creates a long-lived self-signed ECDSA P-256 certificate on first run (`%LOCALAPPDATA%\XrSpatialScreens\host-cert.pfx`, DPAPI-protected).
- The headset app creates its own client key and self-signed client certificate on first run (Android keystore if reachable from Unity, otherwise app-private storage).
- Mutual TLS: each side accepts only a peer whose certificate **fingerprint is in its paired list**. No certificate authority, no trust in the network.

### 3.3 Pairing (once per headset)
Goal: put each side's certificate fingerprint into the other's paired list, with a human confirming it is the right device.
1. On the PC, the user presses **Pair a headset** in the host (tray window or `host.exe --pair`). The host starts a 2-minute pairing window and shows a **6-digit code** and its LAN address.
2. In the headset app's palette: **Pair with PC**. The app finds the host by mDNS (`_xrss._tcp`), or the user can type the address; it asks for the code (laser-keyboard already exists).
3. The two sides run a **password-authenticated key exchange (SPAKE2 / CPace)** over the not-yet-trusted connection, using the code as the password, and bind the result to both TLS certificate fingerprints. If the code is wrong, or a man in the middle swapped a certificate, the confirmation MAC fails; nothing is stored.
4. On success each side stores the other's fingerprint (host: label, fingerprint, date; headset: host name, fingerprint). The pairing window closes. Three wrong codes close it early.
5. The code is single use, never reused, never sent in the clear, and useless after the window.

Simpler alternative if a PAKE library is a problem on the Frame: show a short authentication string (the first 6 digits of a hash over both fingerprints) on both screens and have the user confirm they match (the Bluetooth numeric comparison model). One more tap, no extra crypto library. Needs the host to display and the headset to display, both available.

### 3.4 Normal connection
Headset connects to the paired host (mDNS name first, last known address second, manual address third), mutual TLS, both pinned fingerprints must match, then the protocol starts exactly as today. An unknown client gets the TLS alert and nothing else. The host accepts one headset at a time (as now); an unpaired client cannot even trigger a window list.

### 3.5 Revocation and management
Host UI/command: list paired devices, remove one, **remove all**. Removing a device drops its live connection. A fresh install of the headset app (new key) must re-pair, which also covers the Lepton-reset case. The layout backup that already exists on the PC is unaffected.

### 3.6 Hardening
- Bind to the LAN only when at least one device is paired or pairing is open; otherwise stay on loopback.
- Firewall: add one inbound rule for the host's port (private network profile only), created by the installer, not left to the user.
- Rate limit connection attempts; log refused connections (address, time) to the host log.
- Keep input safety: window must still be in front and uncovered before a click is applied (existing behaviour); consider an on-PC indicator while a headset is connected.
- Protocol version byte in the first message so old clients get a clear refusal.

## 4. Performance and latency
TLS 1.3 with AES-GCM costs well under 1 ms of CPU per frame at these sizes (about 15-40 Mbit/s JPEG, up to about 40 Mbit/s HEVC). Both CPUs have AES acceleration. TCP over Wi-Fi is the real change: expect the 17-23 ms round trip seen on the dongle link to grow and become jittery over ordinary Wi-Fi; Wi-Fi 6/6E on 5/6 GHz and a wired PC are strongly advised. Measure before promising numbers. UDP/QUIC would help against head-of-line blocking but is out of scope for this step.

## 5. Plan (small steps, each ends with a check)
1. **Spike (1 day):** confirm `SslStream` mutual TLS with self-signed pinning runs in the Frame's Unity runtime; confirm mDNS discovery works on the Frame and PC. Decide TLS vs Noise from the result.
2. **Host:** listener on LAN, TLS, paired-device store, pairing window and code UI, firewall rule, refusal logging. Unit tests with a scripted client (unpaired refused, wrong code refused, replay refused).
3. **Headset:** key creation, discovery, pairing screen, connect with pinned host, "not paired" state in the palette.
4. **Measure** latency and bandwidth over the real Wi-Fi vs the dongle link; set sane defaults (quality tiers already adapt).
5. **Fallback and docs:** loopback/adb mode kept as an always-available mode; user instructions; the installer/launcher story (see the one-click Start shortcut).

## 6. Questions for HQ
1. TLS 1.3 with pinned self-signed certificates vs Noise: any reason to prefer Noise here?
2. PAKE (SPAKE2/CPace) vs numeric comparison for pairing: is the added complexity of the PAKE justified for a single-user product?
3. Should the host require a user confirmation on the PC for every new connection of an already paired headset (stronger, more friction), or only at pairing time?
4. Is exposing keyboard injection over the LAN acceptable at all, or should remote typing require a separate opt-in switch on the PC?
5. Release model: is the loopback/adb mode enough for the first public build, deferring LAN?
6. Anything in the threat model that is missing (guests on the Wi-Fi, a stolen headset)? A stolen headset keeps its pairing until revoked on the PC; should pairing expire (for example 30 days of no use)?
