# Pairing spike results (2026-10-11, branch claude/pairing-spike)

**RESEARCH ONLY. Do not merge the spike implementation into `develop`. Reimplement the proven pieces cleanly on `claude/pairing-lan`.**

Step 1 of `PAIRING_PLAN.md`. Throwaway code: `Assets/Scripts/App/TlsSpike.cs` (runs only if `tlsspike.flag` exists in the app data folder, content `host port serverSha256`), `Tools/PairingSpike` (PC echo server with a persisted self-signed ECDSA certificate and an mDNS responder), `Assets/Plugins/Android/XrssNet.androidlib` (manifest: CHANGE_WIFI_MULTICAST_STATE, ACCESS_WIFI_STATE, ACCESS_NETWORK_STATE). Run on the Steam Frame (IL2CPP, .NET Standard 2.1 profile, Unity 6000.3.9f1) against the PC over the dongle network (PC 10.35.78.22, Frame 10.35.78.1). The PC also has Ethernet 10.0.0.224 on the router network.

| Question | Result |
|---|---|
| mDNS discovery (`_xrss._tcp`) | **Works.** With a WifiManager multicast lock and the manifest permission, the Frame joined 224.0.0.251:5353, sent a PTR query and heard 12 answers (PTR/SRV/TXT/A) from the PC within 6 s, with the PC's addresses on both networks. Note the query reached the PC from the Frame's router address 10.0.0.171, so discovery also works on the router network. |
| TLS with the server certificate pinned (SHA-256) | **Works.** `SslStream.AuthenticateAsClient` over UnityTLS; the validation callback compares the thumbprint; wrong pin is refused (`UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED`). Self-signed ECDSA P-256 server certificate from the PC was accepted. Handshake 14 to 198 ms. |
| TLS 1.3 | **Not available.** `SslProtocols.Tls13` does not exist in Unity's API, and asking for the raw value still negotiated **TLS 1.2** (negotiated cipher suite as reported by the PC: `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`, i.e. forward-secret ECDHE key exchange, AES-256-GCM, SHA-384; the headset-side offered list was not captured). Plan: TLS 1.2 with pinning. |
| Client certificate (mutual TLS) | **Not possible as planned.** `CertificateRequest.CreateSelfSigned` throws `NotImplementedException` on the Frame, so the headset cannot create a client certificate there. |
| Raw crypto in the runtime | `ECDsa.Create` + sign: **NotImplemented**. `AesGcm`: **PlatformNotSupported**. HMAC-SHA256, SHA-256, PBKDF2 (Rfc2898DeriveBytes), RandomNumberGenerator: **work**. RSA-2048 generates but my size check returned false: inconclusive, treat RSA as unusable. |
| Round trip over TLS (tiny messages, dongle link) | **avg 4.3 ms** (200 round trips), including adb-free direct TCP to 10.35.78.22. |

## What this changes in the plan
1. **Drop mutual TLS.** Keep server-certificate pinning (works), and authenticate the headset at application level.
2. **Pairing without client certificates or key agreement libraries:**
   - Pair: headset connects over TLS (server cert not yet trusted). Both screens show a 6-digit string derived from the server certificate fingerprint (numeric comparison). The user confirms it matches: this authenticates the server against a man in the middle.
   - The headset then makes a random 256-bit secret, sends it inside the TLS channel, the host stores it with a label and date, the headset stores the host's pinned fingerprint plus the secret.
   - Later connections: TLS with pinned fingerprint, then HMAC-SHA256 challenge/response over the TLS channel using the secret (host sends a fresh nonce; replays are useless). All primitives used are known to work on the Frame.
3. **Discovery:** mDNS is confirmed; keep a typed-address fallback.
4. **TLS 1.2 only.** Acceptable with ECDHE + AES-GCM; document it.
5. **If stronger crypto is wanted later** (real key agreement, signatures): ship a managed library such as BouncyCastle in the headset app; it is pure C# and independent of the Mono gaps found here. Not needed for the design above.
6. **Android manifest:** the three permissions above are required and are in the `XrssNet.androidlib` plugin.

## Not tested
Wi-Fi (non-dongle) latency; Wi-Fi power-save effect on multicast; reconnect after sleep; firewall prompt behaviour on a clean PC (the spike ran with the Windows firewall allowing the exe on this PC); IPv6.
