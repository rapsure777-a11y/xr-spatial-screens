using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XrssTransport
{
    /// <summary>
    /// The authentication protocol that runs inside TLS before the application protocol (XRS2/XRSL/...). Its version is separate from any application feature version.
    /// Frame: type u8, payload length u16 (little endian), payload. Payloads are small and bounded; nothing here carries application data.
    ///
    ///   client -> host  Hello      "XRAU", authVersion u8, mode u8 (1 authenticate, 2 pair), deviceId[16]
    ///   mode 1:  host -> client Challenge  hostId[16], challenge[32]      client -> host Response  mac[32] = HMAC-SHA256(secret, "XRSS-AUTH-v1" || hostId || deviceId || challenge)      host -> client Ok
    ///   mode 2:  client -> host Commit     C[32] = SHA-256("XRSS-SAS-commit-v1" || Nc)
    ///            host -> client HostNonce  hostId[16], Nh[32]             client -> host Reveal   Nc[32]
    ///            (both show the six-digit code; the user confirms on both)
    ///            client -> host Confirm    secret[32], labelLength u8, label (UTF-8)   or   Cancel      host -> client Ok
    ///   any time: host -> client Reject  code u8, supportedAuthVersion u8
    /// After Ok the same stream carries the application protocol unchanged.
    /// </summary>
    public static class AuthProtocol
    {
        public const byte AuthVersion = 1;
        public const byte ModeAuthenticate = 1, ModePair = 2;
        public const int MaxPayload = 512;
        public static readonly byte[] Magic = { (byte)'X', (byte)'R', (byte)'A', (byte)'U' };

        public enum MsgType : byte { Hello = 1, Challenge = 2, Response = 3, Ok = 4, Reject = 5, Commit = 6, HostNonce = 7, Reveal = 8, Confirm = 9, Cancel = 10 }
        public enum RejectCode : byte { UnsupportedVersion = 1, AuthFailed = 2, PairingClosed = 3, Busy = 4, RateLimited = 5, ProtocolError = 6, PairingFailed = 7, Timeout = 8 }

        public static async Task WriteAsync(Stream s, MsgType type, byte[] payload, CancellationToken ct)
        {
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length > MaxPayload) throw new InvalidOperationException("payload too large");
            var buf = new byte[3 + payload.Length];
            buf[0] = (byte)type; buf[1] = (byte)payload.Length; buf[2] = (byte)(payload.Length >> 8);
            Buffer.BlockCopy(payload, 0, buf, 3, payload.Length);
            await s.WriteAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<(MsgType type, byte[] payload)> ReadAsync(Stream s, CancellationToken ct)
        {
            var h = new byte[3];
            await ReadExactAsync(s, h, ct).ConfigureAwait(false);
            int len = h[1] | h[2] << 8;
            if (len > MaxPayload) throw new InvalidDataException("auth message too large");
            var p = new byte[len];
            if (len > 0) await ReadExactAsync(s, p, ct).ConfigureAwait(false);
            return ((MsgType)h[0], p);
        }

        static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int r = await s.ReadAsync(buf, got, buf.Length - got, ct).ConfigureAwait(false);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
        }

        public static byte[] RejectPayload(RejectCode code) => new[] { (byte)code, AuthVersion };

        public static byte[] HelloPayload(byte mode, byte[] deviceId, byte version = AuthVersion)
        {
            var p = new byte[4 + 1 + 1 + AuthCrypto.IdLength];
            Buffer.BlockCopy(Magic, 0, p, 0, 4); p[4] = version; p[5] = mode; Buffer.BlockCopy(deviceId, 0, p, 6, AuthCrypto.IdLength);
            return p;
        }

        public static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }
    }
}
