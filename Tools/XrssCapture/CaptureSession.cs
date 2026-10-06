using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace XrssCapture
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    /// <summary>
    /// One Windows.Graphics.Capture session on a window or monitor: every frame is copied (GPU staging texture) into the shared-memory frame buffer
    /// (<see cref="FrameProtocol"/>) that Unity reads. Also publishes the window's client rectangle, foreground and alive flags a few times per second.
    /// </summary>
    sealed class CaptureSession : IDisposable
    {
        static readonly Guid IID_IGraphicsCaptureItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        static readonly Guid IID_ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        readonly object m_Lock = new object();
        readonly IntPtr m_Hwnd;            // zero for a monitor
        readonly IntPtr m_MonitorHandle;
        readonly int m_MaxW, m_MaxH, m_Fps;
        readonly bool m_Cursor;
        ID3D11Device m_Device;
        ID3D11DeviceContext m_Ctx;
        IDirect3DDevice m_WinrtDevice;
        GraphicsCaptureItem m_Item;
        Direct3D11CaptureFramePool m_Pool;
        GraphicsCaptureSession m_Session;
        ID3D11Texture2D m_Staging;
        int m_StagingW, m_StagingH;
        SizeInt32 m_PoolSize;
        MemoryMappedFile m_Mmf;
        MemoryMappedViewAccessor m_View;
        unsafe byte* m_Base;
        uint m_Counter;
        int m_Latest;                       // slot most recently published (the writer fills the other one)
        long m_LastWriteTicks;
        long m_FpsWindowStart;
        int m_FpsFrames;
        uint m_Dropped;
        volatile bool m_Closed;
        Timer m_WindowTimer;

        public bool Closed => m_Closed;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public event Action<string> Error;

        public CaptureSession(IntPtr hwnd, bool monitor, string mapId, int maxW, int maxH, int fps, bool cursor, IntPtr monitorHandle)
        {
            m_Hwnd = monitor ? IntPtr.Zero : hwnd;
            m_MonitorHandle = monitor ? monitorHandle : IntPtr.Zero;
            m_MaxW = maxW; m_MaxH = maxH; m_Fps = Math.Max(1, fps); m_Cursor = cursor;

            // D3D11 device with BGRA support (required by Windows.Graphics.Capture).
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out m_Device, out _, out m_Ctx).CheckError();
            using (var dxgi = m_Device.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr abi));
                try { m_WinrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi); } finally { Marshal.Release(abi); }
            }

            m_Item = CreateItem(monitor ? monitorHandle : hwnd, monitor);
            m_Item.Closed += (s, a) => { m_Closed = true; };

            // Shared frame buffer.
            long total = FrameProtocol.TotalSize(maxW, maxH);
            m_Mmf = MemoryMappedFile.CreateOrOpen(FrameProtocol.MapName(mapId), total, MemoryMappedFileAccess.ReadWrite);
            m_View = m_Mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            unsafe
            {
                byte* p = null;
                m_View.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                m_Base = p;
                WriteU32(FrameProtocol.OffMagic, FrameProtocol.Magic);
                WriteU32(FrameProtocol.OffVersion, FrameProtocol.Version);
                WriteU32(FrameProtocol.OffHeaderSize, FrameProtocol.HeaderSize);
                WriteU32(FrameProtocol.OffSlotCapacity, (uint)FrameProtocol.SlotCapacity(maxW, maxH));
                WriteU32(FrameProtocol.OffMaxWidth, (uint)maxW);
                WriteU32(FrameProtocol.OffMaxHeight, (uint)maxH);
                WriteU32(FrameProtocol.OffHelperPid, (uint)Environment.ProcessId);
                WriteU32(FrameProtocol.OffHwnd, (uint)(m_Hwnd.ToInt64() & 0xFFFFFFFF));
                WriteU32(FrameProtocol.OffFlags, FrameProtocol.FlagAlive);
            }

            m_PoolSize = m_Item.Size;
            m_Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(m_WinrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, m_PoolSize);
            m_Pool.FrameArrived += OnFrameArrived;
            m_Session = m_Pool.CreateCaptureSession(m_Item);
            try { m_Session.IsCursorCaptureEnabled = m_Cursor; } catch { }
            try { m_Session.IsBorderRequired = false; } catch { }             // needs Win11 + capability; harmless if refused
            m_Session.StartCapture();

            UpdateWindowInfo();
            m_WindowTimer = new Timer(_ => UpdateWindowInfo(), null, 100, 100);
        }

        static GraphicsCaptureItem CreateItem(IntPtr handle, bool monitor)
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Guid itemIid = IID_IGraphicsCaptureItem;
            Native.WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.Capture.GraphicsCaptureItem".Length, out IntPtr hs);
            try
            {
                Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hs, ref iid, out IntPtr factory));
                try
                {
                    var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                    IntPtr abi = monitor ? interop.CreateForMonitor(handle, ref itemIid) : interop.CreateForWindow(handle, ref itemIid);
                    try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(abi); } finally { Marshal.Release(abi); }
                }
                finally { Marshal.Release(factory); }
            }
            finally { Native.WindowsDeleteString(hs); }
        }

        unsafe void WriteU32(int off, uint v) => *(uint*)(m_Base + off) = v;
        unsafe void WriteI32(int off, int v) => *(int*)(m_Base + off) = v;
        unsafe void WriteI64(int off, long v) => *(long*)(m_Base + off) = v;
        unsafe uint ReadU32(int off) => *(uint*)(m_Base + off);

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null) return;
                var size = frame.ContentSize;
                if (size.Width != m_PoolSize.Width || size.Height != m_PoolSize.Height)
                {
                    m_PoolSize = size;
                    sender.Recreate(m_WinrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                }
                long now = Stopwatch.GetTimestamp();
                if (m_LastWriteTicks != 0 && (now - m_LastWriteTicks) < Stopwatch.Frequency / m_Fps * 0.8) { m_Dropped++; return; }
                m_LastWriteTicks = now;
                CopyFrame(frame, size.Width, size.Height);
            }
            catch (Exception e) { Error?.Invoke("frame: " + e.Message); }
        }

        void CopyFrame(Direct3D11CaptureFrame frame, int cw, int ch)
        {
            if (cw <= 0 || ch <= 0) return;
            if (cw > m_MaxW || ch > m_MaxH) { Error?.Invoke($"window {cw}x{ch} exceeds the frame buffer {m_MaxW}x{m_MaxH}"); return; }
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid iid = IID_ID3D11Texture2D;
            IntPtr texPtr = access.GetInterface(ref iid);
            using var src = new ID3D11Texture2D(texPtr);
            lock (m_Lock)
            {
                var desc = src.Description;
                if (m_Staging == null || m_StagingW != (int)desc.Width || m_StagingH != (int)desc.Height)
                {
                    m_Staging?.Dispose();
                    var sd = new Texture2DDescription
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
                    };
                    m_Staging = m_Device.CreateTexture2D(sd);
                    m_StagingW = (int)desc.Width; m_StagingH = (int)desc.Height;
                }
                m_Ctx.CopyResource(m_Staging, src);
                var map = m_Ctx.Map(m_Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int slot = 1 - m_Latest;
                    int stride = cw * 4;
                    unsafe
                    {
                        byte* dst = m_Base + FrameProtocol.HeaderSize + (long)slot * FrameProtocol.SlotCapacity(m_MaxW, m_MaxH);
                        byte* srcp = (byte*)map.DataPointer;
                        for (int y = 0; y < ch; y++) Buffer.MemoryCopy(srcp + (long)y * map.RowPitch, dst + (long)y * stride, stride, stride);
                    }
                    Publish(slot, cw, ch, stride);
                }
                finally { m_Ctx.Unmap(m_Staging, 0); }
            }
        }

        void Publish(int slot, int w, int h, int stride)
        {
            Width = w; Height = h;
            WriteU32(FrameProtocol.OffWidth, (uint)w);
            WriteU32(FrameProtocol.OffHeight, (uint)h);
            WriteU32(FrameProtocol.OffStride, (uint)stride);
            WriteU32(FrameProtocol.OffSourceW, (uint)w);
            WriteU32(FrameProtocol.OffSourceH, (uint)h);
            WriteU32(FrameProtocol.OffLatestSlot, (uint)slot);
            WriteI64(FrameProtocol.OffTimestampUs, Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency);
            WriteU32(FrameProtocol.OffDropped, m_Dropped);
            m_Latest = slot;
            System.Threading.Thread.MemoryBarrier();
            WriteU32(FrameProtocol.OffFrameCounter, ++m_Counter);               // last: this is what the reader polls
            m_FpsFrames++;
            long now = Stopwatch.GetTimestamp();
            if (m_FpsWindowStart == 0) m_FpsWindowStart = now;
            if (now - m_FpsWindowStart >= Stopwatch.Frequency)
            {
                WriteU32(FrameProtocol.OffCaptureFps, (uint)m_FpsFrames);
                m_FpsFrames = 0; m_FpsWindowStart = now;
            }
            uint flags = ReadU32(FrameProtocol.OffFlags);
            WriteU32(FrameProtocol.OffFlags, flags | FrameProtocol.FlagReady);
        }

        void UpdateWindowInfo()
        {
            try
            {
                uint flags = FrameProtocol.FlagAlive | (ReadU32(FrameProtocol.OffFlags) & FrameProtocol.FlagReady);
                if (m_Hwnd != IntPtr.Zero)
                {
                    if (!Native.IsWindow(m_Hwnd)) { m_Closed = true; WriteU32(FrameProtocol.OffFlags, flags & ~FrameProtocol.FlagAlive); return; }
                    if (Native.IsIconic(m_Hwnd)) flags |= FrameProtocol.FlagMinimized;
                    if (Native.GetForegroundWindow() == m_Hwnd) flags |= FrameProtocol.FlagForeground;
                    // The captured image is the whole visible window (title bar and frame included), i.e. its extended frame bounds, not the client area.
                    if (Native.DwmGetWindowAttribute(m_Hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, 16) != 0) Native.GetWindowRect(m_Hwnd, out r);
                    WriteI32(FrameProtocol.OffClientX, r.left); WriteI32(FrameProtocol.OffClientY, r.top);
                    WriteI32(FrameProtocol.OffClientW, r.right - r.left); WriteI32(FrameProtocol.OffClientH, r.bottom - r.top);
                }
                else if (m_MonitorHandle != IntPtr.Zero)
                {
                    var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
                    if (Native.GetMonitorInfo(m_MonitorHandle, ref mi))
                    {
                        WriteI32(FrameProtocol.OffClientX, mi.rcMonitor.left); WriteI32(FrameProtocol.OffClientY, mi.rcMonitor.top);
                        WriteI32(FrameProtocol.OffClientW, mi.rcMonitor.right - mi.rcMonitor.left); WriteI32(FrameProtocol.OffClientH, mi.rcMonitor.bottom - mi.rcMonitor.top);
                    }
                    flags |= FrameProtocol.FlagForeground;
                }
                WriteU32(FrameProtocol.OffFlags, flags);
            }
            catch { }
        }

        public unsafe void Dispose()
        {
            m_Closed = true;
            m_WindowTimer?.Dispose();
            try { m_Session?.Dispose(); } catch { }
            try { m_Pool?.Dispose(); } catch { }
            lock (m_Lock)
            {
                m_Staging?.Dispose(); m_Staging = null;
                if (m_Base != null) WriteU32(FrameProtocol.OffFlags, 0);
            }
            try { if (m_Base != null) m_View?.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            m_View?.Dispose(); m_Mmf?.Dispose();
            m_Ctx?.Dispose(); m_Device?.Dispose();
        }
    }
}
