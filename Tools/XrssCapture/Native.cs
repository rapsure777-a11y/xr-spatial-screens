using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XrssCapture
{
    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
        public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("combase.dll", PreserveSig = true)] public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
        [DllImport("combase.dll", PreserveSig = true)] public static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
        public const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        public static string Title(IntPtr hwnd)
        {
            int n = GetWindowTextLength(hwnd);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 2);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int v, 4) == 0 && v != 0;
    }
}
