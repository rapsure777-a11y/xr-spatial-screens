using System;
using System.Runtime.InteropServices;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// Sends the laser pointer to the captured application as a mouse. The chain is: ray hit on a panel -> panel UV -> crop-aware source position (0..1, origin top-left) ->
    /// the captured window's on-screen rectangle -> a desktop pixel -> absolute SendInput (trigger = left button, secondary button = right click, stick = wheel).
    /// There is one system cursor, so there is exactly one active destination: the panel under the laser, locked to the panel where a press began until the button is
    /// released. A press never goes to a window that is not in front: the window is activated first, and if Windows refuses, or another window covers the spot, the
    /// press is dropped with a message. Held buttons are released when tracking or the window is lost.
    /// Absolute pointing only: games that capture the mouse for camera look will not respond to it.
    /// </summary>
    public sealed class InputForwarder : MonoBehaviour
    {
        public SurfaceTool Tool;
        public bool Enabled = true;
        public string LastAction { get; private set; } = "";
        /// <summary>After a press the position stays put until the laser moves this many pixels, so a shaky hand still makes clean clicks and double clicks.</summary>
        public int ClickDeadZonePx = 6;
        /// <summary>A second press this soon and this close to the previous one lands on exactly the same pixel, so Windows sees a double click.</summary>
        public float DoubleClickSeconds = 0.5f;
        public int DoubleClickSnapPx = 14;
        /// <summary>How long to wait for Windows to bring the target window to the front before dropping the press.</summary>
        public float ActivationTimeout = 0.5f;

        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        const uint INPUT_MOUSE = 0, MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, WHEEL = 0x800, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
        const uint GA_ROOTOWNER = 3;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
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
        static bool TryFrameBounds(IntPtr hwnd, out RECT r) => DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf<RECT>()) == 0;
#else
        static bool TryFrameBounds(IntPtr hwnd, out RECT r) { r = default; return false; }
        static uint SendInput(uint n, INPUT[] inputs, int size) => 0;
        static int GetSystemMetrics(int index) => 0;
        static bool SetForegroundWindow(IntPtr hwnd) => false;
        static bool IsIconic(IntPtr hwnd) => false;
        static bool IsWindow(IntPtr hwnd) => false;
        static bool ShowWindow(IntPtr hwnd, int cmd) => false;
        static IntPtr GetForegroundWindow() => IntPtr.Zero;
        static IntPtr WindowFromPoint(POINT p) => IntPtr.Zero;
        static IntPtr GetAncestor(IntPtr hwnd, uint flags) => IntPtr.Zero;
        static uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid) { pid = 0; return 0; }
        static uint GetCurrentThreadId() => 0;
        static bool AttachThreadInput(uint a, uint b, bool c) => false;
        static bool BringWindowToTop(IntPtr hwnd) => false;
#endif

        // ------------------------------------------------------------------ pure mapping (unit tested)

        /// <summary>Desktop pixel for a position in the source image (0..1, origin top-left) given the captured window rectangle. Pure: see the tests.</summary>
        public static Vector2Int SourceToDesktop(Vector2 src, int winX, int winY, int winW, int winH)
            => new Vector2Int(winX + Mathf.Clamp(Mathf.RoundToInt(src.x * winW), 0, Mathf.Max(0, winW - 1)), winY + Mathf.Clamp(Mathf.RoundToInt(src.y * winH), 0, Mathf.Max(0, winH - 1)));

        /// <summary>Normalised absolute coordinates (0..65535 across the virtual desktop) for SendInput. The virtual desktop origin may be negative (a monitor left of or above the primary).</summary>
        public static Vector2Int DesktopToAbsolute(Vector2Int px, int vx, int vy, int vw, int vh)
            => new Vector2Int(Mathf.RoundToInt((px.x - vx) * 65535f / Mathf.Max(1, vw - 1)), Mathf.RoundToInt((px.y - vy) * 65535f / Mathf.Max(1, vh - 1)));

        /// <summary>The pixel to send for a laser position while a press is in progress: the press point until the laser has moved further than the dead zone, then the laser position.</summary>
        public static Vector2Int ApplyDeadZone(Vector2Int press, Vector2Int now, int deadZone, ref bool dragging)
        {
            if (!dragging && (now - press).sqrMagnitude <= deadZone * deadZone) return press;
            dragging = true;
            return now;
        }

        /// <summary>The captured window's rectangle in desktop pixels as seen by this process: the visible frame the capture shows, read here so it is in the same pixel space as the injected input (whatever the
        /// helper's DPI awareness) and follows a moved or resized window. Falls back to the rectangle the helper reported (monitors have no window handle).</summary>
        public static RectInt WindowRect(WindowGeometry win)
        {
            if (win.hwnd != 0 && TryFrameBounds(new IntPtr(win.hwnd), out var fb) && fb.right > fb.left && fb.bottom > fb.top)
                return new RectInt(fb.left, fb.top, fb.right - fb.left, fb.bottom - fb.top);
            return new RectInt(win.x, win.y, win.width, win.height);
        }

        // ------------------------------------------------------------------ state

        bool m_Left, m_Dragging, m_Refused;
        int m_Pending;                       // 0 none, 1 left press waiting for the window to come to the front, 2 right click waiting
        bool m_UpWhileWaiting;
        float m_WaitStart, m_NextMove, m_NextWheel, m_LastPressTime = -10f;
        Vector2Int m_PressPx, m_LastPx, m_LastPressPx;
        Vector2 m_LastUv;
        PanelView m_Target;
        bool m_HaveLast;

        /// <summary>Number of presses that were dropped on purpose (window would not come to the front, covered by another window, window gone).</summary>
        public static int RefusedPresses { get; private set; }
        public static int FocusAttempts { get; private set; }
        public static int FailedSends { get; private set; }
        public static int LastError { get; private set; }
        public string LastRefusal { get; private set; } = "";

        void Update()
        {
            if (!Enabled || !Tool || !Tool.InteractMode) { Release("interact off"); return; }
            var s = Tool.InteractState;
            bool holding = m_Left || m_Pending != 0;
            PanelView panel = holding ? m_Target : Tool.InteractPanel;

            if (holding && (!Tool.InteractTracking || !panel)) { Release("tracking lost"); return; }
            if (!holding && (!Tool.InteractLive || !panel)) { m_Refused &= s.triggerHeld; m_HaveLast = false; return; }
            if (panel.Source == null || panel.Source.Def.kind == "pattern") { Release("no window"); return; }   // the test pattern is not a real window

            var win = panel.Source.Window;
            bool gone = !win.IsValid || !win.alive || (win.hwnd != 0 && !IsWindow(new IntPtr(win.hwnd)));
            if (gone)
            {
                if (s.triggerDown) Refuse(panel, "The captured window is gone, so there is nothing to click.");
                Release("window gone");
                return;
            }

            // Where on the source is the laser? While a button is held the laser may wander off the panel: keep the destination and clamp to its edge.
            Vector2 uv;
            if (holding)
            {
                uv = QuadMath.RayToUv(s.ray, panel.Def.corners, out var hit, out _, false) ? new Vector2(Mathf.Clamp01(hit.x), Mathf.Clamp01(hit.y)) : m_LastUv;
            }
            else uv = Tool.InteractUv;
            m_LastUv = uv;

            var rect = WindowRect(win);
            var px = SourceToDesktop(panel.UvToSource(uv), rect.x, rect.y, rect.width, rect.height);
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            if (vw <= 0 || vh <= 0) return;

            if (m_Left) px = ApplyDeadZone(m_PressPx, px, ClickDeadZonePx, ref m_Dragging);
            m_LastPx = px; m_HaveLast = true;
            var abs = DesktopToAbsolute(px, vx, vy, vw, vh);
            if (Time.unscaledTime >= m_NextMove) { Send(MOVE | ABSOLUTE | VIRTUALDESK, abs.x, abs.y, 0); m_NextMove = Time.unscaledTime + 0.008f; }

            IntPtr target = win.hwnd != 0 ? new IntPtr(win.hwnd) : IntPtr.Zero;

            // ---- press: left button
            if (s.triggerDown && !m_Left && m_Pending == 0 && !m_Refused) StartPress(1, panel, target, px, abs);
            // ---- press: right button (a click)
            if (s.secondaryDown && !m_Left && m_Pending == 0 && !m_Refused) StartPress(2, panel, target, px, abs);

            if (m_Pending != 0)
            {
                if (s.triggerUp || !s.triggerHeld) m_UpWhileWaiting = true;
                if (target == IntPtr.Zero || IsInFront(target)) FinishPress(target, px);
                else if (Time.unscaledTime - m_WaitStart > ActivationTimeout)
                {
                    Refuse(panel, "Windows would not bring that window to the front. Click it once with the mouse, then try again.");
                    m_Pending = 0; m_Target = null;
                }
            }
            else if (m_Left && (s.triggerUp || !s.triggerHeld))
            {
                var a = DesktopToAbsolute(px, vx, vy, vw, vh);
                Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTUP, a.x, a.y, 0); m_Left = false; m_Target = null; LastAction = "left up";
            }
            if (!s.triggerHeld && !s.triggerDown) m_Refused = false;

            if (Mathf.Abs(s.stick.y) > 0.5f && Time.unscaledTime > m_NextWheel && m_Pending == 0)
            { Send(WHEEL, 0, 0, (uint)(int)(Mathf.Sign(s.stick.y) * 120)); m_NextWheel = Time.unscaledTime + 0.08f; }
        }

        void StartPress(int button, PanelView panel, IntPtr target, Vector2Int px, Vector2Int abs)
        {
            m_Target = panel; m_Pending = button; m_UpWhileWaiting = button == 2; m_WaitStart = Time.unscaledTime;
            if (target != IntPtr.Zero && !IsInFront(target)) { Activate(target); FocusAttempts++; LastAction = "focusing"; }
        }

        /// <summary>The window is in front: check nothing else covers the spot, then press.</summary>
        void FinishPress(IntPtr target, Vector2Int px)
        {
            int button = m_Pending;
            m_Pending = 0;
            var panel = m_Target;
            if (target != IntPtr.Zero && !SpotBelongsTo(target, px))
            {
                Refuse(panel, "Another window is covering that spot, so the click was not sent.");
                m_Target = null;
                return;
            }
            // A quick second press close to the last one lands on the same pixel so Windows counts a double click.
            if (Time.unscaledTime - m_LastPressTime < DoubleClickSeconds && (px - m_LastPressPx).sqrMagnitude <= DoubleClickSnapPx * DoubleClickSnapPx) px = m_LastPressPx;
            m_LastPressTime = Time.unscaledTime; m_LastPressPx = px;
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            var a = DesktopToAbsolute(px, vx, vy, vw, vh);
            const uint pos = MOVE | ABSOLUTE | VIRTUALDESK;
            Debug.Log($"[XrSpatial] input: press {button} on '{panel.Def.label ?? panel.Def.id}' at desktop pixel {px.x},{px.y} (uv {m_LastUv.x:0.000},{m_LastUv.y:0.000})");
            if (button == 1)
            {
                Send(pos | LEFTDOWN, a.x, a.y, 0); m_Left = true; m_Dragging = false; m_PressPx = px; LastAction = "left down";
                if (m_UpWhileWaiting) { Send(pos | LEFTUP, a.x, a.y, 0); m_Left = false; m_Target = null; LastAction = "left up"; }
            }
            else
            {
                Send(pos | RIGHTDOWN, a.x, a.y, 0); Send(pos | RIGHTUP, a.x, a.y, 0); m_Target = null; LastAction = "right click";
            }
            m_UpWhileWaiting = false;
        }

        void Refuse(PanelView panel, string why)
        {
            RefusedPresses++; m_Refused = true; LastRefusal = why; LastAction = "refused";
            if (Tool) Tool.Say(why, 5f);
        }

        // ------------------------------------------------------------------ windows

        /// <summary>The top-level window (or its owner) that a window belongs to, so a menu, tooltip or dialog owned by the target counts as the target.</summary>
        static IntPtr Root(IntPtr h) { var r = GetAncestor(h, GA_ROOTOWNER); return r != IntPtr.Zero ? r : h; }

        static bool IsInFront(IntPtr target)
        {
            var fg = GetForegroundWindow();
            return fg != IntPtr.Zero && Root(fg) == Root(target);
        }

        static bool SpotBelongsTo(IntPtr target, Vector2Int px)
        {
            var top = WindowFromPoint(new POINT { x = px.x, y = px.y });
            return top != IntPtr.Zero && Root(top) == Root(target);
        }

        /// <summary>Brings a window to the front even though this process is not the foreground one (attach to the foreground thread's input queue for the call). Windows may still refuse: the caller checks.</summary>
        static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9);                       // SW_RESTORE
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(h);
            ShowWindow(h, 5);                                        // SW_SHOW
            SetForegroundWindow(h);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        // ------------------------------------------------------------------ release / send

        /// <summary>Lets go of every held button (tracking lost, interaction switched off, window gone) so nothing stays pressed in the target.</summary>
        void Release(string why)
        {
            bool had = m_Left || m_Pending != 0;
            m_Pending = 0; m_UpWhileWaiting = false;
            if (m_Left)
            {
                int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
                if (vw > 0 && vh > 0 && m_HaveLast) { var a = DesktopToAbsolute(m_LastPx, vx, vy, vw, vh); Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTUP, a.x, a.y, 0); }
                else Send(LEFTUP, 0, 0, 0);
                m_Left = false;
            }
            m_Target = null;
            if (had) LastAction = "released (" + why + ")";
        }

        void Send(uint flags, int x, int y, uint data)
        {
            var i = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags } };
            uint sent = SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
            if (sent == 0)
            {
                LastError = Marshal.GetLastWin32Error();
                if (FailedSends++ == 0 && Tool) Tool.Say($"Windows refused the injected click (error {LastError}). Is the captured app running as administrator?", 8f);
            }
        }

        void OnDisable() => Release("disabled");
    }
}
