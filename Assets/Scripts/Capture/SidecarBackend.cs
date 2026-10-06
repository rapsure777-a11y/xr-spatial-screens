using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Capture
{
    /// <summary>What to capture: a window found by process/title (or a handle), or a monitor.</summary>
    [Serializable]
    public class CaptureRequest
    {
        public string processName;
        public string titleContains;
        public long hwnd;
        public int monitorIndex = -1;
        public int fps = 60;
        public bool captureCursor;
        public int maxWidth = 3840, maxHeight = 2160;
    }

    /// <summary>
    /// Window/monitor capture through the XrssCapture helper process (Windows.Graphics.Capture). The helper writes frames into a memory-mapped file; this class
    /// maps it and uploads the newest frame to a texture. The helper is started hidden, exits when this process dies (stdin closes / parent pid), and is
    /// restarted by the owner if the source ends.
    /// </summary>
    public sealed unsafe class SidecarBackend : ICaptureBackend
    {
        readonly string m_Id = Guid.NewGuid().ToString("N").Substring(0, 12);
        readonly CaptureRequest m_Request;
        Process m_Proc;
        byte* m_Base;
        uint m_LastCounter;
        readonly ConcurrentQueue<string> m_Lines = new ConcurrentQueue<string>();
        string m_Status = "starting capture helper";
        bool m_Ended;
        float m_StartTime, m_LastMapAttempt;
        int m_Width, m_Height;
        int m_MaxW, m_MaxH;
        string m_LastError;
        int m_MapAttempts, m_Frames;

        public SidecarBackend(CaptureRequest request)
        {
            m_Request = request;
            m_MaxW = request.maxWidth; m_MaxH = request.maxHeight;
            m_StartTime = Time.realtimeSinceStartup;
            Start();
        }

        public string Status => m_Status;
        public bool IsRunning => m_Proc != null && !m_Proc.HasExited && !m_Ended;
        public bool HasEnded => m_Ended;
        public int Width => m_Width;
        public int Height => m_Height;
        public float CaptureFps { get; private set; }
        public WindowGeometry Window { get; private set; }
        public string LastError => m_LastError;

        // ------------------------------------------------------------------ helper discovery

        /// <summary>Finds XrssCapture.exe: env XRSS_CAPTURE_EXE, next to the player, then the development build output in the repository.</summary>
        public static string FindHelper()
        {
            string env = Environment.GetEnvironmentVariable("XRSS_CAPTURE_EXE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            string baseDir = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string[] candidates =
            {
                Path.Combine(baseDir, "XrssCapture", "XrssCapture.exe"),
                Path.Combine(baseDir, "Tools", "XrssCapture", "publish", "XrssCapture.exe"),
                Path.Combine(baseDir, "Tools", "XrssCapture", "bin", "Release", "net8.0-windows10.0.22621.0", "win-x64", "XrssCapture.exe"),
                Path.Combine(baseDir, "Tools", "XrssCapture", "bin", "Debug", "net8.0-windows10.0.22621.0", "win-x64", "XrssCapture.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        void Start()
        {
            string exe = FindHelper();
            if (exe == null) { m_Status = "capture helper (XrssCapture.exe) not found"; m_Ended = true; return; }
            var sb = new StringBuilder("--run --id " + m_Id + " --fps " + Mathf.Clamp(m_Request.fps, 1, 240) + " --max " + m_MaxW + "x" + m_MaxH + " --parent " + Process.GetCurrentProcess().Id);
            if (m_Request.captureCursor) sb.Append(" --cursor");
            if (m_Request.monitorIndex >= 0) sb.Append(" --monitor " + m_Request.monitorIndex);
            else if (m_Request.hwnd != 0) sb.Append(" --hwnd " + m_Request.hwnd);
            else
            {
                if (!string.IsNullOrEmpty(m_Request.processName)) sb.Append(" --process \"" + m_Request.processName.Replace("\"", "") + "\"");
                if (!string.IsNullOrEmpty(m_Request.titleContains)) sb.Append(" --title \"" + m_Request.titleContains.Replace("\"", "") + "\"");
            }
            try
            {
                var psi = new ProcessStartInfo(exe, sb.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(exe),
                };
                m_Proc = Process.Start(psi);
                var p = m_Proc;
                new Thread(() => { try { string l; while ((l = p.StandardOutput.ReadLine()) != null) m_Lines.Enqueue(l); } catch { } }) { IsBackground = true }.Start();
                new Thread(() => { try { string l; while ((l = p.StandardError.ReadLine()) != null) m_Lines.Enqueue("{\"event\":\"error\",\"msg\":\"" + l.Replace("\"", "'") + "\"}"); } catch { } }) { IsBackground = true }.Start();
                m_Status = "waiting for the first frame";
            }
            catch (Exception e) { m_Status = "could not start the capture helper: " + e.Message; m_Ended = true; }
        }

        void Map()
        {
            if (m_Base != null || Time.realtimeSinceStartup - m_LastMapAttempt < 0.1f) return;
            m_LastMapAttempt = Time.realtimeSinceStartup;
            // Direct Win32 (OpenFileMapping + MapViewOfFile): the helper creates a named section with the .NET Core API; opening it through Unity's Mono implementation of
            // MemoryMappedFile proved unreliable, so the section is mapped explicitly.
            IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, FrameProtocol.MapName(m_Id));
            if (h == IntPtr.Zero) { m_MapAttempts++; return; }
            IntPtr view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                CloseHandle(h);
                m_MapAttempts++; m_Status = "cannot map frames (error " + Marshal.GetLastWin32Error() + ")";
                return;
            }
            m_MapHandle = h; m_ViewPtr = view; m_Base = (byte*)view;
        }

        IntPtr m_MapHandle, m_ViewPtr;
        const uint FILE_MAP_READ = 4;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        void ReadLines()
        {
            while (m_Lines.TryDequeue(out var line))
            {
                if (line.Contains("\"event\":\"error\"")) { int i = line.IndexOf("\"msg\":", StringComparison.Ordinal); m_LastError = i >= 0 ? line.Substring(i + 6).Trim('}', '"', ' ') : line; m_Status = "capture error: " + m_LastError; }
                else if (line.Contains("\"event\":\"closed\"")) { m_Ended = true; m_Status = "the captured window was closed"; }
            }
        }

        void DrainStatus()
        {
            ReadLines();
            if (m_Proc != null && m_Proc.HasExited && !m_Ended)
            {
                // The helper prints its error and exits straight away: give the reader threads a moment to deliver that last line before reporting the exit.
                if (string.IsNullOrEmpty(m_LastError)) { Thread.Sleep(80); ReadLines(); }
                m_Ended = true;
                if (string.IsNullOrEmpty(m_LastError)) m_Status = "capture helper exited (code " + m_Proc.ExitCode + ")";
            }
        }

        string m_LastTrace;
        void Trace(string s) { if (s != m_LastTrace) { m_LastTrace = s; Debug.Log("[XrSpatial] sidecar: " + s); } }

        public bool PollInto(ref Texture2D target)
        {
            DrainStatus();
            Trace($"poll ended={m_Ended} base={(m_Base != null)} proc={(m_Proc != null && !m_Proc.HasExited)} status={m_Status}");
            if (m_Ended && m_Base == null) return false;
            Map();
            if (m_Base == null) { if (m_Frames == 0) m_Status = $"waiting for the helper to publish frames ({m_MapAttempts} map attempts)"; return false; }
            if (*(uint*)(m_Base + FrameProtocol.OffMagic) != FrameProtocol.Magic) { Trace("bad magic " + (*(uint*)(m_Base + FrameProtocol.OffMagic)).ToString("x")); return false; }
            uint flags = *(uint*)(m_Base + FrameProtocol.OffFlags);
            Window = new WindowGeometry
            {
                x = *(int*)(m_Base + FrameProtocol.OffClientX), y = *(int*)(m_Base + FrameProtocol.OffClientY),
                width = *(int*)(m_Base + FrameProtocol.OffClientW), height = *(int*)(m_Base + FrameProtocol.OffClientH),
                hwnd = *(uint*)(m_Base + FrameProtocol.OffHwnd), foreground = (flags & FrameProtocol.FlagForeground) != 0, minimized = (flags & FrameProtocol.FlagMinimized) != 0, alive = (flags & FrameProtocol.FlagAlive) != 0,
            };
            CaptureFps = *(uint*)(m_Base + FrameProtocol.OffCaptureFps);
            if ((flags & FrameProtocol.FlagReady) == 0) { if (m_Frames == 0) m_Status = $"mapped; helper has not published a frame yet (flags {flags})"; return false; }
            uint c1 = *(uint*)(m_Base + FrameProtocol.OffFrameCounter);
            if (c1 == m_LastCounter) return false;
            int w = (int)*(uint*)(m_Base + FrameProtocol.OffWidth), h = (int)*(uint*)(m_Base + FrameProtocol.OffHeight), stride = (int)*(uint*)(m_Base + FrameProtocol.OffStride);
            int slot = (int)*(uint*)(m_Base + FrameProtocol.OffLatestSlot);
            int cap = (int)*(uint*)(m_Base + FrameProtocol.OffSlotCapacity);
            if (w <= 0 || h <= 0 || stride != w * 4 || slot < 0 || slot > 1 || (long)h * stride > cap) return false;
            if (!target || target.width != w || target.height != h)
            {
                if (target) UnityEngine.Object.Destroy(target);
                target = new Texture2D(w, h, TextureFormat.BGRA32, false, false) { name = "CapturedFrame", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            byte* src = m_Base + FrameProtocol.HeaderSize + (long)slot * cap;
            target.LoadRawTextureData((IntPtr)src, h * stride);
            target.Apply(false);
            uint c2 = *(uint*)(m_Base + FrameProtocol.OffFrameCounter);
            m_LastCounter = c1; m_Frames++;
            m_Width = w; m_Height = h;
            m_Status = (CaptureFps >= 1f ? $"capturing {w}x{h} @ {CaptureFps:0} fps" : $"capturing {w}x{h} (static: frames arrive when the window changes)") + (c2 - c1 > 1 ? " (reader lagging)" : "") + (Window.minimized ? " - window minimised" : "");
            return true;
        }

        public void Dispose()
        {
            try { if (m_Proc != null && !m_Proc.HasExited) { try { m_Proc.StandardInput.Close(); } catch { } if (!m_Proc.WaitForExit(800)) m_Proc.Kill(); } } catch { }
            if (m_Base != null) { UnmapViewOfFile(m_ViewPtr); CloseHandle(m_MapHandle); m_Base = null; m_ViewPtr = IntPtr.Zero; m_MapHandle = IntPtr.Zero; }
            m_Proc?.Dispose(); m_Proc = null;
        }
    }
}
