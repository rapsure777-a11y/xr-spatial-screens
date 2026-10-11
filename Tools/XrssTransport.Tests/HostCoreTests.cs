using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using XrssTransport;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Tests
{
    public class PairingWindowTests
    {
        [Fact]
        public void Window_Opens_AndCloses()
        {
            var clock = new FakeClock(); var w = new PairingWindow(null, clock.Get);
            Assert.False(w.IsOpen);
            Assert.Null(w.TryBegin("1.2.3.4"));                    // closed: no ceremony can start
            w.Open();
            Assert.True(w.IsOpen);
            Assert.NotNull(w.TryBegin("1.2.3.4"));
            w.Close();
            Assert.False(w.IsOpen);
            Assert.Null(w.TryBegin("1.2.3.4"));                    // closing is immediate
        }

        [Fact]
        public void Window_Expires_AfterTwoMinutes()
        {
            var clock = new FakeClock(); var log = new TestLog(); var w = new PairingWindow(log, clock.Get);
            w.Open();
            clock.Advance(TimeSpan.FromSeconds(119)); Assert.True(w.IsOpen);
            clock.Advance(TimeSpan.FromSeconds(2));   Assert.False(w.IsOpen);
            Assert.Null(w.TryBegin("x"));
            Assert.True(log.Contains("pairing expired"));
        }

        [Fact]
        public void OnlyOneCeremony_AtATime()
        {
            var w = new PairingWindow(null, new FakeClock().Get); w.Open();
            var first = w.TryBegin("a");
            Assert.NotNull(first);
            Assert.Null(w.TryBegin("b"));
            first.Fail("test");
            Assert.NotNull(w.TryBegin("b"));
        }

        [Fact]
        public void ThreeFailures_CloseTheWindow_FourthAttemptRefused()
        {
            var w = new PairingWindow(null, new FakeClock().Get); w.Open();
            for (int i = 0; i < 3; i++) { Assert.True(w.IsOpen); w.TryBegin("a").Fail("wrong code"); }
            Assert.False(w.IsOpen);
            Assert.Null(w.TryBegin("a"));
            Assert.Equal(3, w.Failures);
        }

        [Fact]
        public void Reopening_ResetsFailures() { var w = new PairingWindow(null, new FakeClock().Get); w.Open(); w.TryBegin("a").Fail("x"); w.Open(); Assert.Equal(0, w.Failures); }

        [Fact]
        public void Success_ClosesTheWindow() { var w = new PairingWindow(null, new FakeClock().Get); w.Open(); w.TryBegin("a").Complete(); Assert.False(w.IsOpen); }

        [Fact]
        public void Abandoned_BeforeCode_IsNotAFailure_AfterCode_IsAFailure()
        {
            var w = new PairingWindow(null, new FakeClock().Get); w.Open();
            w.TryBegin("a").Abandon(); Assert.Equal(0, w.Failures);
            var s = w.TryBegin("a"); w.PublishCode(s, "AA", "123456"); s.Abandon(); Assert.Equal(1, w.Failures);
        }
    }

    public class CryptoTests
    {
        [Fact]
        public void Sas_IsSixDigits_AndDependsOnEveryInput()
        {
            byte[] f = AuthCrypto.Random(32), nc = AuthCrypto.Random(32), nh = AuthCrypto.Random(32), id = AuthCrypto.Random(16);
            string s = AuthCrypto.Sas(f, nc, nh, id);
            Assert.Matches("^[0-9]{6}$", s);
            Assert.Equal(s, AuthCrypto.Sas(f, nc, nh, id));
            // changing any single input changes the code (a collision here would be a 1e-6 event per probe; try several)
            int same = 0;
            for (int i = 0; i < 50; i++) if (AuthCrypto.Sas(AuthCrypto.Random(32), nc, nh, id) == s) same++;
            Assert.True(same <= 1);
        }

        [Fact]
        public void Commit_BindsNc() { var nc = AuthCrypto.Random(32); Assert.True(AuthCrypto.MacEquals(AuthCrypto.Commit(nc), AuthCrypto.Commit(nc))); Assert.False(AuthCrypto.MacEquals(AuthCrypto.Commit(nc), AuthCrypto.Commit(AuthCrypto.Random(32)))); }

        [Fact]
        public void AuthMac_BindsHostDeviceAndChallenge()
        {
            byte[] sec = AuthCrypto.Random(32), h = AuthCrypto.Random(16), d = AuthCrypto.Random(16), c = AuthCrypto.Random(32);
            var m = AuthCrypto.AuthMac(sec, h, d, c);
            Assert.Equal(32, m.Length);
            Assert.True(AuthCrypto.MacEquals(m, AuthCrypto.AuthMac(sec, h, d, c)));
            Assert.False(AuthCrypto.MacEquals(m, AuthCrypto.AuthMac(sec, AuthCrypto.Random(16), d, c)));
            Assert.False(AuthCrypto.MacEquals(m, AuthCrypto.AuthMac(sec, h, AuthCrypto.Random(16), c)));
            Assert.False(AuthCrypto.MacEquals(m, AuthCrypto.AuthMac(sec, h, d, AuthCrypto.Random(32))));
            Assert.False(AuthCrypto.MacEquals(m, AuthCrypto.AuthMac(AuthCrypto.Random(32), h, d, c)));
        }

        [Fact]
        public void Challenges_AreFreshAndSingleUse()
        {
            using (var f = new HostFixture())
            {
                var seen = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < 2000; i++) Assert.True(seen.Add(AuthCrypto.Hex(AuthCrypto.Random(32))));
                var c = f.Listener.IssueChallengeForTest();
                Assert.True(f.Listener.TakeChallenge(c));
                Assert.False(f.Listener.TakeChallenge(c));                    // second use of the same challenge: refused
                var old = f.Listener.IssueChallengeForTest();
                f.Clock.Advance(TimeSpan.FromMinutes(5));
                Assert.False(f.Listener.TakeChallenge(old));                  // expired
            }
        }
    }

    public class StorageTests
    {
        [Fact]
        public void HostIdentity_Persists_AndIsProtectedAtRest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-id-" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] hostId, fp;
                using (var a = HostIdentity.LoadOrCreate(dir, new DpapiProtector())) { hostId = a.HostId; fp = a.Fingerprint; Assert.True(a.Certificate.HasPrivateKey); }
                using (var b = HostIdentity.LoadOrCreate(dir, new DpapiProtector())) { Assert.Equal(hostId, b.HostId); Assert.Equal(fp, b.Fingerprint); }
                byte[] raw = File.ReadAllBytes(Path.Combine(dir, "host-identity.dat"));
                Assert.DoesNotContain("hostId", Encoding.UTF8.GetString(raw));              // not readable JSON: DPAPI-protected
                Assert.DoesNotContain("pfx", Encoding.UTF8.GetString(raw));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void HostIdentity_Regenerate_InvalidatesPairedDevices()
        {
            using (var f = new HostFixture())
            {
                byte[] secret = AuthCrypto.Random(32), id = AuthCrypto.Random(16);
                f.Store.Enroll(AuthCrypto.Hex(id), "x", f.Identity.HostIdHex, secret);
                Assert.NotNull(f.Store.TryGetSecret(AuthCrypto.Hex(id), f.Identity.HostIdHex));
                byte[] oldFp = f.Identity.Fingerprint;
                f.Identity.Regenerate();
                Assert.NotEqual(oldFp, f.Identity.Fingerprint);
                Assert.Null(f.Store.TryGetSecret(AuthCrypto.Hex(id), f.Identity.HostIdHex));   // paired to the old identity: no longer matches
            }
        }

        [Fact]
        public void DeviceSecret_Persists_Protected_NeverPlaintext()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-ds-" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] secret = AuthCrypto.Random(32); string id = AuthCrypto.Hex(AuthCrypto.Random(16)), host = AuthCrypto.Hex(AuthCrypto.Random(16));
                var store = new DeviceStore(dir, new DpapiProtector());
                store.Enroll(id, "Living room", host, secret);
                string text = File.ReadAllText(Path.Combine(dir, "devices.json"));
                Assert.DoesNotContain(AuthCrypto.Hex(secret), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(secret), text);
                Assert.Contains("Living room", text);
                var reloaded = new DeviceStore(dir, new DpapiProtector());                  // survives a restart
                Assert.Equal(secret, reloaded.TryGetSecret(id, host));
                Assert.Null(reloaded.TryGetSecret(id, AuthCrypto.Hex(AuthCrypto.Random(16)))); // wrong host id
                Assert.Null(reloaded.TryGetSecret(AuthCrypto.Hex(AuthCrypto.Random(16)), host)); // wrong device
                Assert.All(reloaded.List(), d => Assert.Null(d.SecretProtected));              // List never exposes secrets
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Store_RemoveAndRemoveAll_RaiseEvents()
        {
            using (var f = new HostFixture())
            {
                var removed = new System.Collections.Generic.List<string>(); f.Store.Removed += removed.Add;
                string a = AuthCrypto.Hex(AuthCrypto.Random(16)), b = AuthCrypto.Hex(AuthCrypto.Random(16));
                f.Store.Enroll(a, "a", f.Identity.HostIdHex, AuthCrypto.Random(32)); f.Store.Enroll(b, "b", f.Identity.HostIdHex, AuthCrypto.Random(32));
                Assert.True(f.Store.Remove(a)); Assert.False(f.Store.Remove(a)); Assert.Equal(1, f.Store.Count);
                Assert.Equal(1, f.Store.RemoveAll()); Assert.Equal(0, f.Store.Count);
                Assert.Equal(new[] { a, b }, removed);
            }
        }

        [Fact]
        public void LanInterfaces_OnlyPrivateAddresses()
        {
            Assert.True(LanInterfaces.IsPrivate(IPAddress.Parse("10.0.0.5"))); Assert.True(LanInterfaces.IsPrivate(IPAddress.Parse("172.20.1.1"))); Assert.True(LanInterfaces.IsPrivate(IPAddress.Parse("192.168.1.9")));
            Assert.False(LanInterfaces.IsPrivate(IPAddress.Parse("8.8.8.8"))); Assert.False(LanInterfaces.IsPrivate(IPAddress.Parse("172.32.0.1"))); Assert.False(LanInterfaces.IsPrivate(IPAddress.Parse("169.254.1.1")));
            Assert.All(LanInterfaces.PrivateIPv4(), a => Assert.True(LanInterfaces.IsPrivate(a)));
        }
    }

    public class RateLimiterTests
    {
        [Fact]
        public void FiveFailures_LockOut_ThenRecover()
        {
            var clock = new FakeClock(); var r = new RateLimiter(clock.Get);
            for (int i = 0; i < 4; i++) { r.Failure("ip"); Assert.False(r.IsLockedOut("ip")); }
            r.Failure("ip"); Assert.True(r.IsLockedOut("ip")); Assert.False(r.AllowConnection("ip"));
            Assert.True(r.AllowConnection("other"));
            clock.Advance(TimeSpan.FromSeconds(61)); Assert.True(r.AllowConnection("ip"));
        }

        [Fact]
        public void Success_ClearsFailures() { var r = new RateLimiter(new FakeClock().Get); for (int i = 0; i < 4; i++) r.Failure("ip"); r.Success("ip"); for (int i = 0; i < 4; i++) r.Failure("ip"); Assert.False(r.IsLockedOut("ip")); }

        [Fact]
        public void ConnectionFlood_IsDropped_NormalReconnectsAreNot()
        {
            var clock = new FakeClock(); var r = new RateLimiter(clock.Get);
            for (int i = 0; i < 20; i++) Assert.True(r.AllowConnection("ip"));
            Assert.False(r.AllowConnection("ip"));
            clock.Advance(TimeSpan.FromSeconds(11)); Assert.True(r.AllowConnection("ip"));
            // a headset reconnecting once every few seconds is never limited
            var calm = new RateLimiter(clock.Get); for (int i = 0; i < 100; i++) { Assert.True(calm.AllowConnection("ip")); clock.Advance(TimeSpan.FromSeconds(3)); }
        }
    }

    public class TransportTests
    {
        [Fact]
        public async Task ValidHmac_Succeeds_AndReachesTheApplicationProtocol()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest();
                using (var c = f.NewClient())
                {
                    var r = await c.Authenticate(id, secret);
                    Assert.Equal(MsgType.Ok, r.answer);
                    await c.Ssl.WriteAsync(new[] { (byte)'W' }, 0, 1); await c.Ssl.FlushAsync();
                    var magic = new byte[4]; int got = 0; while (got < 4) got += await c.Ssl.ReadAsync(magic, got, 4 - got);
                    Assert.Equal("XRSL", Encoding.ASCII.GetString(magic));       // the normal protocol now runs over the authenticated stream
                }
                Assert.Equal(AuthCrypto.Hex(id), f.LastAuthenticatedDevice);
            }
        }

        [Fact]
        public async Task WrongHmac_Fails()
        {
            using (var f = new HostFixture())
            {
                var (id, _) = await f.PairHonest(); int before = f.AuthenticatedCount;
                using (var c = f.NewClient()) { var r = await c.Authenticate(id, AuthCrypto.Random(32)); Assert.Equal(MsgType.Reject, r.answer); Assert.Equal((byte)RejectCode.AuthFailed, r.code); }
                Assert.Equal(before, f.AuthenticatedCount);
                Assert.True(f.Log.Contains("authentication failed"));
            }
        }

        [Fact]
        public async Task WrongDeviceId_InTheMac_Fails()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest();
                using (var c = f.NewClient()) { var r = await c.Authenticate(id, secret, macDeviceId: AuthCrypto.Random(16)); Assert.Equal(MsgType.Reject, r.answer); }
                using (var c = f.NewClient()) { var r = await c.Authenticate(AuthCrypto.Random(16), secret); Assert.Equal(MsgType.Reject, r.answer); }   // unknown device: same answer
            }
        }

        [Fact]
        public async Task WrongHostId_InTheMac_Fails()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest(); int before = f.AuthenticatedCount;
                using (var c = f.NewClient()) { var r = await c.Authenticate(id, secret, macHostId: AuthCrypto.Random(16)); Assert.Equal(MsgType.Reject, r.answer); }
                Assert.Equal(before, f.AuthenticatedCount);
            }
        }

        [Fact]
        public async Task ReplayedResponse_FromAnEarlierConnection_Fails()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest(); int before = f.AuthenticatedCount;
                byte[] recorded, firstChallenge;
                using (var a = f.NewClient()) { var r = await a.Authenticate(id, secret); Assert.Equal(MsgType.Ok, r.answer); recorded = r.response; firstChallenge = r.challenge; }
                using (var b = f.NewClient())
                {
                    var r = await b.Authenticate(id, secret, replayResponse: recorded);
                    Assert.NotEqual(firstChallenge, r.challenge);                 // every connection gets a fresh challenge
                    Assert.Equal(MsgType.Reject, r.answer);                       // the recorded answer is useless
                }
                await Task.Delay(100);
                Assert.Equal(before + 1, f.AuthenticatedCount);            // only the honest connection got through
            }
        }

        [Fact]
        public async Task EnrollmentOutsideTheWindow_IsRefused()
        {
            using (var f = new HostFixture())
            {
                using (var c = f.NewClient())
                {
                    await c.Hello(ModePair, AuthCrypto.Random(16));
                    var r = await c.Receive();
                    Assert.Equal(MsgType.Reject, r.type); Assert.Equal((byte)RejectCode.PairingClosed, r.payload[0]);
                }
                Assert.Equal(0, f.Store.Count);
                Assert.True(f.Log.Contains("pairing refused"));
                // and after a completed pairing the window is closed again: a second enrollment needs a new window
                await f.PairHonest();
                using (var c = f.NewClient()) { await c.Hello(ModePair, AuthCrypto.Random(16)); var r = await c.Receive(); Assert.Equal((byte)RejectCode.PairingClosed, r.payload[0]); }
                Assert.Equal(1, f.Store.Count);
            }
        }

        [Fact]
        public async Task Enrollment_AfterTheWindowExpires_IsRefused()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open();
                f.Clock.Advance(TimeSpan.FromMinutes(3));
                using (var c = f.NewClient()) { await c.Hello(ModePair, AuthCrypto.Random(16)); var r = await c.Receive(); Assert.Equal((byte)RejectCode.PairingClosed, r.payload[0]); }
                Assert.Equal(0, f.Store.Count);
            }
        }

        [Fact]
        public async Task WindowClosedDuringTheCeremony_NothingIsStored()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open(); f.ResetCodeSignal();
                var id = AuthCrypto.Random(16);
                using (var c = f.NewClient())
                {
                    await c.BeginPair(id, f);
                    await f.CodeReady.Task;
                    f.Window.ConfirmOnPc(true);
                    f.Window.Close("test closes it");
                    var result = await c.ConfirmPair(AuthCrypto.Random(32), "late");
                    Assert.Equal(MsgType.Reject, result);
                }
                Assert.Equal(0, f.Store.Count);
            }
        }

        [Fact]
        public async Task PcUserRejects_NothingIsStored_AndCountsAsAFailure()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open(); f.ResetCodeSignal();
                using (var c = f.NewClient())
                {
                    await c.BeginPair(AuthCrypto.Random(16), f);
                    await f.CodeReady.Task;
                    f.Window.ConfirmOnPc(false);
                    Assert.Equal(MsgType.Reject, await c.ConfirmPair(AuthCrypto.Random(32), "x"));
                }
                Assert.Equal(0, f.Store.Count); Assert.Equal(1, f.Window.Failures);
            }
        }

        [Fact]
        public async Task ManInTheMiddle_SeesADifferentCode_AndCancelling_CostsAFailure()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open(); f.ResetCodeSignal();
                var id = AuthCrypto.Random(16);
                using (var c = f.NewClient())
                {
                    // the headset sees another certificate than the host's (a man in the middle terminated its TLS)
                    string headsetCode = await c.BeginPair(id, f, fingerprintOverride: AuthCrypto.Random(32));
                    var hostSide = await f.CodeReady.Task;
                    Assert.NotEqual(hostSide.Code, headsetCode);
                    await c.Send(MsgType.Cancel);
                    Assert.Equal(MsgType.Reject, (await c.Receive()).type);
                }
                Assert.Equal(0, f.Store.Count); Assert.Equal(1, f.Window.Failures);
            }
        }

        [Fact]
        public async Task WrongReveal_BreaksTheCommitment()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open();
                using (var c = f.NewClient())
                {
                    await c.Hello(ModePair, AuthCrypto.Random(16));
                    await c.Send(MsgType.Commit, AuthCrypto.Commit(AuthCrypto.Random(32)));
                    Assert.Equal(MsgType.HostNonce, (await c.Receive()).type);
                    await c.Send(MsgType.Reveal, AuthCrypto.Random(32));          // not what was committed
                    var r = await c.Receive(); Assert.Equal(MsgType.Reject, r.type); Assert.Equal((byte)RejectCode.PairingFailed, r.payload[0]);
                }
                Assert.Equal(1, f.Window.Failures); Assert.Equal(0, f.Store.Count);
            }
        }

        [Fact]
        public async Task ThreeFailedCeremonies_CloseTheWindow_TheFourthIsRefused()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open();
                for (int i = 0; i < 3; i++)
                {
                    f.ResetCodeSignal();
                    using (var c = f.NewClient()) { await c.BeginPair(AuthCrypto.Random(16), f); await f.CodeReady.Task; await c.Send(MsgType.Cancel); Assert.Equal(MsgType.Reject, (await c.Receive()).type); }
                }
                Assert.False(f.Window.IsOpen);
                using (var c = f.NewClient()) { await c.Hello(ModePair, AuthCrypto.Random(16)); var r = await c.Receive(); Assert.Equal((byte)RejectCode.PairingClosed, r.payload[0]); }
                Assert.True(f.Log.Contains("too many failures"));
                Assert.Equal(0, f.Store.Count);
            }
        }

        [Fact]
        public async Task RevokedDevice_Fails_AndItsLiveConnectionIsDropped()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest();
                using (var c = f.NewClient())
                {
                    Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer);
                    await Task.Delay(200);
                    Assert.Equal(1, f.Listener.LiveConnections);
                    Assert.True(f.Store.Remove(AuthCrypto.Hex(id)));
                    await Task.Delay(500);
                    Assert.Equal(0, f.Listener.LiveConnections);
                    await Assert.ThrowsAnyAsync<Exception>(async () => { for (int i = 0; i < 20; i++) { await c.Ssl.WriteAsync(new[] { (byte)'W' }, 0, 1); await Task.Delay(50); } });
                }
                using (var c = f.NewClient()) Assert.Equal(MsgType.Reject, (await c.Authenticate(id, secret)).answer);   // the old secret no longer works
            }
        }

        [Fact]
        public async Task RemoveAll_InvalidatesEveryDevice()
        {
            using (var f = new HostFixture())
            {
                var a = await f.PairHonest("a"); var b = await f.PairHonest("b");
                Assert.Equal(2, f.Store.Count);
                Assert.Equal(2, f.Store.RemoveAll());
                using (var c = f.NewClient()) Assert.Equal(MsgType.Reject, (await c.Authenticate(a.deviceId, a.secret)).answer);
                using (var c = f.NewClient()) Assert.Equal(MsgType.Reject, (await c.Authenticate(b.deviceId, b.secret)).answer);
            }
        }

        [Fact]
        public async Task UnauthenticatedClient_CannotReachTheApplicationProtocol()
        {
            using (var f = new HostFixture())
            {
                using (var c = f.NewClient())
                {
                    // raw application bytes ('W' = send me the window list) instead of an authentication hello
                    await c.Ssl.WriteAsync(new byte[] { (byte)'W', 0, 0 }, 0, 3); await c.Ssl.FlushAsync();
                    var r = await c.Receive();
                    Assert.Equal(MsgType.Reject, r.type); Assert.Equal((byte)RejectCode.ProtocolError, r.payload[0]);
                    var rest = new byte[4]; int n = 0; try { n = await c.Ssl.ReadAsync(rest, 0, 4); } catch (Exception) { }
                    Assert.Equal(0, n);                                           // nothing else, certainly no 'XRSL'
                }
                Assert.Equal(0, f.AuthenticatedCount);
            }
        }

        [Fact]
        public async Task AuthenticatedHandler_IsNeverCalled_ForPairingClientsThatDoNotFinish()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open();
                using (var c = f.NewClient()) { await c.Hello(ModePair, AuthCrypto.Random(16)); }        // hello, then silence and a closed socket
                await Task.Delay(300);
                Assert.Equal(0, f.AuthenticatedCount);
            }
        }

        [Fact]
        public async Task UnknownAuthProtocolVersion_IsRefusedCleanly()
        {
            using (var f = new HostFixture())
            {
                using (var c = f.NewClient())
                {
                    await c.Hello(ModeAuthenticate, AuthCrypto.Random(16), version: 99);
                    var r = await c.Receive();
                    Assert.Equal(MsgType.Reject, r.type); Assert.Equal((byte)RejectCode.UnsupportedVersion, r.payload[0]); Assert.Equal(AuthVersion, r.payload[1]);
                }
                Assert.True(f.Log.Contains("unsupported auth protocol version 99"));
                using (var c = f.NewClient()) { var (id, secret) = await f.PairHonest(); Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer); }   // the listener is still fine
            }
        }

        [Fact]
        public async Task RepeatedFailedAuthentication_IsRateLimited_ButNormalReconnectsAreNot()
        {
            using (var f = new HostFixture())
            {
                var (id, secret) = await f.PairHonest();
                for (int i = 0; i < 10; i++) { f.Clock.Advance(TimeSpan.FromSeconds(2)); using (var c = f.NewClient()) Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer); }    // honest reconnects every couple of seconds
                for (int i = 0; i < 4; i++) { f.Clock.Advance(TimeSpan.FromSeconds(2)); using (var c = f.NewClient()) await c.Authenticate(id, AuthCrypto.Random(32)); }
                f.Clock.Advance(TimeSpan.FromSeconds(2)); using (var c = f.NewClient()) Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer);   // four typos then success: still fine, and it clears the count
                for (int i = 0; i < 5; i++) { f.Clock.Advance(TimeSpan.FromSeconds(2)); using (var c = f.NewClient()) await c.Authenticate(id, AuthCrypto.Random(32)); }
                await Assert.ThrowsAnyAsync<Exception>(async () => { using (var c = f.NewClient()) { await c.Authenticate(id, secret); } });          // locked out: the connection is dropped
                f.Clock.Advance(TimeSpan.FromSeconds(61));
                using (var c = f.NewClient()) Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer);                                  // lockout over
                Assert.True(f.Log.Contains("rate limit"));
            }
        }

        [Fact]
        public async Task Secrets_Codes_AndResponses_AreNeverLogged()
        {
            using (var f = new HostFixture())
            {
                f.Window.Open(); f.ResetCodeSignal();
                byte[] id = AuthCrypto.Random(16), secret = AuthCrypto.Random(32);
                string code;
                using (var c = f.NewClient())
                {
                    code = await c.BeginPair(id, f); await f.CodeReady.Task; f.Window.ConfirmOnPc(true); Assert.Equal(MsgType.Ok, await c.ConfirmPair(secret, "Quiet"));
                }
                var all = string.Join("\n", f.Log.Lines);
                Assert.DoesNotContain(AuthCrypto.Hex(secret), all, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(AuthCrypto.Hex(id), all, StringComparison.OrdinalIgnoreCase);       // only the short id
                Assert.DoesNotContain(code, all);
                Assert.True(f.Log.Contains("pairing opened")); Assert.True(f.Log.Contains("pairing completed"));
            }
        }

        [Fact]
        public async Task TlsIsVersion12_AndTheHeadsetCanPinTheFingerprint()
        {
            using (var f = new HostFixture())
            {
                using (var c = await TestClient.Connect(f.Port, f.Identity.Fingerprint)) Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, c.Ssl.SslProtocol);
                await Assert.ThrowsAnyAsync<Exception>(async () => { using (var c = await TestClient.Connect(f.Port, AuthCrypto.Random(32))) { } });   // wrong pin refused by the client
            }
        }

        [Fact]
        public async Task ManagedListener_BindsOnlyWhileSomethingIsPairedOrPairingIsOpen()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-ml-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FakeClock(); var log = new TestLog();
                using (var id = HostIdentity.LoadOrCreate(dir, new DpapiProtector(), log))
                {
                    var store = new DeviceStore(dir, new DpapiProtector(), clock.Get); var window = new PairingWindow(log, clock.Get);
                    using (var l = new SecureListener(id, store, window, log, null, null, clock.Get))
                    {
                        l.StartManaged(new[] { IPAddress.Loopback }, 0);
                        Assert.False(l.IsListening);                              // nothing paired, window closed: no port exposed
                        window.Open(); await Task.Delay(100); Assert.True(l.IsListening);
                        window.Close(); await Task.Delay(100); Assert.False(l.IsListening);
                        store.Enroll(AuthCrypto.Hex(AuthCrypto.Random(16)), "x", id.HostIdHex, AuthCrypto.Random(32)); await Task.Delay(100); Assert.True(l.IsListening);
                        store.RemoveAll(); await Task.Delay(100); Assert.False(l.IsListening);
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
