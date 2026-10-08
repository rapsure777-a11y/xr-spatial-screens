using System;
using System.Diagnostics;
using SkiaSharp;
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
            try { Serve(client, view, fps, maxW, quality); }
            catch (Exception e) { Console.WriteLine("client ended: " + e.Message); }
        }
    }

    static unsafe void Serve(TcpClient client, MemoryMappedViewAccessor view, int fps, int maxW, int quality)
    {
        var net = client.GetStream();
        var sentAt = new long[1 << 12];                         // seq -> Stopwatch ticks when the frame was fully written
        long ackCount = 0, rttTicksSum = 0, rttMaxTicks = 0;
        // Headset -> PC messages: 'A' + seq u32 (frame shown); 'P' + kind u8 + u f32 + v f32 + wheel f32 (pointer event, see InputInjector); 'L' (pointer lost).
        bool clientGone = false;                                // set when the read side ends, so a silent (static-window) connection is dropped too
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
            finally { injector.Release("headset disconnected"); clientGone = true; }
        }) { IsBackground = true };
        ackThread.Start();

        // Frames are copied out of shared memory (a few ms) and encoded on up to Workers pool threads in parallel: one JPEG encode takes ~40 ms, so a single
        // thread caps at ~25 fps. Finished frames are written in order; a frame that finishes after a newer one was already sent is dropped.
        const int Workers = 3;
        uint lastCounter = 0, seq2 = 0, lastWritten = 0;
        long minGap = Stopwatch.Frequency / fps, lastSend = 0, statStart = Stopwatch.GetTimestamp();
        long frames = 0, bytes = 0, encTicks = 0, scaleTicks = 0, lastAck = 0, lastRtt = 0, lastW = 0, lastH = 0;
        int inFlight = 0;
        var writeLock = new object();
        var pool = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            while (client.Connected && !Volatile.Read(ref clientGone))
            {
                long since = Stopwatch.GetTimestamp() - statStart;
                if (since >= 3 * Stopwatch.Frequency)
                {
                    double sec = (double)since / Stopwatch.Frequency, ms = 1000.0 / Stopwatch.Frequency;
                    long f = Interlocked.Exchange(ref frames, 0), b = Interlocked.Exchange(ref bytes, 0), et = Interlocked.Exchange(ref encTicks, 0), st = Interlocked.Exchange(ref scaleTicks, 0);
                    long acks = Interlocked.Read(ref ackCount), rtt = Interlocked.Read(ref rttTicksSum), max = Interlocked.Exchange(ref rttMaxTicks, 0);
                    long dAck = acks - lastAck;
                    if (f > 0 || dAck > 0)
                        Console.WriteLine($"sent {f / sec:0.0} fps, {b * 8 / sec / 1e6:0.0} Mbit/s, avg {b / Math.Max(1, f) / 1024} KB/frame, encode {et * ms / Math.Max(1, f):0.0} ms (scale {st * ms / Math.Max(1, f):0.0}, {Workers} threads) | acked {dAck / sec:0.0} fps, round trip avg {(dAck > 0 ? (rtt - lastRtt) * ms / dAck : 0):0} ms max {max * ms:0} ms | {Interlocked.Read(ref lastW)}x{Interlocked.Read(ref lastH)}");
                    lastAck = acks; lastRtt = rtt; statStart = Stopwatch.GetTimestamp();
                }

                uint counter = *(uint*)(basePtr + OffFrameCounter);
                long now = Stopwatch.GetTimestamp();
                if (counter == lastCounter || now - lastSend < minGap || Volatile.Read(ref inFlight) >= Workers) { Thread.Sleep(1); continue; }
                int w = *(int*)(basePtr + OffWidth), h = *(int*)(basePtr + OffHeight), stride = *(int*)(basePtr + OffStride);
                int slot = *(int*)(basePtr + OffLatestSlot), cap = *(int*)(basePtr + 12);
                if (w <= 0 || h <= 0) { Thread.Sleep(10); continue; }

                long t0 = Stopwatch.GetTimestamp();
                int bytesNeeded = stride * h;
                if (!pool.TryTake(out var buf) || buf.Length < bytesNeeded) buf = new byte[bytesNeeded];
                fixed (byte* dstp = buf) Buffer.MemoryCopy(basePtr + HeaderSize + (long)slot * cap, dstp, buf.Length, bytesNeeded);
                if (*(uint*)(basePtr + OffFrameCounter) - counter > 1) { pool.Add(buf); continue; }      // torn: the writer lapped us while copying
                uint id = ++seq2; lastCounter = counter; lastSend = now;
                Interlocked.Increment(ref inFlight);
                int cw = w, ch = h, cstride = stride;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    try
                    {
                        // SkiaSharp (libjpeg-turbo): scale into the output size, then encode 4:4:4 so coloured text edges stay clean.
                        int ow = Math.Min(cw, maxW), oh = (int)((long)ch * ow / cw);
                        byte[] data;
                        using (var dst = new SKBitmap(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Opaque)))
                        {
                            fixed (byte* sp = buf)
                            using (var srcPix = new SKPixmap(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque), (IntPtr)sp, cstride))
                                srcPix.ScalePixels(dst.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                            Interlocked.Add(ref scaleTicks, Stopwatch.GetTimestamp() - t0);
                            using var enc = dst.PeekPixels().Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
                            data = enc.ToArray();
                        }
                        Interlocked.Add(ref encTicks, Stopwatch.GetTimestamp() - t0);
                        var header = new byte[20];
                        BitConverter.GetBytes(FrameMagic).CopyTo(header, 0); BitConverter.GetBytes((uint)data.Length).CopyTo(header, 4);
                        BitConverter.GetBytes(ow).CopyTo(header, 8); BitConverter.GetBytes(oh).CopyTo(header, 12); BitConverter.GetBytes(id).CopyTo(header, 16);
                        lock (writeLock)
                        {
                            if (id > lastWritten)
                            {
                                Volatile.Write(ref sentAt[id & (sentAt.Length - 1)], Stopwatch.GetTimestamp());
                                net.Write(header, 0, 20); net.Write(data, 0, data.Length);
                                lastWritten = id;
                                Interlocked.Increment(ref frames); Interlocked.Add(ref bytes, data.Length);
                                Interlocked.Exchange(ref lastW, ow); Interlocked.Exchange(ref lastH, oh);
                            }
                        }
                    }
                    catch { Volatile.Write(ref clientGone, true); }
                    finally { pool.Add(buf); Interlocked.Decrement(ref inFlight); }
                }, null);
            }
            while (Volatile.Read(ref inFlight) > 0) Thread.Sleep(5);              // let running encodes finish before the shared-memory view goes away
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
}
