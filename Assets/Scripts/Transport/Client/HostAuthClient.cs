using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Client
{
    public enum AuthStatus { Ok, Rejected, HostMismatch, ProtocolError, Timeout }

    public sealed class AuthOutcome
    {
        public AuthStatus Status;
        public RejectCode Reject;            // valid when Status == Rejected
        public long Ms;
        public override string ToString() => Status + (Status == AuthStatus.Rejected ? " " + Reject : "");
    }

    /// <summary>
    /// The headset's side of the authentication protocol (formulas: shared AuthCrypto, framing: shared AuthProtocol). Runs on a connection that finished TLS and hands nothing to the application
    /// protocol itself: on <see cref="AuthStatus.Ok"/> the caller owns an authenticated stream. Blocking calls for worker threads, each read with a timeout.
    /// </summary>
    public static class HostAuthClient
    {
        public static AuthOutcome Authenticate(SecureConnection c, PairedHost host, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] secret = null;
            try
            {
                Send(c, MsgType.Hello, HelloPayload(ModeAuthenticate, host.DeviceId), timeoutMs);
                var ch = Receive(c, timeoutMs);
                if (ch.type == MsgType.Reject) return Rejected(ch.payload, sw);
                if (ch.type != MsgType.Challenge || ch.payload.Length != AuthCrypto.IdLength + AuthCrypto.ChallengeLength) return new AuthOutcome { Status = AuthStatus.ProtocolError, Ms = sw.ElapsedMilliseconds };
                byte[] hostId = ch.payload.Take(AuthCrypto.IdLength).ToArray(), challenge = ch.payload.Skip(AuthCrypto.IdLength).ToArray();
                if (!AuthCrypto.MacEquals(hostId, host.HostId)) return new AuthOutcome { Status = AuthStatus.HostMismatch, Ms = sw.ElapsedMilliseconds };       // never answer a different PC
                secret = (byte[])host.Secret.Clone();
                Send(c, MsgType.Response, AuthCrypto.AuthMac(secret, hostId, host.DeviceId, challenge), timeoutMs);
                var ans = Receive(c, timeoutMs);
                if (ans.type == MsgType.Ok) return new AuthOutcome { Status = AuthStatus.Ok, Ms = sw.ElapsedMilliseconds };
                return ans.type == MsgType.Reject ? Rejected(ans.payload, sw) : new AuthOutcome { Status = AuthStatus.ProtocolError, Ms = sw.ElapsedMilliseconds };
            }
            catch (TimeoutException) { return new AuthOutcome { Status = AuthStatus.Timeout, Ms = sw.ElapsedMilliseconds }; }
            catch (IOException) { return new AuthOutcome { Status = AuthStatus.ProtocolError, Ms = sw.ElapsedMilliseconds }; }
            finally { if (secret != null) Array.Clear(secret, 0, secret.Length); }
        }

        static AuthOutcome Rejected(byte[] payload, System.Diagnostics.Stopwatch sw) =>
            new AuthOutcome { Status = AuthStatus.Rejected, Reject = payload.Length > 0 ? (RejectCode)payload[0] : RejectCode.ProtocolError, Ms = sw.ElapsedMilliseconds };

        internal static void Send(SecureConnection c, MsgType t, byte[] payload, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource())
            {
                var task = WriteAsync(c.Ssl, t, payload, cts.Token);
                if (!SecureChannel.Wait(task, timeoutMs)) { c.Dispose(); throw new TimeoutException("send timed out"); }
                task.GetAwaiter().GetResult();
            }
        }

        internal static (MsgType type, byte[] payload) Receive(SecureConnection c, int timeoutMs)
        {
            using (var cts = new CancellationTokenSource())
            {
                var task = ReadAsync(c.Ssl, cts.Token);
                if (!SecureChannel.Wait(task, timeoutMs)) { c.Dispose(); throw new TimeoutException("receive timed out"); }
                return task.GetAwaiter().GetResult();
            }
        }
    }

    public enum PairStatus { CodeReady, Rejected, ProtocolError, Timeout }

    /// <summary>
    /// The headset's side of the pairing ceremony described in Docs/PAIRING_PLAN.md ("Pairing ceremony (final)"): commit, receive the host nonce, reveal, compute the six-digit code from the
    /// certificate fingerprint this connection actually presented, then, ONLY after the user confirmed, send the new device secret. Nothing is stored here: the caller persists trust after <see cref="Finish"/> returns true.
    /// </summary>
    public sealed class PairingCeremony
    {
        readonly SecureConnection m_Conn;
        readonly HeadsetIdentity m_Identity;
        byte[] m_HostId;
        public string Code { get; private set; }
        public RejectCode Reject { get; private set; }
        public byte[] HostId => m_HostId;
        public byte[] Secret { get; private set; }

        public PairingCeremony(SecureConnection conn, HeadsetIdentity identity) { m_Conn = conn; m_Identity = identity; }

        /// <summary>Steps 2 to 5: Hello(pair), commitment, host nonce, reveal, code. Returns CodeReady with <see cref="Code"/> set, or why not (for example the PC's pairing window is closed).</summary>
        public PairStatus Begin(int timeoutMs)
        {
            byte[] nc = null;
            try
            {
                HostAuthClient.Send(m_Conn, MsgType.Hello, HelloPayload(ModePair, m_Identity.DeviceId), timeoutMs);
                nc = AuthCrypto.Random(AuthCrypto.NonceLength);
                HostAuthClient.Send(m_Conn, MsgType.Commit, AuthCrypto.Commit(nc), timeoutMs);
                var hn = HostAuthClient.Receive(m_Conn, timeoutMs);
                if (hn.type == MsgType.Reject) { Reject = hn.payload.Length > 0 ? (RejectCode)hn.payload[0] : RejectCode.ProtocolError; return PairStatus.Rejected; }
                if (hn.type != MsgType.HostNonce || hn.payload.Length != AuthCrypto.IdLength + AuthCrypto.NonceLength) return PairStatus.ProtocolError;
                m_HostId = hn.payload.Take(AuthCrypto.IdLength).ToArray();
                byte[] nh = hn.payload.Skip(AuthCrypto.IdLength).ToArray();
                HostAuthClient.Send(m_Conn, MsgType.Reveal, nc, timeoutMs);
                Code = AuthCrypto.Sas(m_Conn.PresentedFingerprint, nc, nh, m_Identity.DeviceId);
                return PairStatus.CodeReady;
            }
            catch (TimeoutException) { return PairStatus.Timeout; }
            catch (IOException) { return PairStatus.ProtocolError; }
            finally { if (nc != null) Array.Clear(nc, 0, nc.Length); }
        }

        /// <summary>Step 7. With <paramref name="match"/> false the user said the codes differ: tell the PC and store nothing. With true: send a fresh random 32-byte secret and wait (up to
        /// <paramref name="waitForPcMs"/>, the PC user must confirm too) for the PC's enrollment answer. True only when the PC answered Ok.</summary>
        public bool Finish(bool match, string label, int waitForPcMs)
        {
            try
            {
                if (!match) { HostAuthClient.Send(m_Conn, MsgType.Cancel, null, 5000); return false; }
                byte[] secret = AuthCrypto.Random(AuthCrypto.SecretLength);
                var lb = Encoding.UTF8.GetBytes(label ?? "Headset"); if (lb.Length > 48) lb = lb.Take(48).ToArray();
                HostAuthClient.Send(m_Conn, MsgType.Confirm, Concat(Concat(secret, new[] { (byte)lb.Length }), lb), 5000);
                var ans = HostAuthClient.Receive(m_Conn, waitForPcMs);
                if (ans.type == MsgType.Ok) { Secret = secret; return true; }
                Array.Clear(secret, 0, secret.Length);
                Reject = ans.type == MsgType.Reject && ans.payload.Length > 0 ? (RejectCode)ans.payload[0] : RejectCode.ProtocolError;
                return false;
            }
            catch (TimeoutException) { Reject = RejectCode.Timeout; return false; }
            catch (IOException) { Reject = RejectCode.ProtocolError; return false; }
        }
    }
}
