using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XrssHostApp
{
    /// <summary>
    /// XR Spatial Screens Host: the consumer Windows front end for the unchanged host. Normal start-up: one instance per Windows session, the secure (paired LAN) host starts, a tray icon
    /// appears, no console. <c>--background</c> (used by Start with Windows) keeps the window hidden. Developer options (not shown in the UI): <c>--dev</c> also runs the plain loopback listener
    /// used through adb, <c>--port N</c> (dev port, default 5600), <c>--lan-port N</c> (default 5610), <c>--capture PATH</c>.
    /// </summary>
    static class HostApp
    {
        [STAThread]
        static int Main(string[] args)
        {
            string Arg(string k, string d) { int i = Array.IndexOf(args, "--" + k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
            bool background = args.Contains("--background");

            using (var single = new SingleInstance(waitMs: args.Contains("--restarted") ? 15000 : 0))
            {
                if (!single.IsFirst) { single.SignalExisting(); return 0; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string version = (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1)).ToString(3);
                var log = new HostLog(Path.Combine(TransportHost.Dir, "logs"));
                RedirectConsole(log.LogDirectory);
                InstallCrashGuard(log, exe);
                log.Info("XR Spatial Screens Host " + version + " starting" + (background ? " in the background" : ""));

                // the capture helper: next to the app first (installed layout), then the repository layout used in development
                string capture = Arg("capture", null);
                if (capture == null)
                {
                    string[] candidates = { Path.Combine(AppContext.BaseDirectory, "XrssCapture.exe"), Path.Combine(AppContext.BaseDirectory, "XrssCapture", "XrssCapture.exe"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XrssCapture", "publish", "XrssCapture.exe")) };
                    capture = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
                }
                global::Program.CapturePath = Path.GetFullPath(capture);

                var host = new TransportHost(int.Parse(Arg("lan-port", "5610")), log) { PrintToConsole = false };
                var controller = new HostController(host, log) { CapturePath = global::Program.CapturePath };
                controller.Launcher = new HeadsetLauncher(TransportHost.Dir, AppContext.BaseDirectory);
                var auto = new Autostart();
                try { auto.Refresh(exe); } catch (Exception) { }
                controller.StartHost();
                if (args.Contains("--dev"))
                {
                    int devPort = int.Parse(Arg("port", "5600"));
                    new Thread(() => { try { global::Program.RunDevLoop(devPort); } catch (Exception e) { log.Info("developer listener stopped: " + e.Message); } }) { IsBackground = true, Name = "xrss-dev" }.Start();
                    log.Info("developer listener on 127.0.0.1:" + devPort);
                }

                using (var app = new TrayApp(controller, auto, exe, version, showWindow: !background))
                {
                    single.ShowRequested += app.RequestShow;
                    Application.Run(app);
                }
                controller.Dispose();
                log.Info("exited");
            }
            return 0;
        }

        /// <summary>The host's Console.WriteLine output (stream statistics, errors) goes to <c>session.log</c> so it is available for support without a console window. Reset at each start, capped at 1 MB.</summary>
        static void RedirectConsole(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "session.log");
                var w = new StreamWriter(new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(new CappedWriter(w, 1024 * 1024));
            }
            catch (Exception) { }
        }

        sealed class CappedWriter : TextWriter
        {
            readonly TextWriter m_W; readonly long m_Max; long m_N;
            public CappedWriter(TextWriter w, long max) { m_W = TextWriter.Synchronized(w); m_Max = max; }
            public override Encoding Encoding => m_W.Encoding;
            public override void Write(char value) { if (m_N++ < m_Max) m_W.Write(value); }
            public override void Write(string value) { if (value == null) return; if (m_N < m_Max) m_W.Write(value); m_N += value.Length; }
            public override void WriteLine(string value) { Write(value); Write(Environment.NewLine); }
        }

        /// <summary>An unexpected crash writes a note and starts the app again (at most 3 times a minute), so a fault in streaming code does not leave the PC without a host.</summary>
        static void InstallCrashGuard(HostLog log, string exe)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    log.Info("crash: " + (e.ExceptionObject as Exception)?.GetType().Name + ": " + (e.ExceptionObject as Exception)?.Message);
                    string stamp = Path.Combine(log.LogDirectory, "crash.stamp");
                    var recent = (File.Exists(stamp) ? File.ReadAllLines(stamp) : new string[0]).Select(l => long.TryParse(l, out var t) ? t : 0).Where(t => DateTime.UtcNow.Ticks - t < TimeSpan.FromMinutes(1).Ticks).ToList();
                    if (recent.Count >= 3) return;
                    recent.Add(DateTime.UtcNow.Ticks);
                    File.WriteAllLines(stamp, recent.Select(t => t.ToString()));
                    Process.Start(new ProcessStartInfo(exe, "--background --restarted") { UseShellExecute = false });
                }
                catch (Exception) { }
            };
        }
    }
}
