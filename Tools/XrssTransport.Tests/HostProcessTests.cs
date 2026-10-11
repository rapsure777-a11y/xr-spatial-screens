using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using XrssTransport;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Tests
{
    /// <summary>End to end against the REAL host executable: pair, reach the real application protocol, reconnect, revoke. Passes trivially if the host is not built or no private LAN address exists.</summary>
    public class HostProcessTests
    {
        static string HostExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XrssStream", "bin", "Release", "net8.0-windows", "XrssStream.exe"));

        sealed class Host : IDisposable
        {
            public Process P;
            public readonly ConcurrentQueue<string> Out = new ConcurrentQueue<string>();
            public string Dir = Path.Combine(Path.GetTempPath(), "xrss-e2e-" + Guid.NewGuid().ToString("N"));

            public Host(int port, string transport, int devPort)
            {
                var psi = new ProcessStartInfo(HostExe, "--transport " + transport + " --lan-port " + port + " --port " + devPort) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                psi.Environment["XRSS_HOST_DIR"] = Dir; psi.Environment["XRSS_SHOW_CODE"] = "1";
                P = Process.Start(psi);
                P.OutputDataReceived += (s, e) => { if (e.Data != null) Out.Enqueue(e.Data); }; P.BeginOutputReadLine();
                P.ErrorDataReceived += (s, e) => { if (e.Data != null) Out.Enqueue("ERR " + e.Data); }; P.BeginErrorReadLine();
            }

            public void Say(string cmd) { P.StandardInput.WriteLine(cmd); P.StandardInput.Flush(); }

            public async Task<string> WaitFor(string part, int ms = 15000)
            {
                var end = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < end) { var hit = Out.FirstOrDefault(l => l.Contains(part)); if (hit != null) return hit; await Task.Delay(50); }
                throw new TimeoutException("host never printed: " + part + "\n" + string.Join("\n", Out));
            }

            public void Dispose() { try { P.Kill(true); } catch { } try { Directory.Delete(Dir, true); } catch { } }
        }

        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        static async Task<string> ReadMagic(System.IO.Stream s)
        {
            var magic = new byte[4]; int got = 0;
            while (got < 4) { int r = await s.ReadAsync(magic, got, 4 - got); if (r <= 0) break; got += r; }
            return Encoding.ASCII.GetString(magic, 0, got);
        }

        [Fact]
        public async Task RealHost_PairReachProtocolReconnectRevoke()
        {
            var lanAddress = LanInterfaces.PrivateIPv4().FirstOrDefault();
            if (!File.Exists(HostExe) || lanAddress == null) return;
            int port = FreePort();
            using (var host = new Host(port, "lan", FreePort()))
            {
                await host.WaitFor("paired LAN transport ready");
                string fpLine = await host.WaitFor("certificate fingerprint");
                byte[] fp = AuthCrypto.FromHex(fpLine.Substring(fpLine.LastIndexOf(' ') + 1));
                host.Say("status"); await host.WaitFor("listening: False");                 // nothing paired, window closed: not listening
                await Assert.ThrowsAnyAsync<Exception>(async () => { using (await TestClient.Connect(port, null, lanAddress)) { } });

                host.Say("pair"); await host.WaitFor("pairing window open"); await host.WaitFor("lan listener bound");
                byte[] id = AuthCrypto.Random(16), secret = AuthCrypto.Random(32);
                var c = await TestClient.Connect(port, fp, lanAddress);                      // pinned to the fingerprint the host printed
                string headsetCode = await c.BeginPair(id);
                string shown = await host.WaitFor("PAIRING CODE");
                Assert.Contains(headsetCode, shown);                                        // the PC shows the code the headset computed
                host.Say("yes");
                Assert.Equal(MsgType.Ok, await c.ConfirmPair(secret, "E2E headset"));

                // the paired connection now speaks the real protocol: ask for the window list
                await c.Ssl.WriteAsync(new[] { (byte)'W' }, 0, 1); await c.Ssl.FlushAsync();
                Assert.Equal("XRSL", await ReadMagic(c.Ssl));

                // reconnect with the HMAC; the new session replaces the old one
                using (var c2 = await TestClient.Connect(port, fp, lanAddress))
                {
                    Assert.Equal(MsgType.Ok, (await c2.Authenticate(id, secret)).answer);
                    await c2.Ssl.WriteAsync(new[] { (byte)'W' }, 0, 1); await c2.Ssl.FlushAsync();
                    Assert.Equal("XRSL", await ReadMagic(c2.Ssl));

                    host.Say("devices"); await host.WaitFor("E2E headset");
                    host.Say("remove-all"); await host.WaitFor("removed 1 device");
                    await Task.Delay(500);
                    await Assert.ThrowsAnyAsync<Exception>(async () => { for (int i = 0; i < 20; i++) { await c2.Ssl.WriteAsync(new[] { (byte)'W' }, 0, 1); await c2.Ssl.FlushAsync(); await Task.Delay(100); } });   // live connection dropped
                }
                c.Dispose();
                await Task.Delay(300);
                using (var c3 = await TestClient.Connect(port, fp, lanAddress)) Assert.Equal(MsgType.Reject, (await c3.Authenticate(id, secret)).answer);   // still reachable for the grace period, and the old secret is refused: the headset can be told
                Assert.DoesNotContain(AuthCrypto.Hex(secret), string.Join("\n", host.Out), StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task RealHost_DevTransport_StillServesPlainLoopback()
        {
            if (!File.Exists(HostExe)) return;
            int port = FreePort(), devPort = FreePort();
            using (var host = new Host(port, "dev", devPort))                                         // --transport dev is also the default
            {
                await host.WaitFor("listening on 127.0.0.1:" + devPort);
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, devPort);
                    var s = tcp.GetStream(); await s.WriteAsync(new[] { (byte)'W' }, 0, 1);       // no TLS, no pairing: straight to the application protocol
                    Assert.Equal("XRSL", await ReadMagic(s));
                }
                Assert.DoesNotContain(host.Out, l => l.Contains("paired LAN transport"));       // dev mode never starts the secure listener
            }
        }
    }
}
