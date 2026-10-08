using System;
using System.Collections.Generic;
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
    const int DeadZonePx = 40, DoubleClickSnapPx = 24;           // sized for a laser held in the air: a few millimetres of wobble at arm's length is 10 to 20 window pixels
    const double DoubleClickSeconds = 0.5, ActivationTimeoutSeconds = 0.5;

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
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
    /// <summary>Label for log lines (the window title).</summary>
    public string Name = "";
    public int Refusals;

    public InputInjector(Func<IntPtr> window) { m_Window = window; }

    static IntPtr Root(IntPtr h) { var r = GetAncestor(h, GA_ROOTOWNER); return r != IntPtr.Zero ? r : h; }
    static bool IsInFront(IntPtr t) { var fg = GetForegroundWindow(); return fg != IntPtr.Zero && Root(fg) == Root(t); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    static bool IsDesktop(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetClassName(Root(h), sb, 64); var c = sb.ToString(); return c == "Progman" || c == "WorkerW"; }
    // Some windows (Chromium) are skipped by hit-testing, so the desktop shows through: that is not another window covering the spot.
    static bool SpotBelongsTo(IntPtr t, int x, int y) { var top = WindowFromPoint(new POINT { x = x, y = y }); return top != IntPtr.Zero && (Root(top) == Root(t) || (IsDesktop(top) && IsInFront(t))); }

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
                // z-order can lag a moment behind the foreground change: give the spot a short time to settle before calling it covered
                for (int i = 0; i < 15 && !SpotBelongsTo(hwnd, px, py); i++) Thread.Sleep(10);
                if (!SpotBelongsTo(hwnd, px, py))
                {
                    var under = WindowFromPoint(new POINT { x = px, y = py });
                    Refusals++; Last = $"refused: another window covers that spot (pixel {px},{py}; window under it {under} root {Root(under)}; target {hwnd} root {Root(hwnd)}; foreground {GetForegroundWindow()})";
                    Console.WriteLine(Last); return;
                }
                if (kind == LeftDown)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (m_LastPressX != int.MinValue && (now - m_LastPressTicks) < DoubleClickSeconds * Stopwatch.Frequency
                        && (px - m_LastPressX) * (px - m_LastPressX) + (py - m_LastPressY) * (py - m_LastPressY) <= DoubleClickSnapPx * DoubleClickSnapPx) { px = m_LastPressX; py = m_LastPressY; Abs(px, py, out ax, out ay); }
                    m_LastPressTicks = now; m_LastPressX = px; m_LastPressY = py;
                    Send(pos | LEFTDOWN, ax, ay, 0); m_Left = true; m_Dragging = false; m_PressX = m_LastX = px; m_PressY = m_LastY = py; Last = $"left down at {px},{py}";
                }
                else { Send(pos | RIGHTDOWN, ax, ay, 0); Send(pos | RIGHTUP, ax, ay, 0); Last = $"right click at {px},{py}"; }
                Console.WriteLine($"input: {Last} [{Name}]"); break;
            case LeftUp:
                if (m_Left)
                {
                    // A release that stays inside the dead zone belongs to the press point: a click, not a tiny drag (VR laser wobble would otherwise turn clicks into drags).
                    if (!m_Dragging && (px - m_PressX) * (px - m_PressX) + (py - m_PressY) * (py - m_PressY) <= DeadZonePx * DeadZonePx) { px = m_PressX; py = m_PressY; Abs(px, py, out ax, out ay); }
                    Send(pos | LEFTUP, ax, ay, 0); m_Left = false; m_Dragging = false; Last = $"left up at {px},{py}"; Console.WriteLine($"input: {Last} [{Name}]");
                }
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

    // ------------------------------------------------------------------ keyboard

    public const byte KeyText = 0, KeyDown = 1, KeyUp = 2;
    readonly HashSet<ushort> m_KeysDown = new HashSet<ushort>();

    static bool IsExtendedKey(ushort vk) => vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || (vk >= 0x25 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0xA3 || vk == 0xA5 || vk == 0x5B || vk == 0x5C;

    static void SendKey(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT { type = 1 /* INPUT_KEYBOARD */, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };
        if (SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>()) == 0) Console.WriteLine("SendInput (keyboard) failed, win32 error " + Marshal.GetLastWin32Error());
    }

    /// <summary>A key from the headset: kind 0 types one character (Unicode, so layout and shift state do not matter), 1 presses a virtual key, 2 releases it. The window is brought to the front first; nothing is typed into any other window.</summary>
    public void Key(byte kind, uint code)
    {
        var hwnd = m_Window();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) { Last = "key refused: window gone"; return; }
        if (kind == KeyUp && m_KeysDown.Contains((ushort)code)) { ReleaseKey((ushort)code); return; }      // releasing never needs the window in front
        if (kind == KeyUp) return;
        if (!BringToFront(hwnd)) { Refusals++; Last = "key refused: Windows would not bring the window to the front"; Console.WriteLine(Last); return; }
        if (kind == KeyText)
        {
            if (code == 0 || code > 0xFFFF) return;
            SendKey(0, (ushort)code, KEYEVENTF_UNICODE); SendKey(0, (ushort)code, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            Last = "typed char " + code;
        }
        else if (kind == KeyDown)
        {
            ushort vk = (ushort)code;
            if (vk == 0 || vk > 0xFE) return;
            uint flags = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
            SendKey(vk, (ushort)MapVirtualKey(vk, 0), flags);
            m_KeysDown.Add(vk); Last = "key down " + vk;
        }
    }

    void ReleaseKey(ushort vk)
    {
        uint flags = KEYEVENTF_KEYUP | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        SendKey(vk, (ushort)MapVirtualKey(vk, 0), flags);
        m_KeysDown.Remove(vk);
    }

    /// <summary>Lets go of a held button and any held key (headset disconnected, tracking lost, window gone) so nothing stays pressed.</summary>
    public void ReleaseKeys() { foreach (var vk in new List<ushort>(m_KeysDown)) ReleaseKey(vk); }

    public void Release(string why)
    {
        if (!m_Left) return;
        Abs(m_LastX, m_LastY, out int ax, out int ay);
        Send(MOVEF | ABSOLUTE | VIRTUALDESK | LEFTUP, ax, ay, 0);
        m_Left = false; m_Dragging = false; Last = "released (" + why + ")"; Console.WriteLine("input: " + Last);
    }
}
