using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Applies pointer events from the headset to the captured window with SendInput. Ported from Assets/Scripts/Spatial/InputForwarder.cs
/// (same mapping, same safety rules): the position in the streamed image (0..1, top-left origin) maps onto the window's visible frame,
/// the window is brought to the front before a press, a press is refused when another window covers the spot, a quick second press near the
/// first snaps onto the same pixel so Windows counts a double click, a small dead zone keeps clicks from becoming drags, and everything is released when the
/// headset disconnects. Called from one thread only.
/// </summary>
sealed class InputInjector
{
    public const byte Move = 0, LeftDown = 1, LeftUp = 2, RightClick = 3, Wheel = 4, Lost = 5;
    const int DeadZonePx = 6, DoubleClickSnapPx = 14;
    const double DoubleClickSeconds = 0.5, ActivationTimeoutSeconds = 0.5;

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    const uint MOVEF = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, WHEEL = 0x800, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, GA_ROOTOWNER = 3;

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    readonly Func<IntPtr> m_Window;
    bool m_Left, m_Dragging, m_Refused;
    int m_PressX, m_PressY, m_LastX, m_LastY;
    int m_LastPressX = int.MinValue, m_LastPressY;
    long m_LastPressTicks;
    public string Last = "none";
    public int Refusals;

    public InputInjector(Func<IntPtr> window) { m_Window = window; }

    static IntPtr Root(IntPtr h) { var r = GetAncestor(h, GA_ROOTOWNER); return r != IntPtr.Zero ? r : h; }
    static bool IsInFront(IntPtr t) { var fg = GetForegroundWindow(); return fg != IntPtr.Zero && Root(fg) == Root(t); }
    static bool SpotBelongsTo(IntPtr t, int x, int y) { var top = WindowFromPoint(new POINT { x = x, y = y }); return top != IntPtr.Zero && Root(top) == Root(t); }

    static void Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h); ShowWindow(h, 5); SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    static void Send(uint flags, int x, int y, uint data)
    {
        var i = new INPUT { type = 0, mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags } };
        if (SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>()) == 0) Console.WriteLine("SendInput failed, win32 error " + Marshal.GetLastWin32Error());
    }

    static void Abs(int px, int py, out int ax, out int ay)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        ax = (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1)); ay = (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1));
    }

    bool Pixel(IntPtr hwnd, float u, float v, out int px, out int py)
    {
        px = py = 0;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) != 0) return false;
        int w = r.right - r.left, h = r.bottom - r.top;
        if (w <= 0 || h <= 0) return false;
        px = r.left + Math.Clamp((int)Math.Round(u * w), 0, Math.Max(0, w - 1));
        py = r.top + Math.Clamp((int)Math.Round(v * h), 0, Math.Max(0, h - 1));
        return true;
    }

    public void Handle(byte kind, float u, float v, float wheel)
    {
        var hwnd = m_Window();
        if (kind == Lost) { Release("pointer lost"); return; }
        if (!Pixel(hwnd, u, v, out int px, out int py)) { if (kind == LeftDown || kind == RightClick) { Refusals++; Last = "refused: window gone or minimised"; } Release("window gone"); return; }
        Abs(px, py, out int ax, out int ay);
        const uint pos = MOVEF | ABSOLUTE | VIRTUALDESK;
        switch (kind)
        {
            case Move:
                if (m_Left)
                {
                    if (!m_Dragging && (px - m_PressX) * (px - m_PressX) + (py - m_PressY) * (py - m_PressY) <= DeadZonePx * DeadZonePx) { px = m_PressX; py = m_PressY; Abs(px, py, out ax, out ay); }
                    else m_Dragging = true;
                }
                Send(pos, ax, ay, 0); m_LastX = px; m_LastY = py; break;
            case LeftDown:
            case RightClick:
                if (m_Left) break;
                if (!BringToFront(hwnd)) { Refusals++; Last = "refused: Windows would not bring the window to the front"; Console.WriteLine(Last); return; }
                if (!SpotBelongsTo(hwnd, px, py)) { Refusals++; Last = "refused: another window covers that spot"; Console.WriteLine(Last); return; }
                if (kind == LeftDown)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (m_LastPressX != int.MinValue && (now - m_LastPressTicks) < DoubleClickSeconds * Stopwatch.Frequency
                        && (px - m_LastPressX) * (px - m_LastPressX) + (py - m_LastPressY) * (py - m_LastPressY) <= DoubleClickSnapPx * DoubleClickSnapPx) { px = m_LastPressX; py = m_LastPressY; Abs(px, py, out ax, out ay); }
                    m_LastPressTicks = now; m_LastPressX = px; m_LastPressY = py;
                    Send(pos | LEFTDOWN, ax, ay, 0); m_Left = true; m_Dragging = false; m_PressX = m_LastX = px; m_PressY = m_LastY = py; Last = $"left down at {px},{py}";
                }
                else { Send(pos | RIGHTDOWN, ax, ay, 0); Send(pos | RIGHTUP, ax, ay, 0); Last = $"right click at {px},{py}"; }
                Console.WriteLine("input: " + Last); break;
            case LeftUp:
                if (m_Left) { Send(pos | LEFTUP, ax, ay, 0); m_Left = false; m_Dragging = false; Last = $"left up at {px},{py}"; Console.WriteLine("input: " + Last); }
                break;
            case Wheel:
                if (SpotBelongsTo(hwnd, px, py)) { Send(pos, ax, ay, 0); Send(WHEEL, 0, 0, (uint)(int)(Math.Sign(wheel) * 120)); }
                break;
        }
    }

    bool BringToFront(IntPtr hwnd)
    {
        if (IsInFront(hwnd)) return true;
        Activate(hwnd);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < ActivationTimeoutSeconds) { if (IsInFront(hwnd)) return true; Thread.Sleep(10); }
        return false;
    }

    /// <summary>Lets go of a held button (headset disconnected, tracking lost, window gone) so nothing stays pressed.</summary>
    public void Release(string why)
    {
        if (!m_Left) return;
        Abs(m_LastX, m_LastY, out int ax, out int ay);
        Send(MOVEF | ABSOLUTE | VIRTUALDESK | LEFTUP, ax, ay, 0);
        m_Left = false; m_Dragging = false; Last = "released (" + why + ")"; Console.WriteLine("input: " + Last);
    }
}
