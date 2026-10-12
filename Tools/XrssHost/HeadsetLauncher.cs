using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace XrssHostApp
{
    /// <summary>The outcome of one "Launch on Headset" attempt, worded for the user.</summary>
    sealed class LaunchResult
    {
        public bool Ok;
        public string Message;
    }

    /// <summary>
    /// Starts the headset app from the PC over adb. The app is a development build that is not listed in the headset's own menu, so this is the only way to start it
    /// (for example after the headset closed it because the wearer walked out of range). Addresses are remembered from the headset's real connections, with the
    /// Steam Frame dongle's fixed address as a fallback. All process work goes through <see cref="Run"/> so tests can replace it.
    /// </summary>
    sealed class HeadsetLauncher
    {
        public const string Package = "com.gamebreaklabs.xrspatialscreens", Activity = "com.unity3d.player.UnityPlayerGameActivity";
        public const int AdbPort = 5555;
        public const string DongleAddress = "10.35.78.1";

        readonly string m_Dir;
        readonly string m_AppDir;
        readonly object m_Lock = new object();
        int m_Busy;

        /// <summary>Runs a program and returns its exit code and combined output. Replaced in tests.</summary>
        public Func<string, string, int, (int code, string output)> Run = RunProcess;

        public HeadsetLauncher(string stateDir, string appDir) { m_Dir = stateDir; m_AppDir = appDir; }

        string AddressFile => Path.Combine(m_Dir, "headset-addresses.txt");

        /// <summary>adb.exe: next to the app, then XRSS_ADB, then the Android tools this developer PC has, then PATH. Null if none.</summary>
        public string FindAdb()
        {
            var list = new List<string> { Path.Combine(m_AppDir ?? "", "adb.exe"), Environment.GetEnvironmentVariable("XRSS_ADB") };
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(Path.Combine(profile, "UnityAndroid", "SDK", "platform-tools", "adb.exe"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            list.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (dir.Length > 0) list.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
            return list.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        }

        /// <summary>Remembers an address a headset really connected from (newest first, at most 4). Ignores anything that is not a plain IPv4 address.</summary>
        public void Remember(string address)
        {
            if (System.Net.IPAddress.TryParse(address ?? "", out var parsed) && parsed.IsIPv4MappedToIPv6) address = parsed.MapToIPv4().ToString();
            if (!IsIpv4(address)) return;
            lock (m_Lock)
            {
                try
                {
                    var all = Remembered();
                    all.Remove(address); all.Insert(0, address);
                    Directory.CreateDirectory(m_Dir);
                    File.WriteAllLines(AddressFile, all.Take(4));
                }
                catch (Exception) { }
            }
        }

        List<string> Remembered()
        {
            try { if (File.Exists(AddressFile)) return File.ReadAllLines(AddressFile).Select(l => l.Trim()).Where(IsIpv4).Distinct().ToList(); } catch (Exception) { }
            return new List<string>();
        }

        /// <summary>The addresses to try, in order: the dongle first when it was ever seen, then the others remembered, then the dongle's fixed address.</summary>
        public List<string> Candidates()
        {
            var all = Remembered();
            var ordered = all.Where(a => a.StartsWith("10.35.")).Concat(all.Where(a => !a.StartsWith("10.35."))).ToList();
            if (!ordered.Contains(DongleAddress)) ordered.Add(DongleAddress);
            return ordered;
        }

        static bool IsIpv4(string s) => System.Net.IPAddress.TryParse(s ?? "", out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        /// <summary>Starts the app on the headset. Only one attempt runs at a time (a second click while one is running returns at once).</summary>
        public Task<LaunchResult> LaunchAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref m_Busy, 1, 0) != 0) return Task.FromResult(new LaunchResult { Ok = false, Message = "Already trying to start the headset app..." });
            return Task.Run(() => { try { return Launch(); } finally { System.Threading.Volatile.Write(ref m_Busy, 0); } });
        }

        public LaunchResult Launch()
        {
            string adb = FindAdb();
            if (adb == null) return new LaunchResult { Message = "Can't find the Android tools (adb.exe). Put adb.exe next to this app." };
            string target = Connect(adb);
            if (target == null) return Unreachable;

            if (IsRunning(adb, target)) return new LaunchResult { Ok = true, Message = "The headset app is already running." };
            var s = Run(adb, "-s " + target + " shell am start -n " + Package + "/" + Activity, 10000);
            if (s.code != 0 || (s.output ?? "").Contains("Error")) return new LaunchResult { Message = "The headset didn't start the app: " + FirstLine(s.output) };
            for (int i = 0; i < 6; i++)
            {
                System.Threading.Thread.Sleep(i == 0 ? 500 : 1000);
                if (IsRunning(adb, target)) return new LaunchResult { Ok = true, Message = "Started the app on the headset." };
            }
            return new LaunchResult { Message = "The headset was told to start the app, but it isn't running yet. Check the headset, then try again." };
        }

        static readonly LaunchResult Unreachable = new LaunchResult { Message = "Couldn't reach the headset. Make sure it is on and in range (and that developer mode is still on), then try again." };

        /// <summary>The first candidate address adb can connect to ("ip:port"), or null.</summary>
        string Connect(string adb)
        {
            foreach (string ip in Candidates())
            {
                var c = Run(adb, "connect " + ip + ":" + AdbPort, 6000);
                string o = c.output ?? "";
                if (c.code == 0 && (o.Contains("connected to") || o.Contains("already connected"))) return ip + ":" + AdbPort;
            }
            return null;
        }

        /// <summary>The headset app package (APK): next to this app, then the repository's headset build folder. Null if there is none.</summary>
        public string FindApk()
        {
            var list = new List<string> { Path.Combine(m_AppDir ?? "", "XrSpatialScreens.apk") };
            try { if (!string.IsNullOrEmpty(m_AppDir)) list.Add(Path.GetFullPath(Path.Combine(m_AppDir, "..", "Headset", "XrSpatialScreens.apk"))); } catch (Exception) { }
            return list.FirstOrDefault(File.Exists);
        }

        /// <summary>Installs (or updates, keeping the app's saved data) the headset app from the APK that ships with this app, then starts it.</summary>
        public Task<LaunchResult> InstallAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref m_Busy, 1, 0) != 0) return Task.FromResult(new LaunchResult { Ok = false, Message = "Already working on the headset..." });
            return Task.Run(() => { try { return Install(); } finally { System.Threading.Volatile.Write(ref m_Busy, 0); } });
        }

        public LaunchResult Install()
        {
            string adb = FindAdb();
            if (adb == null) return new LaunchResult { Message = "Can't find the Android tools (adb.exe). Put adb.exe next to this app." };
            string apk = FindApk();
            if (apk == null) return new LaunchResult { Message = "The headset app package (XrSpatialScreens.apk) isn't next to this app, so there is nothing to install." };
            string target = Connect(adb);
            if (target == null) return Unreachable;
            var i = Run(adb, "-s " + target + " install -r \"" + apk + "\"", 180000);          // -r keeps the app's saved layouts and pairing
            string o = i.output ?? "";
            if (i.code != 0 || !o.Contains("Success")) return new LaunchResult { Message = "The headset didn't accept the install: " + FailureLine(o) };
            var s = Run(adb, "-s " + target + " shell am start -n " + Package + "/" + Activity, 10000);
            if (s.code != 0 || (s.output ?? "").Contains("Error")) return new LaunchResult { Ok = true, Message = "Installed the app on the headset, but it didn't start: " + FirstLine(s.output) };
            return new LaunchResult { Ok = true, Message = "Installed the latest app on the headset and started it." };
        }

        /// <summary>The line that explains an adb install failure (adb prints the reason after "Failure" or "Error").</summary>
        static string FailureLine(string o)
        {
            var l = (o ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Contains("Failure") || x.Contains("Error") || x.Contains("error"));
            return l ?? FirstLine(o);
        }

        bool IsRunning(string adb, string target)
        {
            var r = Run(adb, "-s " + target + " shell pidof " + Package, 5000);
            return r.code == 0 && (r.output ?? "").Trim().Length > 0 && (r.output ?? "").Trim().All(ch => char.IsDigit(ch) || ch == ' ');
        }

        static string FirstLine(string s) => (s ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no details";

        static (int, string) RunProcess(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return (-1, "timed out"); }
                    p.WaitForExit();
                    return (p.ExitCode, (o.Result ?? "") + (e.Result ?? ""));
                }
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }
    }
}
