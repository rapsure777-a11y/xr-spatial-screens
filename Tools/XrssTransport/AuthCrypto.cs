using System;
using System.Security.Cryptography;
using System.Text;

namespace XrssTransport
{
    /// <summary>
    /// Every cryptographic computation of the pairing and authentication protocol, in one small file. Only SHA-256, HMAC-SHA256, the secure RNG and a constant-time compare are used:
    /// exactly the primitives proven to work on the Steam Frame (see Docs/PAIRING_SPIKE_RESULTS.md), so the headset side can use the same formulas.
    /// </summary>
    public static class AuthCrypto
    {
        public const int IdLength = 16, SecretLength = 32, ChallengeLength = 32, NonceLength = 32, MacLength = 32;

        public static byte[] Random(int n) { var b = new byte[n]; RandomNumberGenerator.Fill(b); return b; }

        /// <summary>HMAC-SHA256(secret, "XRSS-AUTH-v1" || host-id || device-id || challenge).</summary>
        public static byte[] AuthMac(byte[] secret, byte[] hostId, byte[] deviceId, byte[] challenge)
        {
            using (var h = new HMACSHA256(secret))
            {
                var label = Encoding.ASCII.GetBytes("XRSS-AUTH-v1");
                h.TransformBlock(label, 0, label.Length, null, 0);
                h.TransformBlock(hostId, 0, hostId.Length, null, 0);
                h.TransformBlock(deviceId, 0, deviceId.Length, null, 0);
                h.TransformFinalBlock(challenge, 0, challenge.Length);
                return h.Hash;
            }
        }

        public static bool MacEquals(byte[] a, byte[] b) => a != null && b != null && a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);

        /// <summary>SAS commitment: SHA-256("XRSS-SAS-commit-v1" || Nc). Sent by the headset before it sees the host's nonce.</summary>
        public static byte[] Commit(byte[] nc) => Sha("XRSS-SAS-commit-v1", nc);

        /// <summary>The six-digit comparison code: first 4 bytes of SHA-256("XRSS-SAS-v1" || len(F) || F || Nc || Nh || deviceId) as an unsigned integer mod 1,000,000.</summary>
        public static string Sas(byte[] certFingerprint, byte[] nc, byte[] nh, byte[] deviceId)
        {
            var len = new[] { (byte)certFingerprint.Length };
            byte[] hash = Sha("XRSS-SAS-v1", len, certFingerprint, nc, nh, deviceId);
            uint v = (uint)(hash[0] << 24 | hash[1] << 16 | hash[2] << 8 | hash[3]);
            return (v % 1000000u).ToString("D6");
        }

        public static byte[] Fingerprint(byte[] certificateRawData) => SHA256.HashData(certificateRawData);

        static byte[] Sha(string label, params byte[][] parts)
        {
            using (var h = SHA256.Create())
            {
                var l = Encoding.ASCII.GetBytes(label);
                h.TransformBlock(l, 0, l.Length, null, 0);
                for (int i = 0; i < parts.Length; i++)
                    if (i == parts.Length - 1) h.TransformFinalBlock(parts[i], 0, parts[i].Length); else h.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                return h.Hash;
            }
        }

        public static string Hex(byte[] b) => Convert.ToHexString(b);
        public static byte[] FromHex(string s) => Convert.FromHexString(s);
    }
}
