using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using XrSpatial.App;
using XrssTransport;
using XrssTransport.Client;

namespace XrSpatial.Tests
{
    /// <summary>
    /// The headset-side transport as compiled by Unity: known vectors (shared with the host's tests, see Docs/PAIRING_TEST_VECTORS.md), persistence, mDNS parsing, the plain dev path, and what the
    /// connection page offers. The full protocol against the real PC host listener is exercised by Tools/XrssTransport.Tests, which compiles these same client files.
    /// </summary>
    public class PairingClientTests
    {
        sealed class ScriptedDiscovery : IDiscovery
        {
            public List<DiscoveredPc> Result = new List<DiscoveredPc>();
            public List<DiscoveredPc> Discover(int timeoutMs, Func<List<DiscoveredPc>, bool> enough) => new List<DiscoveredPc>(Result);
        }

        string m_Root;
        SimpleClientEnv m_Env;

        [SetUp] public void SetUp() { m_Root = Path.Combine(Path.GetTempPath(), "xrss-unity-" + Guid.NewGuid().ToString("N")); m_Env = new SimpleClientEnv { SecretDir = Path.Combine(m_Root, "secret"), SettingsDir = Path.Combine(m_Root, "settings") }; }
        [TearDown] public void TearDown() { try { Directory.Delete(m_Root, true); } catch { } }

        static byte[] Range(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

        [Test]
        public void Vectors_MatchTheHostsAndTheIndependentImplementation()
        {
            Assert.IsTrue(VectorSelfTest.Run(out string detail), detail);
            Assert.AreEqual("4B4C0724A544C9147BCA888F9ED853D779BDE968EA7AF0405911EE9DB0634D30", AuthCrypto.Hex(AuthCrypto.AuthMac(Range(1, 32), Range(0xA0, 16), Range(0xB0, 16), Range(0xC0, 32))));
            Assert.AreEqual("CB7C0122587F929E5610667A913CBDB3446B3C08799F61688055D7FC420A419B", AuthCrypto.Hex(AuthCrypto.Commit(Range(0x40, 32))));
            Assert.AreEqual("165978", AuthCrypto.Sas(Range(0x80, 32), Range(0x40, 32), Range(0x60, 32), Range(0xB0, 16)));
            Assert.AreEqual("BCE0AFF19CF5AA6A7469A30D61D04E4376E4BBF6381052EE9E7F33925C954D52", AuthCrypto.Hex(AuthCrypto.Fingerprint(Range(0, 100))));
        }

        [Test]
        public void DeviceId_Persists_AndOnlyResetChangesIt()
        {
            var a = HeadsetIdentity.LoadOrCreate(m_Env.SecretDir); string id = a.DeviceIdHex;
            Assert.AreEqual(32, id.Length);
            Assert.AreEqual(id, HeadsetIdentity.LoadOrCreate(m_Env.SecretDir).DeviceIdHex);
            a.Reset(); Assert.AreNotEqual(id, a.DeviceIdHex);
        }

        [Test]
        public void PairedHost_Persists()
        {
            var host = new PairedHost { HostIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)), FingerprintHex = AuthCrypto.Hex(AuthCrypto.Random(32)), DeviceIdHex = AuthCrypto.Hex(AuthCrypto.Random(16)), Secret = AuthCrypto.Random(32), LastAddress = "10.0.0.5", LastPort = 5610, DisplayName = "Study PC", PairedUtc = DateTime.UtcNow };
            new PairedHostStore(m_Env.SecretDir).Save(host);
            var back = new PairedHostStore(m_Env.SecretDir).Primary;
            Assert.AreEqual(host.HostIdHex, back.HostIdHex); Assert.AreEqual(host.FingerprintHex, back.FingerprintHex); CollectionAssert.AreEqual(host.Secret, back.Secret);
            Assert.AreEqual("Study PC", back.DisplayName); Assert.AreEqual(5610, back.LastPort);
        }

        [Test]
        public void Mdns_ResponseRoundTrips_AndGarbageIsHarmless()
        {
            string id = AuthCrypto.Hex(AuthCrypto.Random(16));
            var pkt = MdnsPacket.BuildResponse("Study PC", "xrss-study-pc", 5610, new[] { new KeyValuePair<string, string>("id", id), new KeyValuePair<string, string>("name", "Study PC") }, new List<IPAddress> { IPAddress.Parse("10.0.0.224"), IPAddress.Parse("10.35.78.22") });
            var c = new MdnsCollector(); c.Add(MdnsPacket.Parse(pkt)); c.Add(MdnsPacket.Parse(pkt));
            var r = c.Results();
            Assert.AreEqual(1, r.Count); Assert.AreEqual(id, r[0].HostIdHex); Assert.AreEqual(5610, r[0].Port); Assert.AreEqual(2, r[0].Addresses.Count);
            var rnd = new System.Random(3);
            for (int i = 0; i < 100; i++) { var d = new byte[rnd.Next(0, 120)]; rnd.NextBytes(d); Assert.DoesNotThrow(() => MdnsPacket.Parse(d)); }
        }

        [Test]
        public void DevMode_UsesPlainTcp_AndNeverTouchesPairing()
        {
            var plain = new TcpListener(IPAddress.Loopback, 0); plain.Start();
            try
            {
                var link = new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.Dev) { DevPort = ((IPEndPoint)plain.LocalEndpoint).Port };
                Assert.IsTrue(link.TryConnect(out var stream, out var owner));
                using (owner)
                {
                    var server = plain.AcceptTcpClient();
                    stream.Write(new[] { (byte)'W' }, 0, 1);
                    var b = new byte[1]; Assert.AreEqual(1, server.GetStream().Read(b, 0, 1)); Assert.AreEqual((byte)'W', b[0]);
                }
                Assert.AreEqual(LinkState.Dev, link.Snapshot.State); Assert.AreEqual(0, link.Store.Count);
            }
            finally { plain.Stop(); }
        }

        [Test]
        public void ModeSetting_IsRememberedAcrossRestarts()
        {
            var link = new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.Dev);
            link.SetMode(LinkMode.PairedLan);
            Assert.AreEqual(LinkMode.PairedLan, new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.Dev).Mode);
        }

        [Test]
        public void NotPaired_OffersSearchAndTypedAddress_AndListsFoundPcs()
        {
            var link = new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.PairedLan);
            Assert.IsFalse(link.TryConnect(out _, out _));
            Assert.AreEqual(LinkState.NotPaired, link.Snapshot.State);
            var ui = new ConnectionPageState();
            var snap = link.Snapshot;
            snap.Found = new List<DiscoveredPc> { new DiscoveredPc { DisplayName = "Study PC", Port = 5610, Addresses = { IPAddress.Parse("10.0.0.224") } } };
            var v = ConnectionPage.Build(snap, link, ui, () => { });
            var labels = v.Actions.Select(a => a.Label).ToList();
            Assert.IsTrue(labels.Any(l => l.StartsWith("Pair with Study PC")), string.Join("|", labels));
            Assert.Contains("Search again", labels); Assert.Contains("Type address...", labels);
        }

        [Test]
        public void CodeScreen_NeedsAnExplicitAnswer_AndNeverConfirmsItself()
        {
            var link = new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.PairedLan);
            var snap = new LinkSnapshot { Mode = LinkMode.PairedLan, State = LinkState.Pairing, Step = PairStep.ShowCode, Code = "165978" };
            var v = ConnectionPage.Build(snap, link, new ConnectionPageState(), () => { });
            Assert.AreEqual("165 978", v.Code);
            CollectionAssert.AreEqual(new[] { "Same code", "Doesn't match" }, v.Actions.Select(a => a.Label).ToArray());
            Assert.AreEqual(PairStep.None, link.Snapshot.Step);           // building the view confirmed nothing
        }

        [Test]
        public void RevokedState_OffersPairAgain_NotARetryLoop()
        {
            var link = new PcLink(m_Env, new ScriptedDiscovery(), LinkMode.PairedLan);
            var snap = new LinkSnapshot { Mode = LinkMode.PairedLan, State = LinkState.Error, Error = LinkError.Removed, Paired = true, Text = "This PC no longer trusts this headset. Pair again.", PcName = "Study PC" };
            var v = ConnectionPage.Build(snap, link, new ConnectionPageState(), () => { });
            var labels = v.Actions.Select(a => a.Label).ToList();
            Assert.IsTrue(labels.Any(l => l.StartsWith("Search") || l.StartsWith("Pair")), string.Join("|", labels));
            CollectionAssert.DoesNotContain(labels, "Retry now");
            StringAssert.Contains("removed", v.Title);
        }

        [Test]
        public void Keypad_BuildsAnAddress()
        {
            var ui = new ConnectionPageState();
            foreach (var k in new[] { "1", "9", "2", ".", "1", "6", "8", ".", "1", ".", "5", "<", "7" }) ConnectionPage.Key(ui, k);
            Assert.AreEqual("192.168.1.7", ui.Typed);
            PcLink.ParseAddress("192.168.1.7", out string a, out int p); Assert.AreEqual("192.168.1.7", a); Assert.AreEqual(PcLink.LanPort, p);
            PcLink.ParseAddress("192.168.1.7:6000", out a, out p); Assert.AreEqual(6000, p);
        }
    }
}
