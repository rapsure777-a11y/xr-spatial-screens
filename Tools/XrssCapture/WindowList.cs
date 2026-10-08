using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace XrssCapture
{
    public class WindowInfo
    {
        public long hwnd; public string title; public uint pid; public string process; public int x, y, w, h;
        /// <summary>"normal", "minimized" (size is what it will have when restored) or "otherDesktop" (Windows hides it from this virtual desktop; it cannot be captured from here).</summary>
        public string state = "normal";
    }

    static class WindowList
    {
        /// <summary>Visible, titled, un-cloaked top-level windows of useful size (what a person can sensibly pick), excluding this process and tool windows.</summary>
        public static List<WindowInfo> Enumerate(bool includeSmall = false)
        {
            var list = new List<WindowInfo>();
            uint self = (uint)Environment.ProcessId;
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                string title = Native.Title(hwnd);
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;
                // A window Windows hides is only interesting when it lives on another virtual desktop (system placeholders such as Settings are hidden on this one too).
                string state = "normal";
                if (Native.IsCloaked(hwnd)) { if (Native.IsOnCurrentDesktop(hwnd)) return true; state = "otherDesktop"; }
                int x, y, w, h;
                if (Native.IsIconic(hwnd))
                {
                    // a minimized window has no client area; report the size it will have when restored
                    var wp = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
                    if (!Native.GetWindowPlacement(hwnd, ref wp)) return true;
                    x = wp.rcNormalPosition.left; y = wp.rcNormalPosition.top; w = wp.rcNormalPosition.right - wp.rcNormalPosition.left; h = wp.rcNormalPosition.bottom - wp.rcNormalPosition.top;
                    if (state == "normal") state = "minimized";
                    if (!includeSmall && (w < 160 || h < 120)) return true;
                }
                else
                {
                    Native.GetClientRect(hwnd, out var c);
                    if (!includeSmall && (c.right - c.left < 120 || c.bottom - c.top < 80)) return true;
                    var p = new Native.POINT();
                    Native.ClientToScreen(hwnd, ref p);
                    x = p.x; y = p.y; w = c.right - c.left; h = c.bottom - c.top;
                }
                string proc = "";
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
                list.Add(new WindowInfo { hwnd = hwnd.ToInt64(), title = title, pid = pid, process = proc, x = x, y = y, w = w, h = h, state = state });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>Best match by (optional) process name and (optional) title substring, case-insensitive; the largest window wins ties.</summary>
        public static WindowInfo Find(string process, string titleContains)
        {
            WindowInfo best = null;
            foreach (var w in Enumerate())
            {
                if (!string.IsNullOrEmpty(process) && !w.process.Equals(process.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(titleContains) && w.title.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || (long)w.w * w.h > (long)best.w * best.h) best = w;
            }
            return best;
        }

        public static string ToJson(List<WindowInfo> list)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"hwnd\":").Append(w.hwnd).Append(",\"title\":").Append(Json(w.title)).Append(",\"pid\":").Append(w.pid)
                  .Append(",\"process\":").Append(Json(w.process)).Append(",\"x\":").Append(w.x).Append(",\"y\":").Append(w.y).Append(",\"w\":").Append(w.w).Append(",\"h\":").Append(w.h).Append(",\"state\":").Append(Json(w.state)).Append('}');
            }
            return sb.Append(']').ToString();
        }

        public static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
