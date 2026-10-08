using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// Gate 2 proof of concept: reads the frames XrssCapture publishes in shared memory (FrameProtocol) and serves them as JPEG over TCP.
// Listens on loopback only; the headset reaches it through `adb reverse tcp:PORT tcp:PORT`, so nothing is exposed to the network.
//
//   XrssStream --id ID [--port 5600] [--fps 30] [--maxw 1600] [--quality 75]
//
// Wire format, little endian. Server -> client, per frame: 'XRSF' u32, jpegLength u32, width i32, height i32, seq u32, then the JPEG bytes.
// Client -> server: the seq (u32) of each frame it has decoded and shown (used to measure round-trip latency).
static class Program
{
    const int OffHwnd = 76;
    const int OffMagic = 0, OffMaxWidth = 16, OffMaxHeight = 20, OffFrameCounter = 24, OffLatestSlot = 28, OffWidth = 32, OffHeight = 36, OffStride = 40;
    const int HeaderSize = 256;
    const uint Magic = 0x43535258, FrameMagic = 0x46535258;     // 'XRSC', 'XRSF'

    static int Main(string[] argv)
    {
        string Arg(string k, string d) { int i = Array.IndexOf(argv, "--" + k); return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : d; }
        string id = Arg("id", "default");
        int port = int.Parse(Arg("port", "5600")), fps = int.Parse(Arg("fps", "30")), maxW = int.Parse(Arg("maxw", "1600")), quality = int.Parse(Arg("quality", "75"));
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        Console.WriteLine($"waiting for capture map XrssFrame-{id} ...");
        MemoryMappedFile mmf = null;
        while (mmf == null) { try { mmf = MemoryMappedFile.OpenExisting("XrssFrame-" + id, MemoryMappedFileRights.Read); } catch { Thread.Sleep(300); } }
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"listening on 127.0.0.1:{port} (jpeg q{quality}, max width {maxW}, {fps} fps cap)");
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            client.NoDelay = true;
            Console.WriteLine("client connected");
            try { Serve(client, view, fps, maxW, jpeg, encParams); }
            catch (Exception e) { Console.WriteLine("client ended: " + e.Message); }
        }
    }

    static unsafe void Serve(TcpClient client, MemoryMappedViewAccessor view, int fps, int maxW, ImageCodecInfo jpeg, EncoderParameters encParams)
    {
        var net = client.GetStream();
        var sentAt = new long[1 << 12];                         // seq -> Stopwatch ticks when the frame was fully written
        long ackCount = 0, rttTicksSum = 0, rttMaxTicks = 0;
        // Headset -> PC messages: 'A' + seq u32 (frame shown); 'P' + kind u8 + u f32 + v f32 + wheel f32 (pointer event, see InputInjector); 'L' (pointer lost).
        var injector = new InputInjector(() => new IntPtr((long)view.ReadUInt32(OffHwnd)));
        var ackThread = new Thread(() =>
        {
            var b = new byte[16];
            void Fill(int n) { int got = 0; while (got < n) { int r = net.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; } }
            try
            {
                while (true)
                {
                    Fill(1);
                    switch ((char)b[0])
                    {
                        case 'A':
                            Fill(4);
                            uint seq = BitConverter.ToUInt32(b, 0);
                            long rtt = Stopwatch.GetTimestamp() - Volatile.Read(ref sentAt[seq & (sentAt.Length - 1)]);
                            Interlocked.Increment(ref ackCount); Interlocked.Add(ref rttTicksSum, rtt);
                            long cur; do { cur = Interlocked.Read(ref rttMaxTicks); } while (rtt > cur && Interlocked.CompareExchange(ref rttMaxTicks, rtt, cur) != cur);
                            break;
                        case 'P': Fill(13); injector.Handle(b[0], BitConverter.ToSingle(b, 1), BitConverter.ToSingle(b, 5), BitConverter.ToSingle(b, 9)); break;
                        case 'L': injector.Handle(InputInjector.Lost, 0, 0, 0); break;
                        default: throw new InvalidDataException("unknown message " + b[0]);
                    }
                }
            }
            catch { }
            finally { injector.Release("headset disconnected"); }
        }) { IsBackground = true };
        ackThread.Start();

        uint lastCounter = 0, seq2 = 0;
        long minGap = Stopwatch.Frequency / fps, lastSend = 0, statStart = Stopwatch.GetTimestamp();
        long frames = 0, bytes = 0, encTicks = 0, lastAck = 0, lastRtt = 0, lastMax = 0;
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            while (client.Connected)
            {
                uint counter = *(uint*)(basePtr + OffFrameCounter);
                long now = Stopwatch.GetTimestamp();
                if (counter == lastCounter || now - lastSend < minGap) { Thread.Sleep(2); continue; }
                int w = *(int*)(basePtr + OffWidth), h = *(int*)(basePtr + OffHeight), stride = *(int*)(basePtr + OffStride);
                int slot = *(int*)(basePtr + OffLatestSlot), cap = *(int*)(basePtr + 12);
                if (w <= 0 || h <= 0) { Thread.Sleep(10); continue; }
                long t0 = Stopwatch.GetTimestamp();
                byte[] data;
                using (var src = new Bitmap(w, h, PixelFormat.Format32bppRgb))
                {
                    var bd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    byte* from = basePtr + HeaderSize + (long)slot * cap;
                    for (int y = 0; y < h; y++) Buffer.MemoryCopy(from + (long)y * stride, (byte*)bd.Scan0 + (long)y * bd.Stride, bd.Stride, w * 4);
                    src.UnlockBits(bd);
                    if (*(uint*)(basePtr + OffFrameCounter) - counter > 1) continue;      // torn: the writer lapped us while copying
                    int ow = Math.Min(w, maxW), oh = (int)((long)h * ow / w);
                    using var scaled = ow == w ? null : new Bitmap(ow, oh, PixelFormat.Format32bppRgb);
                    if (scaled != null)
                    {
                        using var g = Graphics.FromImage(scaled);
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(src, 0, 0, ow, oh);
                    }
                    using var ms = new MemoryStream();
                    (scaled ?? src).Save(ms, jpeg, encParams);
                    data = ms.ToArray(); w = ow; h = oh;
                }
                encTicks += Stopwatch.GetTimestamp() - t0;
                var header = new byte[20];
                BitConverter.GetBytes(FrameMagic).CopyTo(header, 0); BitConverter.GetBytes((uint)data.Length).CopyTo(header, 4);
                BitConverter.GetBytes(w).CopyTo(header, 8); BitConverter.GetBytes(h).CopyTo(header, 12); BitConverter.GetBytes(++seq2).CopyTo(header, 16);
                Volatile.Write(ref sentAt[seq2 & (sentAt.Length - 1)], Stopwatch.GetTimestamp());
                net.Write(header, 0, 20); net.Write(data, 0, data.Length);
                lastCounter = counter; lastSend = now; frames++; bytes += data.Length;

                long since = Stopwatch.GetTimestamp() - statStart;
                if (since >= 3 * Stopwatch.Frequency)
                {
                    double sec = (double)since / Stopwatch.Frequency, ms = 1000.0 / Stopwatch.Frequency;
                    long acks = Interlocked.Read(ref ackCount), rtt = Interlocked.Read(ref rttTicksSum), max = Interlocked.Read(ref rttMaxTicks);
                    long dAck = acks - lastAck;
                    Console.WriteLine($"sent {frames / sec:0.0} fps, {bytes * 8 / sec / 1e6:0.0} Mbit/s, avg {bytes / Math.Max(1, frames) / 1024} KB/frame, encode {encTicks * ms / Math.Max(1, frames):0.0} ms | acked {dAck / sec:0.0} fps, round trip avg {(dAck > 0 ? (rtt - lastRtt) * ms / dAck : 0):0} ms max {max * ms:0} ms | {w}x{h}");
                    frames = 0; bytes = 0; encTicks = 0; lastAck = acks; lastRtt = rtt; Interlocked.Exchange(ref rttMaxTicks, 0); statStart = Stopwatch.GetTimestamp();
                }
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
}
