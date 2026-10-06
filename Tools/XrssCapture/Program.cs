using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace XrssCapture
{
    /// <summary>
    /// XrssCapture: Windows.Graphics.Capture helper for XR Spatial Screens.
    ///   XrssCapture --list                       print the capturable windows as JSON and exit
    ///   XrssCapture --run --id ID (--hwnd N | --process NAME [--title TEXT] | --title TEXT | --monitor 0)
    ///               [--fps 60] [--max 3840x2160] [--cursor] [--parent PID]
    ///                                            capture into the shared memory map "XrssFrame-ID" until the window closes, the parent exits, or stdin closes
    ///   XrssCapture --snap (--hwnd N | --process NAME | --title TEXT) --out file.png [--delay MS]
    ///                                            capture one frame to a PNG (for testing the backend without Unity)
    /// Status is printed to stdout as one JSON object per line: {"event":"started"} / {"event":"frame","w":..,"h":..} / {"event":"error","msg":".."} / {"event":"closed"}.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            var a = Args.Parse(args);
            try
            {
                if (a.Has("list")) { Console.WriteLine(WindowList.ToJson(WindowList.Enumerate(a.Has("all")))); return 0; }
                if (a.Has("snap")) return Snap.Run(a);
                if (a.Has("run")) return Run(a);
                Console.Error.WriteLine("usage: XrssCapture --list | --run --id ID (--hwnd N | --process NAME | --title TEXT | --monitor N) | --snap ... --out file.png");
                return 2;
            }
            catch (Exception e)
            {
                Status("error", e.GetType().Name + ": " + e.Message);
                return 1;
            }
        }

        public static void Status(string ev, string msg = null, int w = 0, int h = 0)
        {
            string line = "{\"event\":\"" + ev + "\"" + (msg != null ? ",\"msg\":" + WindowList.Json(msg) : "") + (w > 0 ? ",\"w\":" + w + ",\"h\":" + h : "") + "}";
            lock (Console.Out) { Console.WriteLine(line); Console.Out.Flush(); }
        }

        public static IntPtr ResolveWindow(Args a)
        {
            if (a.Has("hwnd")) return new IntPtr(long.Parse(a.Get("hwnd")));
            var w = WindowList.Find(a.Get("process"), a.Get("title"));
            if (w == null) throw new InvalidOperationException("no matching window (process=" + a.Get("process") + ", title=" + a.Get("title") + ")");
            return new IntPtr(w.hwnd);
        }

        static int Run(Args a)
        {
            string id = a.Get("id") ?? "default";
            int fps = a.Has("fps") ? int.Parse(a.Get("fps")) : 60;
            int maxW = 3840, maxH = 2160;
            if (a.Has("max")) { var p = a.Get("max").Split('x'); maxW = int.Parse(p[0]); maxH = int.Parse(p[1]); }
            bool monitor = a.Has("monitor");
            IntPtr hwnd = monitor ? IntPtr.Zero : ResolveWindow(a);
            IntPtr hmon = monitor ? MonitorHandle(int.Parse(a.Get("monitor"))) : IntPtr.Zero;
            Process parent = null;
            if (a.Has("parent")) { try { parent = Process.GetProcessById(int.Parse(a.Get("parent"))); } catch { return 0; } }

            using var session = new CaptureSession(hwnd, monitor, id, maxW, maxH, fps, a.Has("cursor"), hmon);
            session.Error += m => Status("error", m);
            var stop = new ManualResetEventSlim(false);
            // stdin closing means the parent is gone (or asked us to stop).
            new Thread(() => { try { while (Console.In.ReadLine() != null) { } } catch { } stop.Set(); }) { IsBackground = true }.Start();
            Status("started");
            bool announced = false;
            while (!stop.Wait(100))
            {
                if (session.Closed) { Status("closed"); break; }
                if (parent != null && parent.HasExited) break;
                if (!announced && session.Width > 0) { Status("frame", null, session.Width, session.Height); announced = true; }
            }
            return 0;
        }

        static IntPtr MonitorHandle(int index)
        {
            var list = new List<IntPtr>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, d, r, data) => { list.Add(m); return true; }, IntPtr.Zero);
            if (index < 0 || index >= list.Count) throw new InvalidOperationException("no monitor " + index);
            return list[index];
        }
    }

    sealed class Args
    {
        readonly Dictionary<string, string> m_Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static Args Parse(string[] args)
        {
            var r = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string k = args[i].Substring(2);
                string v = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
                r.m_Map[k] = v;
            }
            return r;
        }
        public bool Has(string k) => m_Map.ContainsKey(k);
        public string Get(string k) => m_Map.TryGetValue(k, out var v) ? v : null;
    }
}
