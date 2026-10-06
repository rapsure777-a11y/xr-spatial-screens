using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace XrssCapture
{
    public class WindowInfo
    {
        public long hwnd; public string title; public uint pid; public string process; public int x, y, w, h;
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
                if (!Native.IsWindowVisible(hwnd) || Native.IsCloaked(hwnd)) return true;
                if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                string title = Native.Title(hwnd);
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;
                Native.GetClientRect(hwnd, out var c);
                if (!includeSmall && (c.right - c.left < 120 || c.bottom - c.top < 80)) return true;
                var p = new Native.POINT();
                Native.ClientToScreen(hwnd, ref p);
                string proc = "";
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
                list.Add(new WindowInfo { hwnd = hwnd.ToInt64(), title = title, pid = pid, process = proc, x = p.x, y = p.y, w = c.right - c.left, h = c.bottom - c.top });
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
                  .Append(",\"process\":").Append(Json(w.process)).Append(",\"x\":").Append(w.x).Append(",\"y\":").Append(w.y).Append(",\"w\":").Append(w.w).Append(",\"h\":").Append(w.h).Append('}');
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
