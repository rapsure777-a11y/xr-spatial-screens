using System;
using System.Runtime.InteropServices;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// Sends the laser pointer to the captured application as a mouse: the ray's hit on a panel is converted to a position in the source image (crop-aware), then to
    /// a desktop pixel using the captured window's rectangle, and injected with SendInput (trigger = left button, secondary button = right button, stick = wheel).
    /// Absolute pointing only: games that capture the mouse for camera look will not respond to it. Needs the helper to report the window rectangle.
    /// </summary>
    public sealed class InputForwarder : MonoBehaviour
    {
        public SurfaceTool Tool;
        public bool Enabled = true;
        public string LastAction { get; private set; } = "";
        bool m_Left, m_Right;
        float m_NextMove;

        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
        const uint INPUT_MOUSE = 0, MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, WHEEL = 0x800, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
#else
        static uint SendInput(uint n, INPUT[] inputs, int size) => 0;
        static int GetSystemMetrics(int index) => 0;
        static bool SetForegroundWindow(IntPtr hwnd) => false;
        static bool IsIconic(IntPtr hwnd) => false;
        static bool ShowWindow(IntPtr hwnd, int cmd) => false;
        static bool SetCursorPos(int x, int y) => false;
        static IntPtr GetForegroundWindow() => IntPtr.Zero;
        static uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid) { pid = 0; return 0; }
        static uint GetCurrentThreadId() => 0;
        static bool AttachThreadInput(uint a, uint b, bool c) => false;
        static bool BringWindowToTop(IntPtr hwnd) => false;
#endif

        /// <summary>Desktop pixel for a position in the source image (0..1, origin top-left) given the captured window rectangle. Pure: see the tests.</summary>
        public static Vector2Int SourceToDesktop(Vector2 src, int winX, int winY, int winW, int winH)
            => new Vector2Int(winX + Mathf.Clamp(Mathf.RoundToInt(src.x * winW), 0, Mathf.Max(0, winW - 1)), winY + Mathf.Clamp(Mathf.RoundToInt(src.y * winH), 0, Mathf.Max(0, winH - 1)));

        /// <summary>Normalised absolute coordinates (0..65535 across the virtual desktop) for SendInput.</summary>
        public static Vector2Int DesktopToAbsolute(Vector2Int px, int vx, int vy, int vw, int vh)
            => new Vector2Int(Mathf.RoundToInt((px.x - vx) * 65535f / Mathf.Max(1, vw - 1)), Mathf.RoundToInt((px.y - vy) * 65535f / Mathf.Max(1, vh - 1)));

        bool m_WaitDown, m_UpAfterDown;
        float m_WaitStart;
        Vector2Int m_LastAbs;

        void Update()
        {
            if (!Enabled || !Tool || !Tool.InteractMode) { Release(); return; }
            var panel = Tool.InteractPanel;
            var s = Tool.InteractState;
            if (!panel || panel.Source == null) { Release(); return; }
            var win = panel.Source.Window;
            if (!win.IsValid || !win.alive) { Release(); return; }
            Vector2 src = panel.UvToSource(Tool.InteractUv);
            var px = SourceToDesktop(src, win.x, win.y, win.width, win.height);
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            if (vw <= 0 || vh <= 0) return;
            var abs = DesktopToAbsolute(px, vx, vy, vw, vh);
            m_LastAbs = abs;

            if (Time.unscaledTime >= m_NextMove) { Send(MOVE | ABSOLUTE | VIRTUALDESK, abs.x, abs.y, 0); m_NextMove = Time.unscaledTime + 0.008f; }

            // A press first brings the captured window to the front (it may be covered by this app's own window or others) and waits briefly for that to happen, so the
            // click lands on the window and not on whatever was on top.
            if (s.triggerDown && !m_Left && !m_WaitDown)
            {
                if (win.hwnd != 0 && !win.foreground) { ForceForeground(new IntPtr(win.hwnd)); m_WaitDown = true; m_WaitStart = Time.unscaledTime; m_UpAfterDown = false; LastAction = "focusing"; }
                else { Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTDOWN, abs.x, abs.y, 0); m_Left = true; LastAction = "left down"; }
            }
            if (m_WaitDown)
            {
                if (s.triggerUp || !s.triggerHeld) m_UpAfterDown = true;
                if (win.foreground || Time.unscaledTime - m_WaitStart > 0.4f)
                {
                    Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTDOWN, abs.x, abs.y, 0); m_Left = true; m_WaitDown = false; LastAction = "left down (after focus)";
                    if (m_UpAfterDown) { Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTUP, abs.x, abs.y, 0); m_Left = false; LastAction = "left up"; }
                }
            }
            else if (m_Left && (s.triggerUp || !s.triggerHeld)) { Send(MOVE | ABSOLUTE | VIRTUALDESK | LEFTUP, abs.x, abs.y, 0); m_Left = false; LastAction = "left up"; }

            if (s.secondaryDown) { Send(MOVE | ABSOLUTE | VIRTUALDESK | RIGHTDOWN, abs.x, abs.y, 0); m_Right = true; LastAction = "right down"; }
            else if (m_Right) { Send(MOVE | ABSOLUTE | VIRTUALDESK | RIGHTUP, abs.x, abs.y, 0); m_Right = false; }
            if (Mathf.Abs(s.stick.y) > 0.5f && Time.unscaledTime > m_NextWheel) { Send(WHEEL, 0, 0, (uint)(int)(Mathf.Sign(s.stick.y) * 120)); m_NextWheel = Time.unscaledTime + 0.08f; }
        }

        float m_NextWheel;

        /// <summary>Brings a window to the front even though this process is not the foreground one (attach to the foreground thread's input queue for the call).</summary>
        static void ForceForeground(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9);                       // SW_RESTORE
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(h);
            ShowWindow(h, 5);                                        // SW_SHOW
            SetForegroundWindow(h);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        void Release()
        {
            m_WaitDown = false;
            if (m_Left) { Send(LEFTUP, 0, 0, 0); m_Left = false; }
            if (m_Right) { Send(RIGHTUP, 0, 0, 0); m_Right = false; }
        }

        /// <summary>SendInput failed on the last call (Windows refused: for example the target runs as administrator, or this process is sandboxed). Shown to the user once.</summary>
        public static int FailedSends { get; private set; }
        public static int LastError { get; private set; }

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

        void OnDisable() => Release();
    }
}
