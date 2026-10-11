using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrssTransport;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Tests
{
    /// <summary>A controllable clock shared by the window, the limiters and the listener.</summary>
    public sealed class FakeClock { public DateTime Now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc); public DateTime Get() => Now; public void Advance(TimeSpan t) { Now += t; } }

    public sealed class TestLog : ITransportLog
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        public void Info(string message) => Lines.Enqueue(message);
        public bool Contains(string part) => Lines.Any(l => l.Contains(part));
    }

    /// <summary>A host: identity, store and pairing window in a temp folder, plus a listener on loopback whose authenticated handler speaks a tiny stand-in for the application protocol.</summary>
    public sealed class HostFixture : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "xrss-tests-" + Guid.NewGuid().ToString("N"));
        public readonly FakeClock Clock = new FakeClock();
        public readonly TestLog Log = new TestLog();
        public readonly IProtector Protector = new DpapiProtector();
        public HostIdentity Identity;
        public DeviceStore Store;
        public PairingWindow Window;
        public SecureListener Listener;
        public RateLimiter Limits, PairLimits;
        public int AuthenticatedCount;
        public volatile string LastAuthenticatedDevice;
        public TaskCompletionSource<PairingSession> CodeReady = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostFixture()
        {
            Identity = HostIdentity.LoadOrCreate(Dir, Protector, Log);
            Store = new DeviceStore(Dir, Protector, Clock.Get);
            Window = new PairingWindow(Log, Clock.Get);
            Window.CodeReady += s => CodeReady.TrySetResult(s);
            Limits = new RateLimiter(Clock.Get); PairLimits = new RateLimiter(Clock.Get, maxFailures: 5);
            Listener = new SecureListener(Identity, Store, Window, Log, Limits, PairLimits, Clock.Get);
            Listener.OnAuthenticated = async c =>
            {
                Interlocked.Increment(ref AuthenticatedCount);
                LastAuthenticatedDevice = c.DeviceIdHex;
                // stand-in application protocol: 'W' (send me the window list) -> 'XRSL' magic; runs until the peer or the host drops the stream
                var b = new byte[1];
                while (await c.Stream.ReadAsync(b, 0, 1).ConfigureAwait(false) > 0)
                    if (b[0] == (byte)'W') { var r = Encoding.ASCII.GetBytes("XRSL"); await c.Stream.WriteAsync(r, 0, 4).ConfigureAwait(false); await c.Stream.FlushAsync().ConfigureAwait(false); }
            };
            Listener.Start(new[] { IPAddress.Loopback }, 0);
        }

        public int Port => Listener.Port;
        public TestClient NewClient() => TestClient.Connect(Port).GetAwaiter().GetResult();

        public void ResetCodeSignal() { CodeReady = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously); }

        /// <summary>Runs a complete honest pairing and returns the new device's credentials.</summary>
        public async Task<(byte[] deviceId, byte[] secret)> PairHonest(string label = "Test headset")
        {
            Window.Open();
            ResetCodeSignal();
            byte[] deviceId = AuthCrypto.Random(16), secret = AuthCrypto.Random(32);
            using (var c = await TestClient.Connect(Port))
            {
                string code = await c.BeginPair(deviceId, this);
                var hostSide = await CodeReady.Task;
                if (hostSide.Code != code) throw new Exception("codes differ in an honest pairing");
                Window.ConfirmOnPc(true);
                var result = await c.ConfirmPair(secret, label);
                if (result != MsgType.Ok) throw new Exception("pairing did not complete: " + result);
            }
            await Task.Delay(150);                                     // the pairing connection itself is promoted to the application protocol; let that settle
            return (deviceId, secret);
        }

        public void Dispose() { Listener.Dispose(); Identity.Dispose(); try { Directory.Delete(Dir, true); } catch { } }
    }

    /// <summary>A scripted headset: connects over TLS 1.2, records the certificate fingerprint it was shown, and speaks the authentication protocol with optional tampering.</summary>
    public sealed class TestClient : IDisposable
    {
        public TcpClient Tcp;
        public SslStream Ssl;
        public byte[] SeenFingerprint;
        public byte[] HostId, Nc, Nh;
        static readonly TimeSpan T = TimeSpan.FromSeconds(8);

        public static async Task<TestClient> Connect(int port, byte[] pinnedFingerprint = null, IPAddress address = null)
        {
            var c = new TestClient { Tcp = new TcpClient() };
            await c.Tcp.ConnectAsync(address ?? IPAddress.Loopback, port);
            c.Ssl = new SslStream(c.Tcp.GetStream(), false, (s, cert, ch, e) => { c.SeenFingerprint = AuthCrypto.Fingerprint(cert.GetRawCertData()); return pinnedFingerprint == null || c.SeenFingerprint.SequenceEqual(pinnedFingerprint); });
            await c.Ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "xrss-pc", EnabledSslProtocols = SslProtocols.Tls12 });
            return c;
        }

        public async Task Send(MsgType t, byte[] p = null) { using (var cts = new CancellationTokenSource(T)) await WriteAsync(Ssl, t, p, cts.Token); }
        public async Task<(MsgType type, byte[] payload)> Receive() { using (var cts = new CancellationTokenSource(T)) return await ReadAsync(Ssl, cts.Token); }

        public Task Hello(byte mode, byte[] deviceId, byte version = AuthVersion) => Send(MsgType.Hello, HelloPayload(mode, deviceId, version));

        /// <summary>Reconnect authentication. Returns the host's final answer (Ok or Reject) and the (challenge, response) pair that was sent.</summary>
        public async Task<(MsgType answer, byte[] challenge, byte[] response, byte code)> Authenticate(byte[] deviceId, byte[] secret, byte[] macHostId = null, byte[] macDeviceId = null, byte[] replayResponse = null)
        {
            await Hello(ModeAuthenticate, deviceId);
            var ch = await Receive();
            if (ch.type != MsgType.Challenge) return (ch.type, null, null, ch.payload.Length > 0 ? ch.payload[0] : (byte)0);
            HostId = ch.payload.Take(16).ToArray();
            byte[] challenge = ch.payload.Skip(16).ToArray();
            byte[] mac = replayResponse ?? AuthCrypto.AuthMac(secret, macHostId ?? HostId, macDeviceId ?? deviceId, challenge);
            await Send(MsgType.Response, mac);
            var ans = await Receive();
            return (ans.type, challenge, mac, ans.payload.Length > 0 ? ans.payload[0] : (byte)0);
        }

        /// <summary>First half of pairing: commit, receive the host nonce, reveal, compute the code. Returns the code this side computes.</summary>
        public async Task<string> BeginPair(byte[] deviceId, HostFixture host = null, byte[] fingerprintOverride = null)
        {
            await Hello(ModePair, deviceId);
            Nc = AuthCrypto.Random(32);
            await Send(MsgType.Commit, AuthCrypto.Commit(Nc));
            var hn = await Receive();
            if (hn.type != MsgType.HostNonce) throw new InvalidOperationException("expected HostNonce, got " + hn.type + (hn.payload.Length > 0 ? " code " + hn.payload[0] : ""));
            HostId = hn.payload.Take(16).ToArray(); Nh = hn.payload.Skip(16).ToArray();
            await Send(MsgType.Reveal, Nc);
            return AuthCrypto.Sas(fingerprintOverride ?? SeenFingerprint, Nc, Nh, deviceId);
        }

        public async Task<MsgType> ConfirmPair(byte[] secret, string label)
        {
            var lb = Encoding.UTF8.GetBytes(label);
            await Send(MsgType.Confirm, Concat(Concat(secret, new[] { (byte)lb.Length }), lb));
            return (await Receive()).type;
        }

        public void Dispose() { try { Tcp.Dispose(); } catch { } }
    }
}
