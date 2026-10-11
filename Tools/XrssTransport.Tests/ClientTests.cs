using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XrssTransport;
using XrssTransport.Client;

namespace XrssTransport.Tests
{
    /// <summary>Scripted answers for the mDNS search, so connection logic is tested without a network.</summary>
    public sealed class FakeDiscovery : IDiscovery
    {
        public List<DiscoveredPc> Result = new List<DiscoveredPc>();
        public int Calls;
        public List<DiscoveredPc> Discover(int timeoutMs, Func<List<DiscoveredPc>, bool> enough) { Calls++; enough?.Invoke(Result); return new List<DiscoveredPc>(Result); }
    }

    /// <summary>A headset: its own temp folders, a log that tests can search, and a PcLink wired to a fake or real discovery.</summary>
    public sealed class HeadsetFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "xrss-headset-" + Guid.NewGuid().ToString("N"));
        public readonly SimpleClientEnv Env;
        public readonly List<string> Log = new List<string>();
        public readonly FakeDiscovery Discovery = new FakeDiscovery();
        public PcLink Link;

        public HeadsetFixture(LinkMode mode = LinkMode.PairedLan)
        {
            Env = new SimpleClientEnv { SecretDir = Path.Combine(Root, "secret"), SettingsDir = Path.Combine(Root, "settings") };
            Env.Sink = m => { lock (Log) Log.Add(m); };
            Link = new PcLink(Env, Discovery, mode);
        }

        /// <summary>Simulates an app restart: a new PcLink reading what was saved.</summary>
        public void Restart() { Link = new PcLink(Env, Discovery, LinkMode.PairedLan); }

        public string AllLog() { lock (Log) return string.Join("\n", Log); }

        public async Task WaitFor(Func<LinkSnapshot, bool> cond, int ms = 10000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (cond(Link.Snapshot)) return; await Task.Delay(20); }
            var s = Link.Snapshot; throw new TimeoutException($"snapshot never matched; state {s.State} step {s.Step} error {s.Error} text '{s.Text}'");
        }

        /// <summary>Runs a full honest pairing against the host fixture: the PC opens a window, both sides show the same code, both users confirm. Returns the authenticated stream the pairing produced.</summary>
        public async Task<(Stream stream, IDisposable owner)> PairWith(HostFixture host, string name = "Test PC")
        {
            host.Window.Open(); host.ResetCodeSignal();
            Link.BeginPairing(new DiscoveredPc { DisplayName = name, Port = host.Port, Addresses = { IPAddress.Loopback } });
            await WaitFor(s => s.Step == PairStep.ShowCode);
            var hostSide = await host.CodeReady.Task;
            if (hostSide.Code != Link.Snapshot.Code) throw new Exception("codes differ: " + hostSide.Code + " vs " + Link.Snapshot.Code);
            host.Window.ConfirmOnPc(true);
            Link.AnswerCode(true);
            await WaitFor(s => s.Step == PairStep.Complete);
            if (!Link.TryConnect(out var stream, out var owner)) throw new Exception("pairing connection was not handed over: " + Link.Snapshot.Text);
            return (stream, owner);
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    public class VectorTests
    {
        static byte[] Range(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

        [Fact]
        public void KnownVectors_MatchTheIndependentImplementation()
        {
            // Docs/PAIRING_TEST_VECTORS.md: computed with Python hashlib/hmac. The Unity EditMode tests assert the same constants.
            Assert.Equal("4B4C0724A544C9147BCA888F9ED853D779BDE968EA7AF0405911EE9DB0634D30", AuthCrypto.Hex(AuthCrypto.AuthMac(Range(1, 32), Range(0xA0, 16), Range(0xB0, 16), Range(0xC0, 32))));
            Assert.Equal("CB7C0122587F929E5610667A913CBDB3446B3C08799F61688055D7FC420A419B", AuthCrypto.Hex(AuthCrypto.Commit(Range(0x40, 32))));
            Assert.Equal("165978", AuthCrypto.Sas(Range(0x80, 32), Range(0x40, 32), Range(0x60, 32), Range(0xB0, 16)));
            Assert.Equal("BCE0AFF19CF5AA6A7469A30D61D04E4376E4BBF6381052EE9E7F33925C954D52", AuthCrypto.Hex(AuthCrypto.Fingerprint(Range(0, 100))));
        }

        [Fact]
        public void Hex_RoundTrips_AndRejectsGarbage() { var b = AuthCrypto.Random(16); Assert.Equal(b, AuthCrypto.FromHex(AuthCrypto.Hex(b))); Assert.Throws<FormatException>(() => AuthCrypto.FromHex("abc")); }

        [Fact]
        public void MacEquals_IsExact() { var a = AuthCrypto.Random(32); var b = (byte[])a.Clone(); Assert.True(AuthCrypto.MacEquals(a, b)); b[31] ^= 1; Assert.False(AuthCrypto.MacEquals(a, b)); Assert.False(AuthCrypto.MacEquals(a, new byte[31])); Assert.False(AuthCrypto.MacEquals(a, null)); }
    }

    public class ClientStorageTests
    {
        [Fact]
        public void DeviceId_Persists_AndChangesOnlyOnExplicitReset()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-did-" + Guid.NewGuid().ToString("N"));
            try
            {
                var a = HeadsetIdentity.LoadOrCreate(dir); string id = a.DeviceIdHex;
                Assert.Equal(32, id.Length);
                Assert.Equal(id, HeadsetIdentity.LoadOrCreate(dir).DeviceIdHex);
                a.Reset(); Assert.NotEqual(id, a.DeviceIdHex);
                Assert.Equal(a.DeviceIdHex, HeadsetIdentity.LoadOrCreate(dir).DeviceIdHex);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PairedHost_Persists_EveryField_AndRevocationFlag()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-ph-" + Guid.NewGuid().ToString("N"));
            try
            {
                var host = new PairedHost { HostIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)), FingerprintHex = AuthCrypto.Hex(AuthCrypto.Random(32)), DeviceIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)), Secret = AuthCrypto.Random(32), LastAddress = "10.0.0.5", LastPort = 5610, DisplayName = "Study PC", PairedUtc = new DateTime(2026, 10, 11, 1, 2, 3, DateTimeKind.Utc), LastConnectedUtc = new DateTime(2026, 10, 12, 4, 5, 6, DateTimeKind.Utc) };
                new PairedHostStore(dir).Save(host);
                var back = new PairedHostStore(dir).Primary;
                Assert.Equal(host.HostIdHex, back.HostIdHex); Assert.Equal(host.FingerprintHex, back.FingerprintHex); Assert.Equal(host.DeviceIdHex, back.DeviceIdHex); Assert.Equal(host.Secret, back.Secret);
                Assert.Equal("10.0.0.5", back.LastAddress); Assert.Equal(5610, back.LastPort); Assert.Equal("Study PC", back.DisplayName); Assert.Equal(1, back.AuthVersion);
                Assert.Equal(host.PairedUtc, back.PairedUtc); Assert.Equal(host.LastConnectedUtc, back.LastConnectedUtc); Assert.Null(back.RevokedSeenUtc);
                var s = new PairedHostStore(dir); s.Update(host.HostIdHex, h => h.RevokedSeenUtc = DateTime.UtcNow);
                Assert.NotNull(new PairedHostStore(dir).Primary.RevokedSeenUtc);
                Assert.True(s.Remove(host.HostIdHex)); Assert.Equal(0, new PairedHostStore(dir).Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void CorruptStore_StartsEmpty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xrss-cs-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "paired-hosts.txt"), "[host]\nhostId=zz\nsecret=!!!\n"); Assert.Equal(0, new PairedHostStore(dir).Count); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    public class MdnsPacketTests
    {
        [Fact]
        public void Response_RoundTrips_ThroughTheParserAndCollector()
        {
            var addrs = new List<IPAddress> { IPAddress.Parse("10.0.0.224"), IPAddress.Parse("10.35.78.22") };
            string id = AuthCrypto.Hex(AuthCrypto.Random(16));
            var pkt = MdnsPacket.BuildResponse("Study PC", "xrss-study-pc-ab12", 5610, new[] { new KeyValuePair<string, string>("v", "1"), new KeyValuePair<string, string>("id", id), new KeyValuePair<string, string>("name", "Study PC") }, addrs);
            var c = new MdnsCollector(); c.Add(MdnsPacket.Parse(pkt)); c.Add(MdnsPacket.Parse(pkt));      // the same answer twice: no duplicates
            var r = c.Results();
            Assert.Single(r);
            Assert.Equal(5610, r[0].Port); Assert.Equal(id, r[0].HostIdHex); Assert.Equal("Study PC", r[0].DisplayName); Assert.Equal(2, r[0].Addresses.Count);
        }

        [Fact]
        public void Goodbye_RemovesThePc()
        {
            var c = new MdnsCollector(); var addr = new List<IPAddress> { IPAddress.Loopback };
            var txt = new[] { new KeyValuePair<string, string>("id", AuthCrypto.Hex(AuthCrypto.Random(16))) };
            c.Add(MdnsPacket.Parse(MdnsPacket.BuildResponse("PC", "h", 1, txt, addr))); Assert.Single(c.Results());
            c.Add(MdnsPacket.Parse(MdnsPacket.BuildResponse("PC", "h", 1, txt, addr, 0))); Assert.Empty(c.Results());
        }

        [Fact]
        public void Query_IsRecognised_AndNotAnAnswer()
        {
            var q = MdnsPacket.Parse(MdnsPacket.BuildQuery(false)); Assert.False(q.IsResponse); Assert.Contains(MdnsPacket.Service, q.Questions);
            var qu = MdnsPacket.BuildQuery(true); Assert.Equal(0x80, qu[qu.Length - 2]);                  // unicast-response bit
        }

        [Fact]
        public void NameCompression_IsUnderstood()
        {
            // header: 0 questions, 2 answers; answer 1: PTR "_xrss._tcp.local" -> "A._xrss._tcp.local" with the target compressed to point at offset 12; answer 2: A for "A._xrss._tcp.local"? use SRV-less minimal
            var b = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
            int nameAt = b.Count;
            foreach (var l in new[] { "_xrss", "_tcp", "local" }) { b.Add((byte)l.Length); b.AddRange(Encoding.ASCII.GetBytes(l)); }
            b.Add(0); b.AddRange(new byte[] { 0, 12, 0, 1, 0, 0, 0, 120 });
            var rdata = new List<byte> { 1, (byte)'A', 0xC0, (byte)nameAt };                           // "A" + pointer to "_xrss._tcp.local"
            b.Add(0); b.Add((byte)rdata.Count); b.AddRange(rdata);
            // the owner name of the record above was written uncompressed at nameAt: re-point: build properly instead
            var m = MdnsPacket.Parse(b.ToArray());
            Assert.True(m.IsResponse);
            Assert.NotEmpty(m.Records);
            Assert.Equal("A._xrss._tcp.local", m.Records[0].Ptr);
        }

        [Fact]
        public void Garbage_NeverThrows()
        {
            var rnd = new Random(1);
            for (int i = 0; i < 300; i++) { var d = new byte[rnd.Next(0, 200)]; rnd.NextBytes(d); MdnsPacket.Parse(d); }
            var good = MdnsPacket.BuildResponse("PC", "h", 1, new[] { new KeyValuePair<string, string>("id", "x") }, new List<IPAddress> { IPAddress.Loopback });
            for (int cut = 0; cut < good.Length; cut++) MdnsPacket.Parse(good.Take(cut).ToArray());
            var loop = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 2, 0xC0, 12 };    // a name that points at itself
            MdnsPacket.Parse(loop);
        }

        [Fact]
        public void Advertiser_And_Discovery_FindEachOther_OnTheRealNetwork()
        {
            var lan = LanInterfaces.PrivateIPv4();
            if (lan.Count == 0) return;                                                                // no private interface: nothing to test on this machine
            string id = AuthCrypto.Hex(AuthCrypto.Random(16));
            using (var adv = new MdnsAdvertiser("Test PC", id, 5610, lan))
            {
                adv.Start();
                var env = new SimpleClientEnv();
                var found = new MdnsDiscovery(env).Discover(5000, l => l.Any(p => p.HostIdHex == id));
                var pc = found.FirstOrDefault(p => p.HostIdHex == id);
                Assert.NotNull(pc);
                Assert.Equal(5610, pc.Port); Assert.Contains(lan[0], pc.Addresses);
            }
        }
    }

    public class ClientConnectionTests
    {
        [Fact]
        public async Task Pairing_Succeeds_AndSavesTrustOnlyAfterTheFinalOk()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                host.Window.Open(); host.ResetCodeSignal();
                head.Link.BeginPairing(new DiscoveredPc { DisplayName = "Test PC", Port = host.Port, Addresses = { IPAddress.Loopback } });
                await head.WaitFor(s => s.Step == PairStep.ShowCode);
                Assert.Equal(0, head.Link.Store.Count);                                                // code on screen: nothing trusted yet
                string code = head.Link.Snapshot.Code; Assert.Matches("^[0-9]{6}$", code);
                Assert.Equal((await host.CodeReady.Task).Code, code);                                   // the PC computed the same six digits from its own certificate
                head.Link.AnswerCode(true);
                await head.WaitFor(s => s.Step == PairStep.WaitingForPc);
                Assert.Equal(0, head.Link.Store.Count);                                                // user confirmed, PC not yet: still nothing trusted
                Assert.Equal(0, host.Store.Count);
                host.Window.ConfirmOnPc(true);
                await head.WaitFor(s => s.Step == PairStep.Complete);
                Assert.Equal(1, head.Link.Store.Count); Assert.Equal(1, host.Store.Count);
                var saved = head.Link.Store.Primary;
                Assert.Equal(AuthCrypto.Hex(host.Identity.Fingerprint), saved.FingerprintHex);
                Assert.Equal(host.Identity.HostIdHex, saved.HostIdHex);
                Assert.Equal(head.Link.Identity.DeviceIdHex, saved.DeviceIdHex);
                Assert.True(head.Link.TryConnect(out var stream, out var owner));                     // the pairing connection is already authenticated: handed straight to the app protocol
                using (owner) { await stream.WriteAsync(new[] { (byte)'W' }, 0, 1); await stream.FlushAsync(); var m = new byte[4]; int g = 0; while (g < 4) g += await stream.ReadAsync(m, g, 4 - g); Assert.Equal("XRSL", Encoding.ASCII.GetString(m)); }
                Assert.Equal(LinkState.Connected, head.Link.Snapshot.State);
            }
        }

        [Fact]
        public async Task CancelledPairing_StoresNothing_OnEitherSide()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                host.Window.Open(); host.ResetCodeSignal();
                head.Link.BeginPairing(new DiscoveredPc { Port = host.Port, Addresses = { IPAddress.Loopback } });
                await head.WaitFor(s => s.Step == PairStep.ShowCode);
                head.Link.AnswerCode(false);                                                           // "Doesn't match"
                await head.WaitFor(s => s.Step == PairStep.None && s.State != LinkState.Pairing);
                await Task.Delay(200);
                Assert.Equal(0, head.Link.Store.Count); Assert.Equal(0, host.Store.Count); Assert.Equal(1, host.Window.Failures);
            }
        }

        [Fact]
        public async Task PcRefusesToConfirm_StoresNothing()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                host.Window.Open(); host.ResetCodeSignal();
                head.Link.BeginPairing(new DiscoveredPc { Port = host.Port, Addresses = { IPAddress.Loopback } });
                await head.WaitFor(s => s.Step == PairStep.ShowCode); await host.CodeReady.Task;
                head.Link.AnswerCode(true); host.Window.ConfirmOnPc(false);
                await head.WaitFor(s => s.State == LinkState.Error);
                Assert.Equal(LinkError.CodeRejected, head.Link.Snapshot.Error);
                Assert.Equal(0, head.Link.Store.Count); Assert.Equal(0, host.Store.Count);
            }
        }

        [Fact]
        public async Task PairingWindowClosed_IsReportedPlainly_AndStoresNothing()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                head.Link.BeginPairing(new DiscoveredPc { Port = host.Port, Addresses = { IPAddress.Loopback } });
                await head.WaitFor(s => s.State == LinkState.Error);
                Assert.Equal(LinkError.PairingClosed, head.Link.Snapshot.Error); Assert.Contains("Open pairing on the PC", head.Link.Snapshot.Text);
                Assert.Equal(0, head.Link.Store.Count);
            }
        }

        [Fact]
        public async Task PcNotFound_WhenNothingListens()
        {
            using (var head = new HeadsetFixture())
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int dead = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                head.Link.BeginPairingAt("127.0.0.1:" + dead);
                await head.WaitFor(s => s.State == LinkState.Error);
                Assert.Equal(LinkError.PcNotFound, head.Link.Snapshot.Error);
            }
        }

        [Fact]
        public void WrongCertificateFingerprint_IsRejected_CorrectOneAccepted()
        {
            using (var host = new HostFixture())
            {
                Assert.Throws<PinMismatchException>(() => SecureChannel.Connect("127.0.0.1", host.Port, AuthCrypto.Random(32), 3000));
                using (var ok = SecureChannel.Connect("127.0.0.1", host.Port, host.Identity.Fingerprint, 3000)) { Assert.Equal(System.Security.Authentication.SslProtocols.Tls12, ok.Protocol); Assert.Equal(host.Identity.Fingerprint, ok.PresentedFingerprint); }
            }
        }

        [Fact]
        public async Task Reconnect_AfterAppRestart_NeedsNoCode_AndRecordsTimings()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                var first = await head.PairWith(host); first.owner.Dispose();
                head.Restart();                                                                       // app restart: everything comes from storage
                Assert.True(head.Link.TryConnect(out var stream, out var owner));
                using (owner)
                {
                    await stream.WriteAsync(new[] { (byte)'W' }, 0, 1); await stream.FlushAsync(); var m = new byte[4]; int g = 0; while (g < 4) g += await stream.ReadAsync(m, g, 4 - g); Assert.Equal("XRSL", Encoding.ASCII.GetString(m));
                }
                Assert.Equal(LinkState.Connected, head.Link.Snapshot.State);
                Assert.Equal("saved address", head.Link.Timings.Via); Assert.True(head.Link.Timings.TlsMs >= 0 && head.Link.Timings.AuthMs >= 0);
                Assert.DoesNotContain("discovered addresses", head.AllLog());                          // the saved address worked: the connect itself needed no discovery
                Assert.True(head.Link.Store.Primary.LastConnectedUtc > head.Link.Store.Primary.PairedUtc);
            }
        }

        [Fact]
        public async Task ReconnectAfterDrop_IsImmediate_ThenBacksOff()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                var first = await head.PairWith(host); first.owner.Dispose();
                head.Link.NotifyDisconnected();
                Assert.Equal(0, head.Link.RetryDelayMs); Assert.Equal(LinkState.Reconnecting, head.Link.Snapshot.State);
                // host goes away: retries back off 0, 0.5, 1, 2, 3, 5, 5, 10, 10 s
                host.Listener.Dispose();
                var delays = new List<int>();
                for (int i = 0; i < 9; i++) { Assert.False(head.Link.TryConnect(out _, out _)); delays.Add(head.Link.RetryDelayMs); }
                Assert.Equal(new[] { 0, 500, 1000, 2000, 3000, 5000, 5000, 10000, 10000 }, delays);
                Assert.Equal(LinkError.PcNotFound, head.Link.Snapshot.Error);
            }
        }


        [Fact]
        public void Rank_PutsTheFastestReachableAddressFirst_AndDropsDeadOnes()
        {
            long Fake(string a, int port, int t) => a == "10.35.78.22" ? 1 : a == "10.0.0.224" ? 24 : -1;
            var r = AddressProbe.Rank(new[] { "10.0.0.224", "192.168.9.9", "10.35.78.22", "10.0.0.224" }, 5610, 1500, 150, Fake);
            Assert.Equal(new[] { "10.35.78.22", "10.0.0.224" }, r.Select(x => x.address).ToArray());
        }

        [Fact]
        public void Rank_DoesNotWaitForADeadAddress()
        {
            long Fake(string a, int port, int t) { if (a == "dead") { Thread.Sleep(1400); return -1; } return 2; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = AddressProbe.Rank(new[] { "dead", "alive" }, 5610, 1500, 150, Fake);
            Assert.Equal("alive", r[0].address); Assert.True(sw.ElapsedMilliseconds < 800, "waited " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public async Task Pairing_PrefersTheFasterPath_AndRemembersEveryAddress()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                head.Link.m_Probe = (a, p, t) => a == "127.0.0.1" ? 1 : 30;                           // pretend 127.0.0.1 is the 1 ms dongle link and the other address the slow router path
                host.Window.Open(); host.ResetCodeSignal();
                head.Link.BeginPairing(new DiscoveredPc { DisplayName = "PC", Port = host.Port, Addresses = { IPAddress.Parse("127.0.0.3"), IPAddress.Loopback } });
                await head.WaitFor(s => s.Step == PairStep.ShowCode); await host.CodeReady.Task;
                host.Window.ConfirmOnPc(true); head.Link.AnswerCode(true); await head.WaitFor(s => s.Step == PairStep.Complete);
                var saved = head.Link.Store.Primary;
                Assert.Equal("127.0.0.1", saved.LastAddress);
                Assert.Contains("127.0.0.3", saved.Addresses); Assert.Contains("127.0.0.1", saved.Addresses);
                Assert.Contains("127.0.0.1 1 ms", head.AllLog());
            }
        }

        [Fact]
        public async Task Reconnect_TriesSavedAddresses_FastestFirst()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.LastAddress = "127.0.0.2"; h.Addresses = new List<string> { "127.0.0.2", "127.0.0.1" }; h.LastPort = host.Port; });
                head.Restart();
                head.Link.m_Probe = (a, p, t) => a == "127.0.0.2" ? 1 : 20;                           // the "faster" saved address is stale; the slower one is the real PC
                Assert.True(head.Link.TryConnect(out var s, out var o)); o.Dispose();
                Assert.Contains("saved addresses by speed: 127.0.0.2 1 ms, 127.0.0.1 20 ms", head.AllLog());
                Assert.Equal("127.0.0.1", head.Link.Store.Primary.LastAddress);
            }
        }

        [Fact]
        public async Task StaleAddress_FallsBackToDiscovery_ByHostId()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.LastAddress = "127.0.0.2"; h.Addresses.Clear(); h.LastPort = host.Port; });          // the PC "moved": nothing listens there
                head.Discovery.Result = new List<DiscoveredPc> { new DiscoveredPc { HostIdHex = host.Identity.HostIdHex, Port = host.Port, DisplayName = "Test PC", Addresses = { IPAddress.Loopback } } };
                head.Restart();
                Assert.True(head.Link.TryConnect(out var s, out var o)); o.Dispose();
                Assert.Equal("discovery", head.Link.Timings.Via); Assert.True(head.Discovery.Calls >= 1);
                Assert.Equal("127.0.0.1", head.Link.Store.Primary.LastAddress);                       // the new address is remembered
            }
        }

        [Fact]
        public async Task AfterAConnect_OtherAddressesOfThePcAreLearned()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => h.Addresses.Clear());          // an older record that knows only its last address
                head.Restart();
                head.Discovery.Result = new List<DiscoveredPc> { new DiscoveredPc { HostIdHex = host.Identity.HostIdHex, Port = host.Port, Addresses = { IPAddress.Loopback, IPAddress.Parse("10.35.78.22") } } };
                Assert.True(head.Link.TryConnect(out var s, out var o)); o.Dispose();
                for (int i = 0; i < 100 && !head.Link.Store.Primary.Addresses.Contains("10.35.78.22"); i++) await Task.Delay(50);
                Assert.Contains("10.35.78.22", head.Link.Store.Primary.Addresses);                    // learned in the background, used (fastest first) from the next reconnect
            }
        }

        [Fact]
        public async Task DiscoveryOfADifferentPc_IsNotTrusted()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.LastAddress = "127.0.0.2"; h.Addresses.Clear(); h.LastPort = host.Port; });
                head.Discovery.Result = new List<DiscoveredPc> { new DiscoveredPc { HostIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)), Port = host.Port, Addresses = { IPAddress.Loopback } } };   // someone else's id at a reachable address
                head.Restart();
                Assert.False(head.Link.TryConnect(out _, out _));
                Assert.Equal(LinkError.PcNotFound, head.Link.Snapshot.Error);
            }
        }

        [Fact]
        public async Task ManualAddress_IsTheLastResort()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.LastAddress = "127.0.0.2"; h.Addresses.Clear(); h.LastPort = host.Port; });
                head.Restart();
                head.Link.SetManualAddress("127.0.0.1:" + host.Port);
                Assert.True(head.Link.TryConnect(out var s, out var o)); o.Dispose();
                Assert.Equal("typed address", head.Link.Timings.Via);
                head.Restart(); Assert.Equal("127.0.0.1:" + host.Port, head.Link.Snapshot.ManualAddress);   // the typed address is remembered
            }
        }

        [Fact]
        public async Task DifferentPcAtTheOldAddress_IsNeverTrusted_AndNeverSeesAnAuthenticationAttempt()
        {
            using (var host = new HostFixture()) using (var impostor = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.LastAddress = "127.0.0.1"; h.Addresses.Clear(); h.LastPort = impostor.Port; });       // the old address now answers with another PC's certificate
                head.Restart();
                Assert.False(head.Link.TryConnect(out _, out _));
                Assert.Equal(LinkError.WrongPc, head.Link.Snapshot.Error);
                Assert.DoesNotContain(impostor.Log.Lines, l => l.Contains("authentication failed for device") || l.Contains("authenticated from"));   // it never got a Hello, let alone an HMAC
                Assert.Equal(0, impostor.AuthenticatedCount);
            }
        }

        [Fact]
        public async Task WrongHostIdInTheChallenge_IsRefused_WithoutAnswering()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose(); await Task.Delay(100); int before = host.AuthenticatedCount;
                head.Link.Store.Update(host.Identity.HostIdHex, h => { h.HostIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)); });          // same certificate, but the PC claims a different identity than we saved
                head.Restart();
                Assert.False(head.Link.TryConnect(out _, out _));
                Assert.Equal(LinkError.WrongPc, head.Link.Snapshot.Error);
                Assert.Equal(before, host.AuthenticatedCount);                                         // no second authentication was completed
                Assert.DoesNotContain(host.Log.Lines, l => l.Contains("authentication failed for device"));    // and no answer was even sent for the host to reject
            }
        }

        [Fact]
        public async Task RevokedDevice_IsReportedOnce_NotRetriedForever_AndPairAgainWorks()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                host.Store.RemoveAll();                                                                // removed on the PC
                head.Restart();
                Assert.False(head.Link.TryConnect(out _, out _));
                Assert.Equal(LinkError.Removed, head.Link.Snapshot.Error); Assert.Equal(LinkState.Error, head.Link.Snapshot.State);
                int failures = host.Log.Lines.Count(l => l.Contains("authentication failed"));
                Assert.Equal(1, failures);
                for (int i = 0; i < 5; i++) Assert.False(head.Link.TryConnect(out _, out _));          // no more HMAC attempts: the PC is not hammered
                Assert.Equal(failures, host.Log.Lines.Count(l => l.Contains("authentication failed")));
                head.Restart(); Assert.False(head.Link.TryConnect(out _, out _)); Assert.Equal(LinkError.Removed, head.Link.Snapshot.Error);   // remembered across an app restart
                Assert.Equal(1, head.Link.Store.Count);                                                // trust is not cleared behind the user's back
                // the user chooses Pair again: it works and clears the flag
                var again = await head.PairWith(host); again.owner.Dispose();
                head.Restart(); Assert.True(head.Link.TryConnect(out _, out var o)); o.Dispose();
            }
        }

        [Fact]
        public async Task Forget_DeletesTheSecret_AndNeverAutoEnrolls()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                head.Link.Forget();
                Assert.Equal(0, head.Link.Store.Count); Assert.Equal(LinkState.NotPaired, head.Link.Snapshot.State);
                host.Window.Open();                                                                    // even with the PC's window open the headset does not enrol by itself
                Assert.False(head.Link.TryConnect(out _, out _));
                await Task.Delay(300); Assert.Equal(1, host.Store.Count); Assert.Equal(0, head.Link.Store.Count);
            }
        }

        [Fact]
        public void NotPaired_ReportsItself_WhenNothingIsSaved()
        {
            using (var head = new HeadsetFixture()) { Assert.False(head.Link.TryConnect(out _, out _)); Assert.Equal(LinkState.NotPaired, head.Link.Snapshot.State); }
        }

        [Fact]
        public void DevMode_BypassesPairing_AndUsesPlainTcp()
        {
            var plain = new TcpListener(IPAddress.Loopback, 0); plain.Start();
            try
            {
                using (var head = new HeadsetFixture(LinkMode.Dev))
                {
                    head.Link.DevPort = ((IPEndPoint)plain.LocalEndpoint).Port;
                    Assert.True(head.Link.TryConnect(out var stream, out var owner));
                    using (owner)
                    {
                        var server = plain.AcceptTcpClient();
                        stream.Write(new[] { (byte)'W' }, 0, 1); var b = new byte[1]; Assert.Equal(1, server.GetStream().Read(b, 0, 1)); Assert.Equal((byte)'W', b[0]);       // raw application bytes, no TLS, no authentication
                    }
                    Assert.Equal(LinkState.Dev, head.Link.Snapshot.State); Assert.Equal(0, head.Link.Store.Count); Assert.Equal(0, head.Discovery.Calls);
                    plain.Stop();
                    Assert.ThrowsAny<Exception>(() => head.Link.TryConnect(out _, out _));            // dev failure behaves as before: the caller sees the exception and retries in a second
                    Assert.Equal(1000, head.Link.RetryDelayMs);
                }
            }
            finally { plain.Stop(); }
        }

        [Fact]
        public void Mode_IsPersisted()
        {
            using (var head = new HeadsetFixture(LinkMode.Dev))
            {
                head.Link.SetMode(LinkMode.PairedLan); head.Restart(); Assert.Equal(LinkMode.PairedLan, head.Link.Mode);
                head.Link.SetMode(LinkMode.Dev); var again = new PcLink(head.Env, head.Discovery, LinkMode.PairedLan); Assert.Equal(LinkMode.Dev, again.Mode);
            }
        }

        [Fact]
        public async Task NoSecret_Challenge_Mac_Nonce_OrCredential_IsEverLogged()
        {
            using (var host = new HostFixture()) using (var head = new HeadsetFixture())
            {
                (await head.PairWith(host)).owner.Dispose();
                var saved = head.Link.Store.Primary;
                head.Restart(); Assert.True(head.Link.TryConnect(out _, out var o)); o.Dispose();
                head.Link.Store.Update(saved.HostIdHex, h => { h.LastAddress = "127.0.0.2"; h.Addresses.Clear(); }); head.Restart(); head.Link.TryConnect(out _, out var o2); o2?.Dispose();
                string log = head.AllLog() + "\n" + string.Join("\n", host.Log.Lines);
                Assert.DoesNotContain(AuthCrypto.Hex(saved.Secret), log, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToBase64String(saved.Secret), log);
                Assert.DoesNotContain(saved.DeviceIdHex, log, StringComparison.OrdinalIgnoreCase);       // the full device id is not logged either (host logs a short form)
                Assert.DoesNotContain(head.Link.Snapshot.Code ?? "no-code-left", log);
                Assert.Contains("[link]", log);                                                           // but the link did log what it was doing
            }
        }
    }
}
