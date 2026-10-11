using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static XrssTransport.AuthProtocol;

namespace XrssTransport
{
    /// <summary>A connection that finished TLS and authentication. The stream carries the unchanged application protocol from here on.</summary>
    public sealed class AuthenticatedConnection
    {
        public Stream Stream { get; internal set; }
        public TcpClient Client { get; internal set; }
        public string DeviceIdHex { get; internal set; }
        public string Label { get; internal set; }
        public string RemoteAddress { get; internal set; }
    }

    /// <summary>
    /// The consumer (LAN) listener. Each connection moves through
    /// <c>Connected -> TLS established -> Authentication pending -> Authenticated</c>, and only the last state is handed to the application protocol through <see cref="OnAuthenticated"/>.
    /// Before that, the only bytes the host reads or writes are the small authentication messages of <see cref="AuthProtocol"/>; any other first bytes end the connection. TLS is 1.2 with the
    /// host's self-signed ECDSA certificate (the headset pins its fingerprint; no client certificate, because the Frame cannot make one).
    /// This class and its neighbours are the whole of the transport/auth layer: capture, input, audio, cursor and panel code never see certificates, secrets or pairing codes.
    /// </summary>
    public sealed class SecureListener : IDisposable
    {
        public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
        static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);
        const SslProtocols TlsVersions = SslProtocols.Tls12;

        readonly HostIdentity m_Identity;
        readonly DeviceStore m_Store;
        readonly PairingWindow m_Window;
        readonly RateLimiter m_Limits, m_PairLimits;
        readonly ITransportLog m_Log;
        readonly Func<DateTime> m_Now;
        readonly object m_Lock = new object();
        readonly List<TcpListener> m_Listeners = new List<TcpListener>();
        readonly CancellationTokenSource m_Stop = new CancellationTokenSource();
        readonly ConcurrentDictionary<string, DateTime> m_Issued = new ConcurrentDictionary<string, DateTime>();
        readonly List<AuthenticatedConnection> m_Live = new List<AuthenticatedConnection>();
        Timer m_Timer;
        IReadOnlyList<IPAddress> m_Addresses; int m_ManagedPort;

        /// <summary>Called on a worker thread for each authenticated connection; the connection ends when this returns.</summary>
        public Func<AuthenticatedConnection, Task> OnAuthenticated { get; set; }
        public int Port { get; private set; }

        public SecureListener(HostIdentity identity, DeviceStore store, PairingWindow window, ITransportLog log = null, RateLimiter limits = null, RateLimiter pairLimits = null, Func<DateTime> now = null)
        {
            m_Identity = identity; m_Store = store; m_Window = window; m_Log = log ?? new NullTransportLog(); m_Now = now ?? (() => DateTime.UtcNow);
            m_Limits = limits ?? new RateLimiter(now); m_PairLimits = pairLimits ?? new RateLimiter(now, maxFailures: 5);
            m_Store.Removed += DropDevice;
        }

        // ------------------------------------------------------------------ binding

        /// <summary>Binds now (tests, or an explicit start). Use <see cref="StartManaged"/> for the product behaviour (bound only while something is paired or a window is open).</summary>
        public void Start(IEnumerable<IPAddress> addresses, int port)
        {
            lock (m_Lock)
            {
                foreach (var a in addresses)
                {
                    var l = new TcpListener(a, port); l.Start(); m_Listeners.Add(l);
                    if (Port == 0) Port = ((IPEndPoint)l.LocalEndpoint).Port;
                    m_Log.Info("lan listener bound to " + a + ":" + ((IPEndPoint)l.LocalEndpoint).Port);
                    _ = AcceptLoop(l);
                }
            }
        }

        /// <summary>Product behaviour: listen on the given interfaces only while at least one device is paired or a pairing window is open; otherwise nothing is exposed.</summary>
        public void StartManaged(IReadOnlyList<IPAddress> addresses, int port)
        {
            m_Addresses = addresses; m_ManagedPort = port;
            m_Store.Changed += Refresh; m_Window.Changed += Refresh;
            m_Timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Refresh();
        }

        void Refresh()
        {
            if (m_Stop.IsCancellationRequested || m_Addresses == null) return;
            bool want = m_Store.Count > 0 || m_Window.IsOpen;
            lock (m_Lock)
            {
                if (want && m_Listeners.Count == 0)
                {
                    foreach (var a in m_Addresses)
                        try { var l = new TcpListener(a, m_ManagedPort); l.Start(); m_Listeners.Add(l); Port = ((IPEndPoint)l.LocalEndpoint).Port; m_Log.Info("lan listener bound to " + a + ":" + Port); _ = AcceptLoop(l); }
                        catch (Exception e) { m_Log.Info("lan listener could not bind " + a + ": " + e.Message); }
                }
                else if (!want && m_Listeners.Count > 0)
                {
                    foreach (var l in m_Listeners) l.Stop();
                    m_Listeners.Clear(); Port = 0;
                    m_Log.Info("lan listener closed (nothing paired, pairing closed)");
                }
            }
        }

        public bool IsListening { get { lock (m_Lock) return m_Listeners.Count > 0; } }

        async Task AcceptLoop(TcpListener l)
        {
            while (!m_Stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => Handle(c));
            }
        }

        // ------------------------------------------------------------------ one connection

        async Task Handle(TcpClient tcp)
        {
            string ip = "?";
            AuthenticatedConnection live = null;
            try
            {
                ip = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString();
                if (!m_Limits.AllowConnection(ip)) { m_Log.Info("connection refused (rate limit) from " + ip); return; }
                tcp.NoDelay = true;
                SslStream ssl = new SslStream(tcp.GetStream(), false);
                using (var cts = Step())
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = m_Identity.Certificate, ClientCertificateRequired = false, EnabledSslProtocols = TlsVersions }, cts.Token).ConfigureAwait(false);

                // TLS established, authentication pending: nothing but auth messages is understood from here
                (MsgType type, byte[] p) hello;
                using (var cts = Step()) hello = await ReadAsync(ssl, cts.Token).ConfigureAwait(false);
                if (hello.type != MsgType.Hello || hello.p.Length != 4 + 2 + AuthCrypto.IdLength || !hello.p.Take(4).SequenceEqual(Magic))
                {
                    m_Log.Info("refused: not an authentication hello from " + ip);
                    m_Limits.Failure(ip);
                    await Reject(ssl, RejectCode.ProtocolError).ConfigureAwait(false);
                    return;
                }
                byte version = hello.p[4], mode = hello.p[5];
                byte[] deviceId = hello.p.Skip(6).ToArray();
                string deviceHex = AuthCrypto.Hex(deviceId);
                if (version != AuthVersion)
                {
                    m_Log.Info("refused: unsupported auth protocol version " + version + " from " + ip);
                    await Reject(ssl, RejectCode.UnsupportedVersion).ConfigureAwait(false);
                    return;
                }
                if (mode == ModeAuthenticate) live = await Authenticate(ssl, tcp, ip, deviceId, deviceHex).ConfigureAwait(false);
                else if (mode == ModePair) live = await Pair(ssl, tcp, ip, deviceId, deviceHex).ConfigureAwait(false);
                else { m_Log.Info("refused: unknown mode " + mode + " from " + ip); await Reject(ssl, RejectCode.ProtocolError).ConfigureAwait(false); }

                if (live != null)
                {
                    lock (m_Live) m_Live.Add(live);
                    var handler = OnAuthenticated;
                    if (handler != null) await handler(live).ConfigureAwait(false);
                }
            }
            catch (Exception e) { m_Log.Info("connection from " + ip + " ended: " + e.GetType().Name); }
            finally
            {
                if (live != null) lock (m_Live) m_Live.Remove(live);
                try { tcp.Dispose(); } catch { }
            }
        }

        static CancellationTokenSource Step() => new CancellationTokenSource(StepTimeout);

        static async Task Reject(Stream s, RejectCode code)
        {
            try { using (var cts = Step()) await WriteAsync(s, MsgType.Reject, RejectPayload(code), cts.Token).ConfigureAwait(false); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ reconnect authentication

        async Task<AuthenticatedConnection> Authenticate(SslStream ssl, TcpClient tcp, string ip, byte[] deviceId, string deviceHex)
        {
            byte[] challenge = AuthCrypto.Random(AuthCrypto.ChallengeLength);
            IssueChallenge(challenge);
            using (var cts = Step()) await WriteAsync(ssl, MsgType.Challenge, Concat(m_Identity.HostId, challenge), cts.Token).ConfigureAwait(false);
            (MsgType type, byte[] p) resp;
            using (var cts = Step()) resp = await ReadAsync(ssl, cts.Token).ConfigureAwait(false);

            // an unknown device is checked against a random dummy secret, so unknown and wrong-secret look the same, in timing too
            byte[] secret = m_Store.TryGetSecret(deviceHex, m_Identity.HostIdHex);
            bool known = secret != null;
            if (!known) secret = AuthCrypto.Random(AuthCrypto.SecretLength);
            byte[] expected = AuthCrypto.AuthMac(secret, m_Identity.HostId, deviceId, challenge);
            Array.Clear(secret, 0, secret.Length);
            bool fresh = TakeChallenge(challenge);                                  // a challenge can be answered once
            bool ok = resp.type == MsgType.Response && AuthCrypto.MacEquals(expected, resp.p) && known && fresh;
            if (!ok)
            {
                m_Limits.Failure(ip);
                m_Log.Info("authentication failed for device " + LogIds.Short(deviceHex) + " from " + ip);
                await Reject(ssl, RejectCode.AuthFailed).ConfigureAwait(false);
                return null;
            }
            m_Limits.Success(ip);
            m_Store.MarkConnected(deviceHex);
            using (var cts = Step()) await WriteAsync(ssl, MsgType.Ok, null, cts.Token).ConfigureAwait(false);
            m_Log.Info("device " + LogIds.Short(deviceHex) + " authenticated from " + ip);
            return new AuthenticatedConnection { Stream = ssl, Client = tcp, DeviceIdHex = deviceHex, RemoteAddress = ip };
        }

        void IssueChallenge(byte[] c)
        {
            var now = m_Now();
            foreach (var kv in m_Issued) if (now - kv.Value > ChallengeLifetime) m_Issued.TryRemove(kv.Key, out _);
            m_Issued[AuthCrypto.Hex(c)] = now;
        }

        /// <summary>True the first time a still-valid issued challenge is answered; false for an unknown, expired or already used one.</summary>
        internal bool TakeChallenge(byte[] c) => m_Issued.TryRemove(AuthCrypto.Hex(c), out var at) && m_Now() - at <= ChallengeLifetime;

        internal byte[] IssueChallengeForTest() { var c = AuthCrypto.Random(AuthCrypto.ChallengeLength); IssueChallenge(c); return c; }

        // ------------------------------------------------------------------ enrollment

        async Task<AuthenticatedConnection> Pair(SslStream ssl, TcpClient tcp, string ip, byte[] deviceId, string deviceHex)
        {
            if (m_PairLimits.IsLockedOut(ip) || !m_PairLimits.AllowConnection(ip)) { m_Log.Info("pairing refused (rate limit) from " + ip); await Reject(ssl, RejectCode.RateLimited).ConfigureAwait(false); return null; }
            PairingSession session = m_Window.TryBegin(ip);
            if (session == null)
            {
                m_PairLimits.Failure(ip);
                m_Log.Info("pairing refused: window " + (m_Window.IsOpen ? "busy" : "closed") + ", from " + ip);
                await Reject(ssl, m_Window.IsOpen ? RejectCode.Busy : RejectCode.PairingClosed).ConfigureAwait(false);
                return null;
            }
            byte[] secret = null;
            try
            {
                (MsgType type, byte[] p) m;
                using (var cts = Step()) m = await ReadAsync(ssl, cts.Token).ConfigureAwait(false);
                if (m.type != MsgType.Commit || m.p.Length != 32) return await PairFail(ssl, session, ip, "bad commitment");
                byte[] commit = m.p;
                byte[] nh = AuthCrypto.Random(AuthCrypto.NonceLength);
                using (var cts = Step()) await WriteAsync(ssl, MsgType.HostNonce, Concat(m_Identity.HostId, nh), cts.Token).ConfigureAwait(false);
                using (var cts = Step()) m = await ReadAsync(ssl, cts.Token).ConfigureAwait(false);
                if (m.type != MsgType.Reveal || m.p.Length != 32 || !AuthCrypto.MacEquals(AuthCrypto.Commit(m.p), commit)) return await PairFail(ssl, session, ip, "commitment mismatch");
                byte[] nc = m.p;

                string code = AuthCrypto.Sas(m_Identity.Fingerprint, nc, nh, deviceId);
                m_Window.PublishCode(session, deviceHex, code);
                m_Log.Info("pairing code ready for device " + LogIds.Short(deviceHex) + " (shown on the PC; the code itself is not logged)");

                // the headset user confirms (Confirm carries the new secret) or cancels; the PC user confirms through PairingWindow.ConfirmOnPc
                var clientTask = ReadUntil(ssl);
                if (!await WaitFor(clientTask, session)) return await PairFail(ssl, session, ip, "timed out waiting for the headset");
                m = clientTask.Result;
                if (m.type == MsgType.Cancel) return await PairFail(ssl, session, ip, "headset reported a different code");
                if (m.type != MsgType.Confirm || m.p.Length < 33) return await PairFail(ssl, session, ip, "bad confirmation");
                secret = m.p.Take(AuthCrypto.SecretLength).ToArray();
                int labelLen = m.p[AuthCrypto.SecretLength];
                string label = SanitizeLabel(m.p.Length >= 33 + labelLen ? Encoding.UTF8.GetString(m.p, 33, labelLen) : "");

                if (!await WaitFor(session.PcDecision, session)) return await PairFail(ssl, session, ip, "timed out waiting for the PC user");
                if (!session.PcDecision.Result) return await PairFail(ssl, session, ip, "PC user said the codes differ");
                if (!m_Window.IsCurrent(session)) return await PairFail(ssl, session, ip, "pairing window closed");

                m_Store.Enroll(deviceHex, label, m_Identity.HostIdHex, secret);
                session.Complete();
                m_PairLimits.Success(ip);
                m_Log.Info("device " + LogIds.Short(deviceHex) + " paired from " + ip + " as \"" + label + "\"");
                using (var cts = Step()) await WriteAsync(ssl, MsgType.Ok, null, cts.Token).ConfigureAwait(false);
                return new AuthenticatedConnection { Stream = ssl, Client = tcp, DeviceIdHex = deviceHex, Label = label, RemoteAddress = ip };
            }
            catch (Exception e)
            {
                m_Log.Info("pairing connection ended: " + e.GetType().Name);
                return null;
            }
            finally
            {
                if (secret != null) Array.Clear(secret, 0, secret.Length);
                session.Abandon();                                                  // no-op after Fail/Complete; counts as a failure only if a code had been shown
            }
        }

        async Task<AuthenticatedConnection> PairFail(Stream ssl, PairingSession session, string ip, string why)
        {
            session.Fail(why);
            m_PairLimits.Failure(ip);
            await Reject(ssl, RejectCode.PairingFailed).ConfigureAwait(false);
            return null;
        }

        async Task<(MsgType type, byte[] payload)> ReadUntil(Stream s)
        {
            using (var cts = new CancellationTokenSource(PairingWindow.DefaultDuration + TimeSpan.FromSeconds(5)))
                return await ReadAsync(s, cts.Token).ConfigureAwait(false);
        }

        /// <summary>Waits for a task while the ceremony is still current (window open and this ceremony active). False if the window closed or the ceremony ended first.</summary>
        async Task<bool> WaitFor(Task t, PairingSession session)
        {
            while (!t.IsCompleted)
            {
                if (!m_Window.IsCurrent(session) || m_Stop.IsCancellationRequested) return false;
                await Task.WhenAny(t, Task.Delay(100)).ConfigureAwait(false);
            }
            return !t.IsFaulted && !t.IsCanceled;
        }

        static string SanitizeLabel(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) { if (!char.IsControl(c) && sb.Length < 48) sb.Append(c); }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "Headset" : r;
        }

        // ------------------------------------------------------------------ revocation

        /// <summary>Drops every live connection of a device (called when it is removed from the store).</summary>
        public void DropDevice(string deviceIdHex)
        {
            List<AuthenticatedConnection> drop;
            lock (m_Live) drop = m_Live.Where(c => string.Equals(c.DeviceIdHex, deviceIdHex, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var c in drop) { m_Log.Info("dropping connection of removed device " + LogIds.Short(deviceIdHex)); try { c.Client.Dispose(); } catch { } }
        }

        public int LiveConnections { get { lock (m_Live) return m_Live.Count; } }

        public void Dispose()
        {
            m_Stop.Cancel();
            m_Timer?.Dispose();
            lock (m_Lock) { foreach (var l in m_Listeners) l.Stop(); m_Listeners.Clear(); }
            m_Store.Removed -= DropDevice;
            lock (m_Live) foreach (var c in m_Live) try { c.Client.Dispose(); } catch { }
        }
    }
}
