using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace XrssCapture
{
    /// <summary>--snap: runs a real capture session for a moment, then writes the latest frame from the shared buffer to a PNG. Proves the backend end to end without Unity.</summary>
    static class Snap
    {
        public static int Run(Args a)
        {
            string output = a.Get("out") ?? "snap.png";
            int delay = a.Has("delay") ? int.Parse(a.Get("delay")) : 1500;
            IntPtr hwnd = Program.ResolveWindow(a);
            string id = "snap" + Environment.ProcessId;
            using var session = new CaptureSession(hwnd, false, id, 3840, 2160, 30, a.Has("cursor"), IntPtr.Zero);
            session.Error += m => Console.Error.WriteLine("capture: " + m);
            Thread.Sleep(delay);
            if (session.Width <= 0) { Program.Status("error", "no frame arrived (window minimised or protected content?)"); return 3; }
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(FrameProtocol.MapName(id));
            using var view = mmf.CreateViewAccessor();
            unsafe
            {
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                try
                {
                    int w = (int)*(uint*)(p + FrameProtocol.OffWidth), h = (int)*(uint*)(p + FrameProtocol.OffHeight), stride = (int)*(uint*)(p + FrameProtocol.OffStride);
                    int slot = (int)*(uint*)(p + FrameProtocol.OffLatestSlot);
                    int cap = (int)*(uint*)(p + FrameProtocol.OffSlotCapacity);
                    byte* data = p + FrameProtocol.HeaderSize + (long)slot * cap;
                    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < h; y++) Buffer.MemoryCopy(data + (long)y * stride, (byte*)bd.Scan0 + (long)y * bd.Stride, stride, stride);
                    bmp.UnlockBits(bd);
                    // Capture is premultiplied-style BGRA with alpha 0xFF for opaque windows; force opaque so viewers do not show it transparent.
                    bmp.Save(output, ImageFormat.Png);
                    Console.WriteLine("{\"event\":\"snap\",\"w\":" + w + ",\"h\":" + h + ",\"file\":" + WindowList.Json(output) + "}");
                }
                finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
            return 0;
        }
    }
}
