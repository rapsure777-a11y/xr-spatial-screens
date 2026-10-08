using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace XrSpatial.Capture
{
    [Serializable]
    public class CapturableWindow
    {
        public long hwnd;
        public string title;
        public uint pid;
        public string process;
        public int x, y, w, h;
        public override string ToString() => $"{process}: {title} ({w}x{h})";
    }

    [Serializable]
    class WindowArray { public CapturableWindow[] items; }

    /// <summary>The windows the user can pick to capture, enumerated by the helper (so the list matches exactly what the helper can capture).</summary>
    public static class CaptureCatalog
    {
        public static Task<List<CapturableWindow>> ListAsync(bool includeSelf = false)
        {
            if (RemoteHost.Active)
            {
                // headset build: the PC host currently streams one window; offer it as the only choice
                var one = new List<CapturableWindow> { new CapturableWindow { hwnd = 0, title = "Streamed PC window", process = "PC", w = RemoteHost.FrameWidth, h = RemoteHost.FrameHeight } };
                return Task.FromResult(one);
            }
            return Task.Run(() =>
            {
                var result = new List<CapturableWindow>();
                string exe = SidecarBackend.FindHelper();
                if (exe == null) return result;
                try
                {
                    var psi = new ProcessStartInfo(exe, "--list") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
                    using var p = Process.Start(psi);
                    string json = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    uint self = (uint)Process.GetCurrentProcess().Id;
                    foreach (var w in Parse(json)) if (includeSelf || w.pid != self) result.Add(w);
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[XrSpatial] window list failed: " + e.Message); }
                return result;
            });
        }

        public static CapturableWindow[] Parse(string jsonArray)
        {
            if (string.IsNullOrWhiteSpace(jsonArray)) return new CapturableWindow[0];
            var w = JsonUtility.FromJson<WindowArray>("{\"items\":" + jsonArray.Trim() + "}");
            return w?.items ?? new CapturableWindow[0];
        }
    }
}
