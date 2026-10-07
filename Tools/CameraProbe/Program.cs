using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Read-only probe: does SteamVR give a normal PC application access to the headset's passthrough camera (IVRTrackedCamera)?
// Changes nothing: it connects as a background application, asks about the camera, tries to open a video stream and read one frame, then disconnects.
// usage: CameraProbe.exe [path-to-openvr_api.dll]
static unsafe class Program
{
    const string DefaultDll = @"D:\SteamLibrary\steamapps\common\SteamVR\bin\win64\openvr_api.dll";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr h, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr InitFn(ref int err, int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetIfaceFn([MarshalAs(UnmanagedType.LPStr)] string v, ref int err);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrTextFn(int err);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HasCameraFn(IntPtr self, uint dev, out byte has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FrameSizeFn(IntPtr self, uint dev, int type, out uint w, out uint h, out uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AcquireFn(IntPtr self, uint dev, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReleaseFn(IntPtr self, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FrameBufferFn(IntPtr self, ulong handle, int type, byte* buf, uint size, byte* header, uint headerSize);

    static T Fn<T>(IntPtr dll, string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetProcAddress(dll, n));
    static string Err(int e) => e switch { 0 => "None", 100 => "NotSupportedForThisDevice", 101 => "NoFrameAvailable", 102 => "InvalidArgument", 103 => "InvalidFrameBufferSize", 104 => "OperationFailed", 105 => "InvalidFrameHeaderVersion", 106 => "UnknownDeviceOrIndex", 107 => "InvalidUnavailable", 108 => "FrameBufferTooSmall", _ => "code " + e };

    static int Main(string[] a)
    {
        string dllPath = a.Length > 0 ? a[0] : DefaultDll;
        var dll = LoadLibrary(dllPath);
        if (dll == IntPtr.Zero) { Console.WriteLine("cannot load " + dllPath); return 2; }

        // The setting that gates the camera (default off). Only read, never written.
        string vrs = @"C:\Program Files (x86)\Steam\config\steamvr.vrsettings";
        if (File.Exists(vrs))
        {
            var m = Regex.Match(File.ReadAllText(vrs), @"""camera""\s*:\s*\{[^}]*?""enableCamera""\s*:\s*(true|false)", RegexOptions.Singleline);
            Console.WriteLine("steamvr.vrsettings camera.enableCamera: " + (m.Success ? m.Groups[1].Value : "(not set: default false)"));
        }

        int err = 0;
        Fn<InitFn>(dll, "VR_InitInternal")(ref err, 3);          // VRApplication_Background
        Console.WriteLine("VR_Init: " + Marshal.PtrToStringAnsi(Fn<ErrTextFn>(dll, "VR_GetVRInitErrorAsEnglishDescription")(err)));
        if (err != 0) return 3;
        int rc = 1;
        try
        {
            int e2 = 0;
            var cam = Fn<GetIfaceFn>(dll, "VR_GetGenericInterface")("IVRTrackedCamera_006", ref e2);
            if (cam == IntPtr.Zero) { Console.WriteLine("IVRTrackedCamera not available, error " + e2); return 4; }
            var vt = Marshal.ReadIntPtr(cam);
            T Slot<T>(int i) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vt, i * IntPtr.Size));
            var has = Slot<HasCameraFn>(1); var size = Slot<FrameSizeFn>(2); var acquire = Slot<AcquireFn>(5); var release = Slot<ReleaseFn>(6); var buffer = Slot<FrameBufferFn>(7);

            int r = has(cam, 0, out byte hc);
            Console.WriteLine($"HasCamera(HMD): result={Err(r)} hasCamera={hc}");
            foreach (int t in new[] { 0, 1, 2 })
            {
                r = size(cam, 0, t, out uint w, out uint h, out uint bytes);
                Console.WriteLine($"GetCameraFrameSize(type {t}: {(t == 0 ? "distorted" : t == 1 ? "undistorted" : "maximum undistorted")}): result={Err(r)} {w}x{h}, {bytes} bytes");
            }
            r = acquire(cam, 0, out ulong handle);
            Console.WriteLine($"AcquireVideoStreamingService: result={Err(r)} handle={handle}");
            if (r == 0 && handle != 0)
            {
                size(cam, 0, 0, out uint w, out uint h, out uint bytes);
                var buf = new byte[Math.Max(bytes, 16)]; var header = new byte[64];
                for (int i = 0; i < 20; i++)
                {
                    fixed (byte* b = buf) fixed (byte* hd = header) r = buffer(cam, handle, 0, b, (uint)buf.Length, hd, (uint)header.Length);
                    if (r == 0) { long sum = 0; for (int k = 0; k < buf.Length; k += 97) sum += buf[k]; Console.WriteLine($"GetVideoStreamFrameBuffer: frame received after {i} tries, {buf.Length} bytes, sample checksum {sum}"); rc = 0; break; }
                    System.Threading.Thread.Sleep(100);
                    if (i == 19) Console.WriteLine("GetVideoStreamFrameBuffer: no frame after 2 s, last result " + Err(r));
                }
                release(cam, handle);
            }
        }
        finally { Fn<ShutdownFn>(dll, "VR_ShutdownInternal")(); }
        Console.WriteLine(rc == 0 ? "RESULT: a PC application CAN read passthrough camera frames through SteamVR on this setup." : "RESULT: no camera frames were obtained.");
        return rc;
    }
}
