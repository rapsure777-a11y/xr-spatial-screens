using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Xunit;
using XrssHostApp;
using XrssTransport;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Tests
{
    /// <summary>
    /// The Windows host app's logic (HostController over the real TransportHost, real listener on loopback, scripted headset clients). The window itself is a thin view of HostStatus; what it
    /// shows and every action behind its buttons is exercised here through the same controller the window uses.
    /// </summary>
    [Collection("host-app")]
    public class HostAppTests
    {
        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        sealed class Fx : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "xrss-app-tests-" + Guid.NewGuid().ToString("N"));
            public readonly int Port = FreePort();
            public readonly HostLog Log;
            public readonly TransportHost Host;
            public readonly HostController C;
            public int Sessions;

            public Fx(Func<string, RouteInfo> route = null, IReadOnlyList<IPAddress> addresses = null)
            {
                Log = new HostLog(Path.Combine(Dir, "logs"));
                Host = new TransportHost(Port, Log, Dir) { EnableMdns = false, PrintToConsole = false, AddressProvider = () => addresses ?? new[] { IPAddress.Loopback } };
                Host.SessionRunner = c => { Interlocked.Increment(ref Sessions); var b = new byte[1]; try { while (c.Stream.Read(b, 0, 1) > 0) { } } catch (Exception) { } };
                C = new HostController(Host, Log, route: route);
            }

            public static async Task<bool> Eventually(Func<bool> cond, int ms = 6000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { if (cond()) return true; await Task.Delay(25); } return cond(); }

            /// <summary>An honest pairing driven through the controller: Pair Headset, the headset shows its code, the PC user presses Same code, the headset confirms.</summary>
            public async Task<(byte[] id, byte[] secret, TestClient client)> Pair(string label = "Steam Frame")
            {
                Assert.True(C.PairHeadset());
                byte[] id = AuthCrypto.Random(16), secret = AuthCrypto.Random(32);
                var c = await TestClient.Connect(Port);
                string code = await c.BeginPair(id);
                Assert.True(await Eventually(() => C.Snapshot().Stage == PairStage.CodeShown));
                Assert.Equal(code, C.Snapshot().Code);
                Assert.True(C.SameCode());
                Assert.Equal(MsgType.Ok, await c.ConfirmPair(secret, label));
                Assert.True(await Eventually(() => C.Snapshot().HeadsetConnected));
                return (id, secret, c);
            }

            public void Dispose() { C.Dispose(); Host.Stop(); try { Directory.Delete(Dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ host starts, pairing window

        [Fact]
        public void Host_app_starts_the_host_core_and_reports_running()
        {
            using (var fx = new Fx())
            {
                var before = fx.C.Snapshot();
                Assert.False(before.Running); Assert.Equal("Host stopped", before.Headline);
                fx.C.StartHost();
                var st = fx.C.Snapshot();
                Assert.True(st.Running); Assert.Equal("Waiting for headset", st.Headline); Assert.False(st.HeadsetConnected);
                Assert.False(st.ListenerBound);                                   // nothing paired and no pairing open: nothing is exposed on the network
            }
        }

        [Fact]
        public void Pair_Headset_opens_the_pairing_window_and_the_port()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                Assert.True(fx.C.PairHeadset());
                var st = fx.C.Snapshot();
                Assert.True(st.PairingOpen); Assert.Equal(PairStage.WaitingForHeadset, st.Stage); Assert.Equal("Pairing", st.Headline);
                Assert.InRange(st.PairingRemaining.TotalSeconds, 100, 121);       // the existing two minutes
                Assert.True(fx.Host.Window.IsOpen);
                Assert.True(fx.Host.Listener.IsListening);
            }
        }

        [Fact]
        public async Task Pairing_countdown_expires_and_no_enrollment_is_possible_afterwards()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                fx.C.PairHeadset(TimeSpan.FromMilliseconds(700));
                Assert.True(fx.C.Snapshot().PairingOpen);
                await Task.Delay(1000);
                var st = fx.C.Snapshot();
                Assert.False(st.PairingOpen); Assert.Equal(PairStage.Expired, st.Stage); Assert.Contains("ran out", st.PairMessage);
                Assert.Null(fx.Host.Window.TryBegin("1.2.3.4"));                  // Host Core refuses a ceremony in a closed window
                Assert.True(fx.C.PairHeadset());                                  // and the user can start again
                Assert.Equal(PairStage.WaitingForHeadset, fx.C.Snapshot().Stage);
            }
        }

        // ------------------------------------------------------------------ the code

        [Fact]
        public async Task Same_code_calls_the_existing_confirmation_and_enrolls_the_device()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                var (id, secret, client) = await fx.Pair("Test Frame");
                using (client)
                {
                    var st = fx.C.Snapshot();
                    Assert.Equal(PairStage.Succeeded, st.Stage); Assert.Contains("Test Frame", st.PairMessage);
                    Assert.False(st.PairingOpen);                                 // the window closed itself
                    Assert.Null(st.Code);                                         // the code is gone from the view once used
                    Assert.Single(st.Devices); Assert.Equal("Test Frame", st.Devices[0].Label); Assert.True(st.Devices[0].Connected);
                    Assert.True(fx.Host.Store.Contains(AuthCrypto.Hex(id), fx.Host.Store.List()[0].HostId));
                }
            }
        }

        [Fact]
        public async Task Same_code_without_a_waiting_code_does_nothing()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost(); fx.C.PairHeadset();
                Assert.False(fx.C.SameCode());                                    // nothing waiting: not auto-confirmed, not queued
                Assert.False(fx.C.DoesNotMatch());
                await Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Doesnt_match_rejects_and_enrolls_nothing()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost(); fx.C.PairHeadset();
                byte[] id = AuthCrypto.Random(16), secret = AuthCrypto.Random(32);
                using (var c = await TestClient.Connect(fx.Port))
                {
                    await c.BeginPair(id);
                    Assert.True(await Fx.Eventually(() => fx.C.Snapshot().Stage == PairStage.CodeShown));
                    Assert.True(fx.C.DoesNotMatch());
                    var r = await c.ConfirmPair(secret, "Nope");
                    Assert.Equal(MsgType.Reject, r);
                    Assert.True(await Fx.Eventually(() => fx.C.Snapshot().Stage == PairStage.WaitingForHeadset && fx.C.Snapshot().PairMessage != null));
                    var st = fx.C.Snapshot();
                    Assert.Contains("didn't match", st.PairMessage); Assert.Empty(st.Devices);
                    Assert.True(st.PairingOpen);                                  // one failure does not close the window
                    Assert.DoesNotContain("Exception", st.PairMessage);           // customer wording, no technical terms
                }
            }
        }

        [Fact]
        public async Task Three_failed_ceremonies_close_the_window_exactly_as_host_core_defines()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost(); fx.C.PairHeadset();
                for (int i = 0; i < 3; i++)
                {
                    using (var c = await TestClient.Connect(fx.Port))
                    {
                        await c.BeginPair(AuthCrypto.Random(16));
                        Assert.True(await Fx.Eventually(() => fx.C.Snapshot().Stage == PairStage.CodeShown));
                        fx.C.DoesNotMatch();
                        await c.ConfirmPair(AuthCrypto.Random(32), "x");
                        await Fx.Eventually(() => fx.Host.Window.Failures >= i + 1 || !fx.Host.Window.IsOpen);
                    }
                }
                var st = fx.C.Snapshot();
                Assert.False(st.PairingOpen); Assert.Equal(PairStage.Failed, st.Stage); Assert.Contains("Pair Headset", st.PairMessage);
            }
        }

        // ------------------------------------------------------------------ devices

        [Fact]
        public void Paired_devices_come_from_the_existing_device_store()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                fx.Host.Store.Enroll("AABBCCDD" + new string('0', 24), "Living room Frame", "11".PadRight(32, '2'), AuthCrypto.Random(32));
                var st = fx.C.Snapshot();
                Assert.Single(st.Devices);
                var d = st.Devices[0];
                Assert.Equal("Living room Frame", d.Label); Assert.Equal("AABBCC", d.ShortId); Assert.False(d.Connected); Assert.Null(d.LastConnectedUtc);
                Assert.True(fx.Host.Listener.IsListening);                        // something is paired: the secure listener is open for reconnects
            }
        }

        [Fact]
        public async Task Remove_revokes_drops_the_connection_and_requires_pairing_again()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                string removedLabel = null; fx.Host.DeviceRemoved += l => removedLabel = l;
                var (id, secret, client) = await fx.Pair("Doomed");
                using (client)
                {
                    string idHex = AuthCrypto.Hex(id);
                    Assert.True(fx.C.Remove(idHex));
                    Assert.True(await Fx.Eventually(() => !fx.C.Snapshot().HeadsetConnected));      // the live connection was dropped at once
                    Assert.Empty(fx.C.Snapshot().Devices);
                    Assert.Equal("Doomed", removedLabel);
                    Assert.Contains("removed", fx.C.Snapshot().Notice);
                    using (var again = await TestClient.Connect(fx.Port))                            // the removed headset is told, not ignored
                    {
                        var r = await again.Authenticate(id, secret);
                        Assert.Equal(MsgType.Reject, r.answer); Assert.Equal((byte)RejectCode.AuthFailed, r.code);
                    }
                }
            }
        }

        [Fact]
        public async Task Remove_All_revokes_every_device()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                var (id1, s1, c1) = await fx.Pair("One");
                using (c1)
                {
                    var (id2, s2, c2) = await fx.Pair("Two");
                    using (c2)
                    {
                        Assert.Equal(2, fx.C.Snapshot().Devices.Count);
                        Assert.Equal(2, fx.C.RemoveAll());
                        Assert.True(await Fx.Eventually(() => !fx.C.Snapshot().HeadsetConnected));
                        Assert.Empty(fx.C.Snapshot().Devices);
                        foreach (var (id, s) in new[] { (id1, s1), (id2, s2) })
                            using (var c = await TestClient.Connect(fx.Port)) Assert.Equal(MsgType.Reject, (await c.Authenticate(id, s)).answer);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ connection state

        [Fact]
        public async Task Connected_state_updates_with_headset_label_and_route()
        {
            using (var fx = new Fx(route: a => new RouteInfo { Kind = "Dongle", Adapter = "Test adapter", Local = a }))
            {
                fx.C.StartHost();
                var (id, secret, client) = await fx.Pair("Frame A");
                client.Dispose();
                Assert.True(await Fx.Eventually(() => !fx.C.Snapshot().HeadsetConnected));
                Assert.Equal("Reconnecting", fx.C.Snapshot().Headline);                              // it just dropped; the headset reconnects by itself
                using (var c = await TestClient.Connect(fx.Port))
                {
                    var r = await c.Authenticate(id, secret);
                    Assert.Equal(MsgType.Ok, r.answer);
                    Assert.True(await Fx.Eventually(() => fx.C.Snapshot().HeadsetConnected));
                    var st = fx.C.Snapshot();
                    Assert.Equal("Connected", st.Headline); Assert.Equal("Frame A", st.HeadsetLabel); Assert.Equal("Dongle", st.Connection);
                    Assert.Contains("127.0.0.1", st.ConnectionDetail);
                    Assert.NotNull(st.Devices[0].LastConnectedUtc);
                }
                Assert.True(await Fx.Eventually(() => !fx.C.Snapshot().HeadsetConnected));
            }
        }

        [Fact]
        public void Route_classification_is_conservative()
        {
            var adapters = new[]
            {
                new Route.Adapter { Address = "10.35.78.22", Name = "Wi-Fi 6", Description = "Realtek 8832CU Wireless LAN WiFi 6 USB NIC For Valve Up" },
                new Route.Adapter { Address = "10.0.0.224", Name = "Ethernet", Description = "Realtek PCIe 2.5GbE Family Controller" },
            };
            Func<IEnumerable<Route.Adapter>> src = () => adapters;
            Assert.Equal("Dongle", Route.Classify("10.35.78.22", src).Kind);
            Assert.Equal("Wi-Fi / LAN", Route.Classify("10.0.0.224", src).Kind);
            Assert.Null(Route.Classify("192.168.9.9", src).Kind);                  // unknown: no guess
            Assert.Null(Route.Classify(null, src).Kind);
        }

        // ------------------------------------------------------------------ start / stop

        [Fact]
        public async Task Stopping_closes_the_listener_and_the_connection_but_keeps_pairings_and_starting_restores_it()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                var (id, secret, client) = await fx.Pair("Keeper");
                using (client)
                {
                    fx.C.StopHost();
                    Assert.False(fx.Host.Listener.IsListening);
                    Assert.True(await Fx.Eventually(() => !fx.C.Snapshot().HeadsetConnected));
                    var st = fx.C.Snapshot();
                    Assert.False(st.Running); Assert.Equal("Host stopped", st.Headline);
                    Assert.Single(st.Devices);                                                        // pairings stay
                    await Assert.ThrowsAnyAsync<Exception>(() => TestClient.Connect(fx.Port));        // nobody can connect to a stopped host

                    fx.C.StartHost();
                    Assert.True(await Fx.Eventually(() => fx.Host.Listener.IsListening));
                    using (var c = await TestClient.Connect(fx.Port))
                        Assert.Equal(MsgType.Ok, (await c.Authenticate(id, secret)).answer);          // the same pairing works again
                }
            }
        }

        [Fact]
        public void Pair_Headset_on_a_stopped_host_starts_it()
        {
            using (var fx = new Fx())
            {
                Assert.True(fx.C.PairHeadset());
                Assert.True(fx.C.Snapshot().Running); Assert.True(fx.C.Snapshot().PairingOpen);
            }
        }

        // ------------------------------------------------------------------ errors

        [Fact]
        public void A_port_in_use_is_reported_instead_of_failing_silently()
        {
            using (var fx = new Fx())
            using (var blocker = new TcpListener(IPAddress.Loopback, fx.Port))
            {
                blocker.Start();
                fx.C.StartHost(); fx.C.PairHeadset();
                var st = fx.C.Snapshot();
                Assert.False(st.ListenerBound);
                Assert.Contains("in use", st.Notice);
            }
        }

        [Fact]
        public void No_private_network_is_explained()
        {
            using (var fx = new Fx(addresses: new IPAddress[0]))
            {
                fx.C.StartHost();
                Assert.Contains("No private network", fx.C.Snapshot().Notice);
            }
        }

        [Fact]
        public void A_network_change_rebuilds_the_listener_but_a_stopped_host_stays_stopped()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                var now = new List<IPAddress> { IPAddress.Loopback };
                fx.Host.AddressProvider = () => now;
                fx.C.RefreshNetwork(false);                                        // same addresses: nothing happens
                Assert.Equal(1, fx.Host.Addresses.Count);
                now.Add(IPAddress.Parse("127.0.0.2"));
                fx.C.RefreshNetwork(false);
                Assert.Equal(2, fx.Host.Addresses.Count);
                fx.C.StopHost();
                fx.C.RefreshNetwork(true);
                Assert.False(fx.C.Snapshot().Running);
            }
        }

        [Fact]
        public void A_corrupt_identity_is_replaced_and_the_user_is_told()
        {
            using (var fx = new Fx())
            {
                Directory.CreateDirectory(fx.Dir);
                File.WriteAllBytes(Path.Combine(fx.Dir, "host-identity.dat"), new byte[] { 1, 2, 3, 4 });
                fx.C.StartHost();
                Assert.True(fx.C.Snapshot().Running);
                Assert.Contains("paired again", fx.C.Snapshot().Notice, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ------------------------------------------------------------------ nothing sensitive in the UI

        [Fact]
        public async Task The_view_and_diagnostics_never_contain_secrets_or_codes()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                var (id, secret, client) = await fx.Pair("Private Frame");
                using (client)
                {
                    string code = client.Nc == null ? "" : AuthCrypto.Sas(client.SeenFingerprint, client.Nc, client.Nh, id);
                    var st = fx.C.Snapshot();
                    var texts = new List<string> { st.Headline, st.Detail, st.Notice, st.HeadsetLabel, st.Connection, st.ConnectionDetail, st.PairMessage, st.LocalAddress, fx.C.DiagnosticsText("0.0.0") };
                    foreach (var d in st.Devices) texts.AddRange(new[] { d.DeviceId.Substring(0, 6), d.ShortId, d.Label });
                    string all = string.Join("\n", texts.Where(t => t != null));
                    Assert.DoesNotContain(AuthCrypto.Hex(secret), all, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(Convert.ToBase64String(secret), all);
                    Assert.DoesNotContain(AuthCrypto.Hex(client.Nc), all, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(AuthCrypto.Hex(client.Nh), all, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(AuthCrypto.Hex(id), all, StringComparison.OrdinalIgnoreCase);        // not even the full device id
                    Assert.DoesNotContain(code, all.Replace("127.0.0.1", ""));
                    Assert.DoesNotContain("SecretProtected", all);
                    Assert.DoesNotContain("HMAC", all, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("PinMismatch", all);
                    Assert.All(fx.Host.Store.List(), d => Assert.Null(d.SecretProtected));                    // the list API never hands the protected blob out either
                    string logFile = File.ReadAllText(Path.Combine(fx.Dir, "logs", "host.log"));
                    Assert.DoesNotContain(AuthCrypto.Hex(secret), logFile, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(code, logFile.Replace("127.0.0.1", ""));
                }
            }
        }

        // ------------------------------------------------------------------ autostart and single instance

        [Fact]
        public void Autostart_writes_and_removes_exactly_one_per_user_run_value()
        {
            string key = @"Software\XrSpatialScreens.Tests\Run-" + Guid.NewGuid().ToString("N");
            try
            {
                var a = new Autostart(key);
                Assert.False(a.IsEnabled);
                a.Enable(@"C:\Program Files\X\XrSpatialScreensHost.exe");
                Assert.True(a.IsEnabled);
                Assert.Equal("\"C:\\Program Files\\X\\XrSpatialScreensHost.exe\" --background", a.Command);          // quiet start in the tray, quoted path
                using (var k = Registry.CurrentUser.OpenSubKey(key)) Assert.Equal(new[] { Autostart.ValueName }, k.GetValueNames());
                a.Enable(@"C:\Other\XrSpatialScreensHost.exe");                                                    // enabling twice never duplicates
                using (var k = Registry.CurrentUser.OpenSubKey(key)) Assert.Single(k.GetValueNames());
                a.Refresh(@"C:\Moved\XrSpatialScreensHost.exe");
                Assert.Contains(@"C:\Moved", a.Command);
                a.Disable();
                Assert.False(a.IsEnabled);
                a.Disable();                                                                                       // already off: no error
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\XrSpatialScreens.Tests", false); }
        }

        [Fact]
        public void The_real_run_key_is_the_current_user_one_and_needs_no_admin()
        {
            Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunKey);          // HKCU, never HKLM
        }

        [Fact]
        public async Task A_second_instance_signals_the_first_and_does_not_become_first()
        {
            string name = "XrssTest-" + Guid.NewGuid().ToString("N");
            using (var first = new SingleInstance(name))
            {
                Assert.True(first.IsFirst);
                var shown = new ManualResetEventSlim();
                first.ShowRequested += shown.Set;
                // a mutex is re-entrant on the thread that owns it, so the second launch runs on another thread (in the product it is another process)
                bool secondIsFirst = await Task.Run(() => { using (var second = new SingleInstance(name)) { second.SignalExisting(); return second.IsFirst; } });
                Assert.False(secondIsFirst);
                Assert.True(shown.Wait(3000));
            }
            using (var again = new SingleInstance(name)) Assert.True(again.IsFirst);                  // once the first is gone a new one may start
        }

        [Fact]
        public async Task Launching_the_real_app_twice_leaves_one_process_and_one_listener()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "XrSpatialScreensHost.exe");
            Assert.True(File.Exists(exe), exe);
            string dir = Path.Combine(Path.GetTempPath(), "xrss-app-2x-" + Guid.NewGuid().ToString("N"));
            int lanPort = FreePort();
            Process Start()
            {
                var psi = new ProcessStartInfo(exe, "--background --lan-port " + lanPort) { UseShellExecute = false };
                psi.Environment["XRSS_HOST_DIR"] = dir;
                return Process.Start(psi);
            }
            Process a = null, b = null;
            try
            {
                a = Start();
                await Task.Delay(2500);
                Assert.False(a.HasExited);
                b = Start();
                Assert.True(b.WaitForExit(8000), "the second launch should exit by itself");
                Assert.Equal(0, b.ExitCode);
                Assert.False(a.HasExited);                                                           // the first keeps running
            }
            finally { try { a?.Kill(true); } catch { } try { b?.Kill(true); } catch { } await Task.Delay(300); try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ console compatibility

        [Fact]
        public async Task The_console_admin_commands_still_work_on_the_same_core()
        {
            using (var fx = new Fx())
            {
                fx.C.StartHost();
                Assert.Contains("open", fx.Host.Execute("pair 30"));
                Assert.True(fx.Host.Window.IsOpen); Assert.Equal(PairStage.None, fx.C.Snapshot().Stage == PairStage.WaitingForHeadset ? PairStage.None : fx.C.Snapshot().Stage);
                Assert.Contains("status", fx.Host.Execute("help").ToLowerInvariant());
                Assert.Contains("no pairing code is waiting", fx.Host.Execute("yes"));
                Assert.Equal("pairing closed", fx.Host.Execute("pair-close"));
                Assert.Contains("no paired devices", fx.Host.Execute("devices"));
                await Task.CompletedTask;
            }
        }
    }

    [CollectionDefinition("host-app", DisableParallelization = true)]
    public class HostAppCollection { }
}
