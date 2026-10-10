using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Tells the headset where Windows has the mouse cursor, for the PC-cursor overlay. Runs only while the headset has asked for it ('C' 1); with it off nothing here exists.
/// Three small threads, none of which touches the video path:
///   poll   reads the real cursor (GetCursorInfo) every ~4 ms and, for each open stream, builds that stream's cursor message when it changes;
///   send   writes the NEWEST message per stream to the connection (a message that is overtaken before it is written is dropped, so the headset always gets the latest position
///          and a long video frame in front of it can never make the poll fall behind);
///   cover  checks every ~25 ms whether another window covers the cursor's spot (WindowFromPoint can wait on a busy window, so it must not run in the poll loop).
/// It only READS the cursor; it never moves it and never sends input, so a physical mouse works exactly as before.
/// The position is mapped with the exact inverse of <see cref="InputInjector"/>'s mapping (the window's extended frame bounds), so what the headset draws is where a click from the headset would land.
///   message 'XRSC' u32, stream u16, flags u8 (1 = the cursor is over this window and nothing covers that spot, 2 = Windows is showing a cursor), shape u8, u f32, v f32, screenX i32, screenY i32  (24 bytes)
/// </summary>
sealed class CursorReporter : IDisposable
{
    public const uint Magic = 0x43535258;       // 'XRSC'
    public const byte FlagInside = 1, FlagShowing = 2;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct THROTTLING { public uint version, controlMask, stateMask; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr name);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref THROTTLING info, int size);
    // A plain Thread.Sleep(4) can take 15-30 ms for a background console program on Windows 11. A high-resolution waitable timer keeps the 4 ms rhythm.
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    readonly Func<List<(ushort id, long hwnd)>> m_Streams;
    readonly Action<byte[]> m_Send;
    readonly Thread m_Poll, m_Writer, m_Cover;
    volatile bool m_Stop;
    readonly Dictionary<ushort, byte[]> m_Last = new Dictionary<ushort, byte[]>();          // what was last queued per stream
    readonly Dictionary<ushort, byte[]> m_Pending = new Dictionary<ushort, byte[]>();       // newest unsent message per stream
    readonly object m_PendingLock = new object();
    readonly AutoResetEvent m_Wake = new AutoResetEvent(false);
    readonly Dictionary<ushort, bool> m_Belongs = new Dictionary<ushort, bool>();           // does the cursor's spot belong to the stream's window (set by the cover thread)
    readonly object m_BelongsLock = new object();
    readonly (IntPtr handle, byte shape)[] m_Shapes;
    volatile int m_X, m_Y;
    long m_Sent, m_Dropped, m_Loops, m_LoopTicks, m_WorstTicks;

    public CursorReporter(Func<List<(ushort id, long hwnd)>> streams, Action<byte[]> send)
    {
        m_Streams = streams; m_Send = send;
        // Windows' standard cursors share one handle each, so the current cursor can be recognised by comparing handles. A game's own cursor matches none of them and goes out as "other".
        (int id, byte shape)[] std = { (32512, 0), (32513, 1), (32649, 2), (32514, 3), (32650, 3), (32515, 4), (32646, 5), (32644, 6), (32645, 7), (32642, 8), (32643, 9), (32648, 10) };
        m_Shapes = new (IntPtr, byte)[std.Length];
        for (int i = 0; i < std.Length; i++) m_Shapes[i] = (LoadCursor(IntPtr.Zero, new IntPtr(std[i].id)), std[i].shape);
        m_Poll = new Thread(PollLoop) { IsBackground = true, Name = "XrssCursor" };
        m_Writer = new Thread(SendLoop) { IsBackground = true, Name = "XrssCursorSend" };
        m_Cover = new Thread(CoverLoop) { IsBackground = true, Name = "XrssCursorCover" };
        m_Poll.Start(); m_Writer.Start(); m_Cover.Start();
        Console.WriteLine("cursor: reporting the PC cursor to the headset");
    }

    byte ShapeOf(IntPtr cursor)
    {
        foreach (var s in m_Shapes) if (s.handle == cursor && s.handle != IntPtr.Zero) return s.shape;
        return 255;
    }

    /// <summary>These threads wait on short timers; Windows 11 stretches such waits for background programs unless power throttling is switched off for the thread.</summary>
    static void NoThrottle()
    {
        try { var t = new THROTTLING { version = 1, controlMask = 1 /* EXECUTION_SPEED */, stateMask = 0 }; SetThreadInformation(GetCurrentThread(), 3 /* ThreadPowerThrottling */, ref t, Marshal.SizeOf<THROTTLING>()); } catch { }
    }

    static IntPtr Root(IntPtr h) { var r = GetAncestor(h, 3 /* GA_ROOTOWNER */); return r != IntPtr.Zero ? r : h; }
    static bool IsDesktop(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetClassName(Root(h), sb, 64); var c = sb.ToString(); return c == "Progman" || c == "WorkerW"; }

    /// <summary>Same rule as the input injector: the spot is this window's, or the desktop shows through it while it is in front (Chromium windows are skipped by hit testing).</summary>
    static bool SpotBelongsTo(IntPtr target, POINT p)
    {
        var top = WindowFromPoint(p);
        if (top == IntPtr.Zero) return false;
        if (Root(top) == Root(target)) return true;
        var fg = GetForegroundWindow();
        return IsDesktop(top) && fg != IntPtr.Zero && Root(fg) == Root(target);
    }

    static void Tick(IntPtr timer, int ms)
    {
        if (timer != IntPtr.Zero) { long due = -10000L * ms; SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(timer, (uint)(ms + 20)); }
        else Thread.Sleep(Math.Max(1, ms / 2));
    }

    static IntPtr NewTimer() => CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);

    void PollLoop()
    {
        NoThrottle(); timeBeginPeriod(1);
        IntPtr timer = NewTimer();
        try
        {
            while (!m_Stop)
            {
                long t0 = Stopwatch.GetTimestamp();
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(ref ci))
                {
                    m_X = ci.pt.x; m_Y = ci.pt.y;
                    byte showing = (ci.flags & 1) != 0 ? FlagShowing : (byte)0, shape = ShapeOf(ci.hCursor);
                    foreach (var (id, hwnd) in m_Streams())
                    {
                        var h = new IntPtr(hwnd);
                        byte flags = showing; float u = 0f, v = 0f;
                        if (IsWindow(h) && !IsIconic(h) && DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0)
                        {
                            int w = r.right - r.left, hh = r.bottom - r.top;
                            if (w > 0 && hh > 0 && ci.pt.x >= r.left && ci.pt.y >= r.top && ci.pt.x < r.right && ci.pt.y < r.bottom && Belongs(id))
                            {
                                flags |= FlagInside; u = (ci.pt.x - r.left) / (float)w; v = (ci.pt.y - r.top) / (float)hh;
                            }
                        }
                        var m = new byte[24];
                        BitConverter.GetBytes(Magic).CopyTo(m, 0); BitConverter.GetBytes(id).CopyTo(m, 4); m[6] = flags; m[7] = shape;
                        BitConverter.GetBytes(u).CopyTo(m, 8); BitConverter.GetBytes(v).CopyTo(m, 12); BitConverter.GetBytes(ci.pt.x).CopyTo(m, 16); BitConverter.GetBytes(ci.pt.y).CopyTo(m, 20);
                        lock (m_Last)
                        {
                            // a cursor that is not over the window reports nothing new while it moves elsewhere: only the first "outside" message matters
                            if (m_Last.TryGetValue(id, out var last) && Same(last, m, (flags & FlagInside) == 0)) continue;
                            m_Last[id] = m;
                        }
                        lock (m_PendingLock) { if (m_Pending.ContainsKey(id)) m_Dropped++; m_Pending[id] = m; }
                        m_Wake.Set();
                    }
                }
                long spent = Stopwatch.GetTimestamp() - t0; m_Loops++; m_LoopTicks += spent; if (spent > m_WorstTicks) m_WorstTicks = spent;
                Tick(timer, 4);
            }
        }
        catch (Exception e) { Console.WriteLine("cursor: poll stopped (" + e.Message + ")"); }
        finally { timeEndPeriod(1); if (timer != IntPtr.Zero) CloseHandle(timer); }
    }

    void SendLoop()
    {
        NoThrottle();
        try
        {
            while (!m_Stop)
            {
                m_Wake.WaitOne(100);
                List<byte[]> batch = null;
                lock (m_PendingLock)
                {
                    if (m_Pending.Count > 0) { batch = new List<byte[]>(m_Pending.Values); m_Pending.Clear(); }
                }
                if (batch == null) continue;
                foreach (var m in batch) { m_Send(m); m_Sent++; }                // may wait behind a video frame; newer positions replace queued ones meanwhile
            }
        }
        catch (Exception e) { Console.WriteLine("cursor: send stopped (" + e.Message + ")"); }
    }

    void CoverLoop()
    {
        NoThrottle();
        IntPtr timer = NewTimer();
        try
        {
            while (!m_Stop)
            {
                var p = new POINT { x = m_X, y = m_Y };
                foreach (var (id, hwnd) in m_Streams())
                {
                    bool belongs = SpotBelongsTo(new IntPtr(hwnd), p);                // can wait on a busy window: that only delays this thread
                    lock (m_BelongsLock) m_Belongs[id] = belongs;
                }
                Tick(timer, 25);
            }
        }
        catch (Exception e) { Console.WriteLine("cursor: cover check stopped (" + e.Message + ")"); }
        finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
    }

    /// <summary>Whether nothing else covers the cursor's spot over this stream's window, as of the last cover check (true until the first check).</summary>
    bool Belongs(ushort id) { lock (m_BelongsLock) return !m_Belongs.TryGetValue(id, out var b) || b; }

    static bool Same(byte[] a, byte[] b, bool outside)
    {
        if (a[6] != b[6] || a[7] != b[7]) return false;
        if (outside) return true;                                   // outside the window the position is meaningless
        for (int i = 8; i < 16; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Forget what was sent for a stream (it was closed or reopened) so the next state goes out again.</summary>
    public void Forget(ushort id)
    {
        lock (m_Last) m_Last.Remove(id);
        lock (m_PendingLock) m_Pending.Remove(id);
        lock (m_BelongsLock) m_Belongs.Remove(id);
    }

    public void Dispose()
    {
        m_Stop = true; m_Wake.Set();
        try { m_Poll.Join(300); m_Cover.Join(300); m_Writer.Join(300); } catch { }
        Console.WriteLine($"cursor: stopped ({m_Sent} messages sent, {m_Dropped} replaced by newer ones, {m_Loops} polls, {(m_Loops > 0 ? m_LoopTicks * 1000.0 / Stopwatch.Frequency / m_Loops : 0):0.00} ms each, worst {m_WorstTicks * 1000.0 / Stopwatch.Frequency:0.0} ms)");
    }
}
