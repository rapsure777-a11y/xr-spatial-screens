using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// XrssStream, the PC host for the headset client. Serves many captured windows over one TCP connection (loopback; the headset reaches it through `adb reverse`).
//
//   XrssStream [--port 5600] [--fps 60] [--maxw 2880] [--quality 90] [--capture path\to\XrssCapture.exe]
//
// Wire format (little endian). Every message starts with a 4-byte magic (server -> client) or a 1-byte type (client -> server).
//   server -> client
//     frame  'XRS2' u32, jpegLength u32, width i32, height i32, seq u32, stream u16, pad u16, then the JPEG bytes
//     list   'XRSL' u32, jsonLength u32, then UTF-8 JSON: the capturable windows (what XrssCapture --list prints)
//     status 'XRSS' u32, stream u16, state u8 (0 ended, 1 opened, 2 failed), pad u8   (12 bytes)
//     audio  'XRSA' u32, pcmLength u32, sampleRate u32, channels u16 (2), pad u16, then 16-bit little-endian interleaved PCM  (16-byte header; the PC's output, in ~10 ms chunks)
//     layout 'XRSY' u32, nameLength u16, pad u16, dataLength u32, then the name and the layout file (one message per backed-up layout)
//     video  'XRSV' u32, length u32, width i32, height i32, seq u32, stream u16, flags u8 (1 = keyframe), pad u8, then one HEVC access unit (Annex-B, parameter sets in-band on keyframes)
//   client -> server
//     'V' stream u16, codec u8             0 JPEG (default), 1 hardware HEVC (needs ffmpeg with AMD AMF on the PC)
//     'Y' nameLength u8, name, dataLength u32, data   back up a layout file on the PC (name: letters, digits . _ - only)
//     'Z'                                  send me every backed-up layout
//     'S' on u8                            start (1) or stop (0) sending the PC's sound
//     'R' stream u16, maxWidth u16, fps u8 quality for one stream: the widest picture to send and its frame-rate cap (out-of-view screens ask for a low rate)
//     'W'                                  send me the window list
//     'O' stream u16, hwnd i64             open a stream for that window
//     'X' stream u16                       close a stream
//     'A' stream u16, seq u32              frame shown (round-trip measurement)
//     'P' stream u16, kind u8, u f32, v f32, wheel f32   pointer event on the stream's window (see InputInjector)
//     'L' stream u16                       pointer lost: release anything held
//     'K' stream u16, kind u8, code u32    key: kind 0 type the character (UTF-16 code), 1 key down, 2 key up (Windows virtual-key code); the window is brought to the front first
static unsafe class Program
{
    public const uint ListMagic = 0x4C535258, FrameMagic = 0x32535258, StatusMagic = 0x53535258, AudioMagic = 0x41535258, LayoutMagic = 0x59535258, VideoMagic = 0x56535258;     // 'XRSL', 'XRS2', 'XRSS', 'XRSA', 'XRSY', 'XRSV'
    public static string CapturePath;
    public static int Fps = 60, MaxW = 2880, Quality = 90;

    static int Main(string[] argv)
    {
        string Arg(string k, string d) { int i = Array.IndexOf(argv, "--" + k); return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : d; }
        int port = int.Parse(Arg("port", "5600"));
        Fps = int.Parse(Arg("fps", "60")); MaxW = int.Parse(Arg("maxw", "2880")); Quality = int.Parse(Arg("quality", "90"));
        CapturePath = Path.GetFullPath(Arg("capture", Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "XrssCapture", "publish", "XrssCapture.exe")));
        if (!File.Exists(CapturePath)) { Console.WriteLine("capture helper not found: " + CapturePath); return 2; }
        HevcPipe.FfmpegPath = HevcPipe.Find(Arg("ffmpeg", ""));
        Console.WriteLine(HevcPipe.FfmpegPath != null ? "hevc video available (ffmpeg " + HevcPipe.FfmpegPath + ")" : "hevc video unavailable (no ffmpeg found); streams stay on JPEG");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"listening on 127.0.0.1:{port} (jpeg q{Quality} 4:4:4, max width {MaxW}, {Fps} fps cap, helper {CapturePath})");
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            client.NoDelay = true;
            Console.WriteLine("client connected");
            try { new Session(client).Run(); }
            catch (Exception e) { Console.WriteLine("client ended: " + e.Message); }
            Console.WriteLine("client gone");
        }
    }
}

/// <summary>One captured window: its helper process, its shared-memory frame map, and what has been sent.</summary>
sealed unsafe class StreamState
{
    const int OffFrameCounter = 24, OffLatestSlot = 28, OffWidth = 32, OffHeight = 36, OffStride = 40, OffSlotCapacity = 12, HeaderSize = 256;

    public readonly ushort Id;
    public readonly long Hwnd;
    public readonly string MapName;
    public Process Capture;
    public MemoryMappedFile Mmf;
    public MemoryMappedViewAccessor View;
    public byte* Base;
    public bool Ready, Ended;
    public long OpenedAt = Stopwatch.GetTimestamp();
    public uint LastCounter, Seq, LastWritten;
    public long LastSend;
    public int InFlight;
    /// <summary>Per-stream quality the headset asked for ('R'): the widest picture worth sending and the frame rate cap (a screen out of view asks for a low rate).</summary>
    public volatile int MaxW = Program.MaxW, FpsCap = Program.Fps;
    public readonly long[] SentAt = new long[1 << 10];
    public readonly InputInjector Injector;
    // statistics since the last print
    public long Frames, Bytes, EncTicks, ScaleTicks, Acks, RttTicks, RttMaxTicks;
    public int LastW, LastH;
    public readonly object WriteLock = new object();
    /// <summary>0 = JPEG, 1 = hardware HEVC through <see cref="Pipe"/>. Pending = submitted pictures not yet back from the encoder, oldest first.</summary>
    public volatile int Codec;
    public HevcPipe Pipe;
    public int DupSeq;
    public readonly ConcurrentQueue<uint> PendingIds = new ConcurrentQueue<uint>();

    public StreamState(ushort id, long hwnd, int port)
    {
        Id = id; Hwnd = hwnd; MapName = $"h{port}-{id}";
        Injector = new InputInjector(() => new IntPtr(Hwnd));
        var sb = new System.Text.StringBuilder(128); GetWindowText(new IntPtr(hwnd), sb, 128);
        Injector.Name = $"stream {id}: {sb}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    bool m_RestoredFromMinimized;

    /// <summary>A minimized window has nothing to capture. Bring it back without taking focus; it is minimized again when the stream closes.</summary>
    void RestoreIfMinimized()
    {
        var h = new IntPtr(Hwnd);
        if (!IsWindow(h) || !IsIconic(h)) return;
        ShowWindow(h, 4 /* SW_SHOWNOACTIVATE */);
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        m_RestoredFromMinimized = true;
        Thread.Sleep(350);                                           // let the window draw before the first capture
        Console.WriteLine($"stream {Id}: window was minimized, restored it");
    }

    public void StartCapture()
    {
        RestoreIfMinimized();
        var psi = new ProcessStartInfo(Program.CapturePath, $"--run --id {MapName} --hwnd {Hwnd} --fps {Program.Fps}")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        Capture = Process.Start(psi);
        // The helper prints status events; drain them so its pipe never fills, and note errors.
        var cap = Capture;
        new Thread(() => { try { string l; while ((l = cap.StandardOutput.ReadLine()) != null) if (l.Contains("\"error\"")) Console.WriteLine($"stream {Id}: {l}"); } catch { } }) { IsBackground = true }.Start();
    }

    /// <summary>True once the helper has created the shared-memory map.</summary>
    public bool TryOpenMap()
    {
        if (Ready) return true;
        try
        {
            Mmf = MemoryMappedFile.OpenExisting("XrssFrame-" + MapName, MemoryMappedFileRights.Read);
            View = Mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null; View.SafeMemoryMappedViewHandle.AcquirePointer(ref p); Base = p;
            Ready = true; return true;
        }
        catch { return false; }
    }

    public bool HelperDied => Capture != null && Capture.HasExited;

    public bool HasNewFrame(out uint counter, out int w, out int h, out int stride, out int slot, out int cap)
    {
        counter = *(uint*)(Base + OffFrameCounter); w = *(int*)(Base + OffWidth); h = *(int*)(Base + OffHeight); stride = *(int*)(Base + OffStride);
        slot = *(int*)(Base + OffLatestSlot); cap = *(int*)(Base + OffSlotCapacity);
        // Only frames with a sane shape go on: a window that is resizing (a video going full screen) can publish an odd size for a moment, and handing that to the JPEG encoder crashed the whole host.
        return counter != LastCounter && w >= 16 && h >= 16 && w <= 8192 && h <= 8192 && stride >= w * 4 && (slot == 0 || slot == 1) && (long)stride * h <= cap;
    }

    // ------------------------------------------------------------------ helper crashes

    long m_RestartWindowStart; int m_Restarts;

    public bool WindowExists => IsWindow(new IntPtr(Hwnd));

    /// <summary>The capture helper died but the window is still there (the helper crashed): start it again, at most 4 times a minute. False when it should not be retried.</summary>
    public bool TryRestartCapture()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - m_RestartWindowStart > 60 * Stopwatch.Frequency) { m_RestartWindowStart = now; m_Restarts = 0; }
        if (!WindowExists || ++m_Restarts > 4) return false;
        try { if (Base != null) View.SafeMemoryMappedViewHandle.ReleasePointer(); Base = null; View?.Dispose(); Mmf?.Dispose(); } catch { }
        View = null; Mmf = null; Ready = false; LastCounter = 0; OpenedAt = now;
        try { StartCapture(); Console.WriteLine($"stream {Id}: capture helper restarted ({m_Restarts})"); return true; } catch { return false; }
    }

    public uint CounterNow => *(uint*)(Base + OffFrameCounter);
    public byte* Slot(int slot, int cap) => Base + HeaderSize + (long)slot * cap;

    public void Dispose()
    {
        Ended = true;
        try { Pipe?.Dispose(); } catch { }
        Injector.Release("stream closed"); Injector.ReleaseKeys();
        if (m_RestoredFromMinimized) { var h = new IntPtr(Hwnd); if (IsWindow(h)) ShowWindow(h, 6 /* SW_MINIMIZE */); }       // leave the desktop as it was
        try { if (Capture != null && !Capture.HasExited) { Capture.StandardInput.Close(); if (!Capture.WaitForExit(500)) Capture.Kill(); } } catch { }
        try { if (Base != null) View.SafeMemoryMappedViewHandle.ReleasePointer(); Base = null; View?.Dispose(); Mmf?.Dispose(); } catch { }
    }
}

/// <summary>One headset connection: reads its requests on a thread and, on the calling thread, encodes and sends frames for every open stream.</summary>
sealed unsafe class Session
{
    const int Workers = 4;
    readonly TcpClient m_Client;
    readonly NetworkStream m_Net;
    readonly object m_WriteLock = new object();
    readonly Dictionary<ushort, StreamState> m_Streams = new Dictionary<ushort, StreamState>();
    readonly object m_StreamsLock = new object();
    readonly ConcurrentBag<byte[]> m_Pool = new ConcurrentBag<byte[]>();
    volatile bool m_Gone;
    int m_InFlight;
    readonly int m_Port;

    public Session(TcpClient client) { m_Client = client; m_Net = client.GetStream(); m_Port = ((IPEndPoint)client.Client.LocalEndPoint).Port; }

    void Send(byte[] a, byte[] b = null)
    {
        lock (m_WriteLock) { m_Net.Write(a, 0, a.Length); if (b != null) m_Net.Write(b, 0, b.Length); }
    }

    void SendStatus(ushort id, byte state)
    {
        var m = new byte[12]; BitConverter.GetBytes(Program.StatusMagic).CopyTo(m, 0); BitConverter.GetBytes(id).CopyTo(m, 4); m[6] = state;
        try { Send(m); } catch { m_Gone = true; }
    }

    // ------------------------------------------------------------------ requests from the headset

    void ReadLoop()
    {
        var b = new byte[32];
        void Fill(int n) { int got = 0; while (got < n) { int r = m_Net.Read(b, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; } }
        byte[] Take(int n) { var buf = new byte[n]; int got = 0; while (got < n) { int r = m_Net.Read(buf, got, n - got); if (r <= 0) throw new EndOfStreamException(); got += r; } return buf; }
        try
        {
            while (true)
            {
                Fill(1);
                switch ((char)b[0])
                {
                    case 'W': SendWindowList(); break;
                    case 'O': { Fill(10); ushort id = BitConverter.ToUInt16(b, 0); long hwnd = BitConverter.ToInt64(b, 2); OpenStream(id, hwnd); break; }
                    case 'X': { Fill(2); CloseStream(BitConverter.ToUInt16(b, 0)); break; }
                    case 'A':
                        {
                            Fill(6); ushort id = BitConverter.ToUInt16(b, 0); uint seq = BitConverter.ToUInt32(b, 2);
                            var s = Find(id); if (s == null) break;
                            long rtt = Stopwatch.GetTimestamp() - Volatile.Read(ref s.SentAt[seq & (s.SentAt.Length - 1)]);
                            Interlocked.Increment(ref s.Acks); Interlocked.Add(ref s.RttTicks, rtt);
                            long cur; do { cur = Interlocked.Read(ref s.RttMaxTicks); } while (rtt > cur && Interlocked.CompareExchange(ref s.RttMaxTicks, rtt, cur) != cur);
                            break;
                        }
                    case 'P':
                        {
                            Fill(15); var s = Find(BitConverter.ToUInt16(b, 0));
                            s?.Injector.Handle(b[2], BitConverter.ToSingle(b, 3), BitConverter.ToSingle(b, 7), BitConverter.ToSingle(b, 11));
                            break;
                        }
                    case 'L': { Fill(2); Find(BitConverter.ToUInt16(b, 0))?.Injector.Handle(InputInjector.Lost, 0, 0, 0); break; }
                    case 'S': { Fill(1); SetAudio(b[0] != 0); break; }
                    case 'R':
                        {
                            Fill(5); var s = Find(BitConverter.ToUInt16(b, 0));
                            if (s != null) { s.MaxW = Math.Clamp((int)BitConverter.ToUInt16(b, 2), 320, 7680); s.FpsCap = Math.Clamp((int)b[4], 1, Program.Fps); }
                            break;
                        }
                    case 'Y':
                        {
                            Fill(1); int nl = b[0]; var nameBytes = Take(nl); Fill(4); int dl = (int)BitConverter.ToUInt32(b, 0);
                            if (dl < 0 || dl > LayoutStore.MaxBytes) throw new InvalidDataException("layout too large");
                            var data = Take(dl); string name = Encoding.UTF8.GetString(nameBytes);
                            Console.WriteLine(LayoutStore.Save(name, data) ? $"layout backup saved: {name} ({dl} bytes)" : $"layout backup refused: '{name}'");
                            break;
                        }
                    case 'Z': SendLayouts(); break;
                    case 'V': { Fill(3); var s = Find(BitConverter.ToUInt16(b, 0)); if (s != null) { s.Codec = b[2] == 1 && HevcPipe.FfmpegPath != null ? 1 : 0; Console.WriteLine($"stream {s.Id}: codec {(s.Codec == 1 ? "HEVC" : "JPEG")}"); } break; }
                    case 'K': { Fill(7); Find(BitConverter.ToUInt16(b, 0))?.Injector.Key(b[2], BitConverter.ToUInt32(b, 3)); break; }
                    default: throw new InvalidDataException("unknown message " + b[0]);
                }
            }
        }
        catch { }
        finally { m_Gone = true; }
    }

    StreamState Find(ushort id) { lock (m_StreamsLock) return m_Streams.TryGetValue(id, out var s) ? s : null; }

    /// <summary>Sends every backed-up layout to the headset (it asks after a start-up so a reinstalled app gets its screens back).</summary>
    void SendLayouts()
    {
        try
        {
            var all = LayoutStore.All();
            foreach (var kv in all)
            {
                var name = Encoding.UTF8.GetBytes(kv.Key);
                var h = new byte[12]; BitConverter.GetBytes(Program.LayoutMagic).CopyTo(h, 0); BitConverter.GetBytes((ushort)name.Length).CopyTo(h, 4); BitConverter.GetBytes((uint)kv.Value.Length).CopyTo(h, 8);
                lock (m_WriteLock) { m_Net.Write(h, 0, h.Length); m_Net.Write(name, 0, name.Length); m_Net.Write(kv.Value, 0, kv.Value.Length); }
            }
            Console.WriteLine($"layout backup: sent {all.Count} file(s) to the headset");
        }
        catch (Exception e) { Console.WriteLine("layout backup send failed: " + e.Message); m_Gone = true; }
    }

    AudioSender m_Audio;
    long m_AudioStatAt;

    /// <summary>Starts or stops sending the PC's sound to the headset.</summary>
    void SetAudio(bool on)
    {
        lock (m_StreamsLock)
        {
            if (on && m_Audio == null)
            {
                try
                {
                    m_Audio = new AudioSender((pcm, rate, ch) =>
                    {
                        var h = new byte[16]; BitConverter.GetBytes(Program.AudioMagic).CopyTo(h, 0); BitConverter.GetBytes((uint)pcm.Length).CopyTo(h, 4);
                        BitConverter.GetBytes((uint)rate).CopyTo(h, 8); BitConverter.GetBytes((ushort)ch).CopyTo(h, 12);
                        try { Send(h, pcm); } catch { m_Gone = true; }
                    });
                }
                catch (Exception e) { Console.WriteLine("audio capture failed: " + e.Message); }
            }
            else if (!on && m_Audio != null) { m_Audio.Dispose(); m_Audio = null; Console.WriteLine("audio: stopped"); }
        }
    }

    void SendWindowList()
    {
        string json = "[]";
        try
        {
            var psi = new ProcessStartInfo(Program.CapturePath, "--list") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi);
            json = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
        }
        catch (Exception e) { Console.WriteLine("window list failed: " + e.Message); }
        var body = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var m = new byte[8]; BitConverter.GetBytes(Program.ListMagic).CopyTo(m, 0); BitConverter.GetBytes((uint)body.Length).CopyTo(m, 4);
        try { Send(m, body); } catch { m_Gone = true; }
    }

    void OpenStream(ushort id, long hwnd)
    {
        lock (m_StreamsLock)
        {
            if (m_Streams.TryGetValue(id, out var old)) { m_Streams.Remove(id); old.Dispose(); }
            var s = new StreamState(id, hwnd, m_Port);
            try { s.StartCapture(); m_Streams[id] = s; Console.WriteLine($"stream {id}: opening window {hwnd}"); }
            catch (Exception e) { Console.WriteLine($"stream {id}: cannot start the capture helper: {e.Message}"); SendStatus(id, 2); }
        }
    }

    void CloseStream(ushort id)
    {
        StreamState s;
        lock (m_StreamsLock) { if (!m_Streams.TryGetValue(id, out s)) return; m_Streams.Remove(id); }
        s.Dispose(); Console.WriteLine($"stream {id}: closed");
        SendStatus(id, 0);
    }

    // ------------------------------------------------------------------ encode and send

    public void Run()
    {
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "XrssRead" };
        reader.Start();
        long minGap = Stopwatch.Frequency / Program.Fps, statStart = Stopwatch.GetTimestamp();
        try
        {
            while (!m_Gone)
            {
                StreamState[] list;
                lock (m_StreamsLock) { list = new StreamState[m_Streams.Count]; m_Streams.Values.CopyTo(list, 0); }
                bool worked = false;
                foreach (var s in list)
                {
                    if (!s.Ready)
                    {
                        if (s.TryOpenMap()) { SendStatus(s.Id, 1); Console.WriteLine($"stream {s.Id}: capturing"); }
                        else if (s.HelperDied)
                        {
                            if (s.TryRestartCapture()) continue;
                            Console.WriteLine($"stream {s.Id}: capture helper exited before the first frame"); CloseFailed(s);
                        }
                        else if ((Stopwatch.GetTimestamp() - s.OpenedAt) > 8 * Stopwatch.Frequency) { Console.WriteLine($"stream {s.Id}: no frames after 8 s"); CloseFailed(s); }
                        continue;
                    }
                    if (s.HelperDied)
                    {
                        if (s.TryRestartCapture()) continue;                                          // the helper crashed but the window is still open
                        Console.WriteLine($"stream {s.Id}: window closed"); CloseEnded(s); continue;
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (now - s.LastSend < Stopwatch.Frequency / Math.Max(1, s.FpsCap) || Volatile.Read(ref m_InFlight) >= Workers || Volatile.Read(ref s.InFlight) >= 2 || (s.Codec == 1 && s.PendingIds.Count >= 3)) continue;
                    if (!s.HasNewFrame(out uint counter, out int w, out int h, out int stride, out int slot, out int cap)) continue;
                    if (Dispatch(s, counter, w, h, stride, slot, cap, now)) worked = true;
                }
                PrintStats(list, ref statStart);
                if (m_Audio != null && Stopwatch.GetTimestamp() - m_AudioStatAt > 5 * Stopwatch.Frequency) { m_AudioStatAt = Stopwatch.GetTimestamp(); Console.WriteLine(m_Audio.TakeStats()); }
                if (!worked) Thread.Sleep(1);
            }
            while (Volatile.Read(ref m_InFlight) > 0) Thread.Sleep(5);
        }
        finally
        {
            SetAudio(false);
            StreamState[] all; lock (m_StreamsLock) { all = new StreamState[m_Streams.Count]; m_Streams.Values.CopyTo(all, 0); m_Streams.Clear(); }
            foreach (var s in all) s.Dispose();
        }
    }

    void CloseFailed(StreamState s) { lock (m_StreamsLock) m_Streams.Remove(s.Id); s.Dispose(); SendStatus(s.Id, 2); }
    void CloseEnded(StreamState s) { lock (m_StreamsLock) m_Streams.Remove(s.Id); s.Dispose(); SendStatus(s.Id, 0); }

    /// <summary>Copies the frame out of shared memory and queues its encoding. False when the copy was torn.</summary>
    bool Dispatch(StreamState s, uint counter, int w, int h, int stride, int slot, int cap, long now)
    {
        long t0 = Stopwatch.GetTimestamp();
        int need = stride * h;
        if (!m_Pool.TryTake(out var buf) || buf.Length < need) buf = new byte[need];
        fixed (byte* dst = buf) Buffer.MemoryCopy(s.Slot(slot, cap), dst, buf.Length, need);
        if (s.CounterNow - counter > 1) { m_Pool.Add(buf); return false; }          // torn: the writer lapped us while copying
        uint id = ++s.Seq; s.LastCounter = counter; s.LastSend = now;
        Interlocked.Increment(ref m_InFlight); Interlocked.Increment(ref s.InFlight);
        int cw = w, ch = h, cstride = stride;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                // SkiaSharp (libjpeg-turbo): scale into the output size, then encode 4:4:4 so coloured text edges stay clean.
                bool video = s.Codec == 1 && HevcPipe.FfmpegPath != null;
                int ow = video ? VideoTier(cw, s.MaxW) : Math.Min(cw, s.MaxW), oh = (int)((long)ch * ow / cw);
                if (video) { ow &= ~1; oh &= ~1; }
                if (ow < 16 || oh < 16) return;                                       // an odd-shaped frame: skip it (the encoder crashes the whole host on empty pictures)
                byte[] data = null;
                using (var dstBmp = new SKBitmap(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Opaque)))
                {
                    if (dstBmp.GetPixels() == IntPtr.Zero) return;                  // the output picture could not be allocated
                    bool scaled;
                    fixed (byte* sp = buf)
                    using (var srcPix = new SKPixmap(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque), (IntPtr)sp, cstride))
                        scaled = srcPix.ScalePixels(dstBmp.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    if (!scaled) return;
                    Interlocked.Add(ref s.ScaleTicks, Stopwatch.GetTimestamp() - t0);
                    if (video) { SubmitVideo(s, dstBmp, ow, oh, id); return; }
                    using var enc = dstBmp.PeekPixels().Encode(new SKJpegEncoderOptions(Program.Quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
                    if (enc == null) return;
                    data = enc.ToArray();
                }
                Interlocked.Add(ref s.EncTicks, Stopwatch.GetTimestamp() - t0);
                var header = new byte[24];
                BitConverter.GetBytes(Program.FrameMagic).CopyTo(header, 0); BitConverter.GetBytes((uint)data.Length).CopyTo(header, 4);
                BitConverter.GetBytes(ow).CopyTo(header, 8); BitConverter.GetBytes(oh).CopyTo(header, 12); BitConverter.GetBytes(id).CopyTo(header, 16);
                BitConverter.GetBytes(s.Id).CopyTo(header, 20);
                lock (s.WriteLock)
                {
                    if (id > s.LastWritten && !s.Ended)
                    {
                        Volatile.Write(ref s.SentAt[id & (s.SentAt.Length - 1)], Stopwatch.GetTimestamp());
                        Send(header, data);
                        s.LastWritten = id;
                        Interlocked.Increment(ref s.Frames); Interlocked.Add(ref s.Bytes, data.Length);
                        s.LastW = ow; s.LastH = oh;
                    }
                }
            }
            catch { m_Gone = true; }
            finally { m_Pool.Add(buf); Interlocked.Decrement(ref s.InFlight); Interlocked.Decrement(ref m_InFlight); }
        }, null);
        return true;
    }

    // ------------------------------------------------------------------ hardware video

    static readonly int[] VideoTiers = { 1280, 1920, 2400, 2880, 3440 };

    /// <summary>The picture width for a video stream: the largest fixed tier that fits what the headset asked for, so the encoder is restarted rarely.</summary>
    static int VideoTier(int sourceW, int maxW)
    {
        int target = Math.Min(sourceW, maxW), best = 0;
        foreach (int t in VideoTiers) if (t <= target) best = t;
        return best > 0 ? best : Math.Max(16, target & ~1);
    }

    void SubmitVideo(StreamState s, SKBitmap bmp, int ow, int oh, uint id)
    {
        lock (s.WriteLock)
        {
            if (id <= s.LastWritten || s.Ended) return;
            if (s.Pipe == null || s.Pipe.Dead || s.Pipe.Width != ow || s.Pipe.Height != oh) StartPipe(s, ow, oh);
            if (s.Pipe == null || bmp.RowBytes != ow * 4) return;
            s.PendingIds.Enqueue(id);
            Volatile.Write(ref s.SentAt[id & (s.SentAt.Length - 1)], Stopwatch.GetTimestamp());      // the round trip includes the encoder
            s.LastWritten = id;
            s.Pipe.Write(bmp.GetPixels(), ow * 4 * oh);
        }
    }

    void StartPipe(StreamState s, int ow, int oh)
    {
        var old = s.Pipe; s.Pipe = null;
        try { old?.Dispose(); } catch { }
        while (s.PendingIds.TryDequeue(out _)) { }
        double mbit = Math.Clamp(ow * (double)oh * Program.Fps * 0.19 / 1e6, 8, 90);
        HevcPipe pipe = null;
        try
        {
            pipe = new HevcPipe(ow, oh, Program.Fps, mbit, (data, key, dup) => OnVideoUnit(s, pipe, data, key, dup));
            s.Pipe = pipe;
            Console.WriteLine($"stream {s.Id}: hardware HEVC encoder started {ow}x{oh}, about {mbit:0} Mbit/s");
        }
        catch (Exception e) { Console.WriteLine($"stream {s.Id}: cannot start the HEVC encoder: {e.Message}"); s.Codec = 0; }
    }

    void OnVideoUnit(StreamState s, HevcPipe pipe, byte[] data, bool key, bool dup)
    {
        if (s.Pipe != pipe || s.Ended) return;
        uint id;
        if (dup) id = 0x80000000u | (uint)Interlocked.Increment(ref s.DupSeq);                       // a re-sent copy: no round-trip bookkeeping, the headset does not acknowledge it
        else
        {
            if (!s.PendingIds.TryDequeue(out id)) return;
            Interlocked.Add(ref s.EncTicks, Stopwatch.GetTimestamp() - Volatile.Read(ref s.SentAt[id & (s.SentAt.Length - 1)]));
        }
        var header = new byte[24];
        BitConverter.GetBytes(Program.VideoMagic).CopyTo(header, 0); BitConverter.GetBytes((uint)data.Length).CopyTo(header, 4);
        BitConverter.GetBytes(pipe.Width).CopyTo(header, 8); BitConverter.GetBytes(pipe.Height).CopyTo(header, 12); BitConverter.GetBytes(id).CopyTo(header, 16);
        BitConverter.GetBytes(s.Id).CopyTo(header, 20); header[22] = (byte)((key ? 1 : 0) | (dup ? 2 : 0));
        try { Send(header, data); } catch { m_Gone = true; return; }
        Interlocked.Increment(ref s.Frames); Interlocked.Add(ref s.Bytes, data.Length);
        s.LastW = pipe.Width; s.LastH = pipe.Height;
    }

    void PrintStats(StreamState[] list, ref long statStart)
    {
        long since = Stopwatch.GetTimestamp() - statStart;
        if (since < 3 * Stopwatch.Frequency) return;
        double sec = (double)since / Stopwatch.Frequency, ms = 1000.0 / Stopwatch.Frequency;
        foreach (var s in list)
        {
            long f = Interlocked.Exchange(ref s.Frames, 0), b = Interlocked.Exchange(ref s.Bytes, 0), et = Interlocked.Exchange(ref s.EncTicks, 0);
            long a = Interlocked.Exchange(ref s.Acks, 0), rt = Interlocked.Exchange(ref s.RttTicks, 0), rm = Interlocked.Exchange(ref s.RttMaxTicks, 0);
            if (f == 0 && a == 0) continue;
            Console.WriteLine($"stream {s.Id} {(s.Codec == 1 ? "HEVC" : "JPEG")}: sent {f / sec:0.0} fps, {b * 8 / sec / 1e6:0.0} Mbit/s, {b / Math.Max(1, f) / 1024} KB/frame, encode {et * ms / Math.Max(1, f):0} ms | shown {a / sec:0.0} fps, round trip avg {(a > 0 ? rt * ms / a : 0):0} ms max {rm * ms:0} ms | {s.LastW}x{s.LastH}");
        }
        statStart = Stopwatch.GetTimestamp();
    }
}
