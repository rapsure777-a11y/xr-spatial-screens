# Pairing and authentication test vectors

Fixed inputs and expected outputs for every formula in the shared source `Assets/Scripts/Transport/Shared/AuthCrypto.cs`. They were computed with an independent implementation (Python `hashlib` and `hmac` following `PAIRING_PLAN.md`), and the same constants are asserted by the host tests (`Tools/XrssTransport.Tests`) and by the headset's Unity EditMode tests, so a change to a formula on either side fails loudly instead of silently diverging.

Inputs (bytes, shown as hex ranges):
- `secret`    = 01 02 03 ... 20 (32 bytes)
- `hostId`    = A0 A1 ... AF (16 bytes)
- `deviceId`  = B0 B1 ... BF (16 bytes)
- `challenge` = C0 C1 ... DF (32 bytes)
- `Nc`        = 40 41 ... 5F (32 bytes)
- `Nh`        = 60 61 ... 7F (32 bytes)
- `F` (certificate fingerprint) = 80 81 ... 9F (32 bytes)
- certificate raw data for the fingerprint helper = 00 01 02 ... 63 (100 bytes)

| Formula | Expected |
|---|---|
| `AuthMac(secret, hostId, deviceId, challenge)` = HMAC-SHA256(secret, "XRSS-AUTH-v1" \|\| hostId \|\| deviceId \|\| challenge) | `4B4C0724A544C9147BCA888F9ED853D779BDE968EA7AF0405911EE9DB0634D30` |
| `Commit(Nc)` = SHA-256("XRSS-SAS-commit-v1" \|\| Nc) | `CB7C0122587F929E5610667A913CBDB3446B3C08799F61688055D7FC420A419B` |
| `Sas(F, Nc, Nh, deviceId)` = first 4 bytes of SHA-256("XRSS-SAS-v1" \|\| len(F) \|\| F \|\| Nc \|\| Nh \|\| deviceId), big endian, mod 1,000,000, six digits | `165978` |
| `Fingerprint(raw)` = SHA-256(raw) | `BCE0AFF19CF5AA6A7469A30D61D04E4376E4BBF6381052EE9E7F33925C954D52` |
