using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// Hardware HEVC encoding of one stream: BGRA pictures go into an ffmpeg process (AMD AMF hardware encoder, ultra-low-latency, no B-frames), and its output comes
/// back over RTP on the loopback so every picture's last packet is marked: no frame-boundary guessing and no added delay. The pictures are reassembled into
/// Annex-B access units (VPS/SPS/PPS come in-band with each keyframe) and handed to <c>onUnit(data, isKey)</c>, one call per submitted picture, in order.
/// </summary>
sealed class HevcPipe : IDisposable
{
    public readonly int Width, Height;
    readonly Process m_Proc;
    readonly Stream m_In;
    readonly UdpClient m_Udp;
    readonly Thread m_Reader;
    readonly Action<byte[], bool, bool> m_OnUnit;
    readonly object m_WriteLock = new object();
    readonly System.Collections.Concurrent.ConcurrentQueue<bool> m_Slots = new System.Collections.Concurrent.ConcurrentQueue<bool>();      // true = a re-sent copy of the last picture
    byte[] m_Last; int m_LastLen, m_Kicks; long m_LastWriteMs; double m_GapMs = 33;                  // m_GapMs: smoothed time between pictures
    static readonly int KickMs = int.TryParse(Environment.GetEnvironmentVariable("XRSS_KICKMS"), out var km) ? km : 30, KickCount = int.TryParse(Environment.GetEnvironmentVariable("XRSS_KICKS"), out var kc) ? kc : 2;
    volatile bool m_Dead;
    public bool Dead => m_Dead || m_Proc.HasExited;
    public static string FfmpegPath;

    /// <summary>Finds ffmpeg: --ffmpeg, then the XRSS_FFMPEG variable, then PATH.</summary>
    public static string Find(string given)
    {
        if (!string.IsNullOrEmpty(given) && File.Exists(given)) return Path.GetFullPath(given);
        var env = Environment.GetEnvironmentVariable("XRSS_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public HevcPipe(int w, int h, int fps, double mbit, Action<byte[], bool, bool> onUnit)
    {
        Width = w; Height = h; m_OnUnit = onUnit;
        m_Udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        m_Udp.Client.ReceiveBufferSize = 64 << 20;
        int port = ((IPEndPoint)m_Udp.Client.LocalEndPoint).Port;
        string br = mbit.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string peak = (mbit * 2).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        // BGRA in, BT.709 limited-range NV12 to the encoder; frames are encoded as they arrive (passthrough), a keyframe every 2 s.
        string args = $"-hide_banner -loglevel error -nostdin -fflags nobuffer -flags low_delay -threads 1 -f rawvideo -pix_fmt bgra -video_size {w}x{h} -framerate {fps} -i pipe:0 " +
                      "-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                      $"-c:v hevc_amf -usage ultralowlatency -latency true -async_depth 2 -preanalysis false -preencode false -quality quality -rc vbr_peak -b:v {br}M -maxrate {peak}M -g {fps * 2} -bf 0 -header_insertion_mode idr -fps_mode passthrough " +
                      $"-flush_packets 1 -f rtp -payload_type 96 \"rtp://127.0.0.1:{port}?pkt_size=60000\"";
        var over = Environment.GetEnvironmentVariable("XRSS_FFENC");                    // test hook: replaces the encoder options
        if (!string.IsNullOrEmpty(over)) args = args.Substring(0, args.IndexOf("-c:v hevc_amf")) + over + args.Substring(args.IndexOf(" -f rtp") );
        m_Proc = Process.Start(new ProcessStartInfo(FfmpegPath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true });
        m_In = m_Proc.StandardInput.BaseStream;
        var proc = m_Proc;
        new Thread(() => { try { string l; while ((l = proc.StandardError.ReadLine()) != null) Console.WriteLine("ffmpeg: " + l); } catch { } }) { IsBackground = true }.Start();
        m_Reader = new Thread(ReadRtp) { IsBackground = true, Name = "HevcRtp" };
        m_Reader.Start();
        new Thread(Kicker) { IsBackground = true, Name = "HevcKick" }.Start();
    }

    /// <summary>The encoder (ffmpeg's AMF wrapper) keeps the newest pictures inside until more input arrives, so a window that stops changing would never show its last frame.
    /// When nothing new has arrived for a moment, the last picture is submitted again a few times: each copy costs a few bytes as a skip frame and pushes the real one out.</summary>
    void Kicker()
    {
        while (!m_Dead)
        {
            Thread.Sleep(3);
            lock (m_WriteLock)
            {
                // the first nudge waits 1.5 pictures' worth of quiet (never fires while a stream is moving steadily), the next one follows quickly
                long wait = m_Kicks == 0 ? (long)Math.Clamp(m_GapMs * 1.5, 6, KickMs) : 6;
                if (m_Dead || m_LastLen == 0 || m_Kicks >= KickCount || Environment.TickCount64 - m_LastWriteMs < wait) continue;
                m_Kicks++; m_LastWriteMs = Environment.TickCount64;
                m_Slots.Enqueue(true);
                try { m_In.Write(m_Last, 0, m_LastLen); m_In.Flush(); } catch { m_Dead = true; }
            }
        }
    }

    /// <summary>Writes one picture (BGRA, tightly packed rows). Blocks only if the encoder is behind.</summary>
    public unsafe void Write(IntPtr pixels, int bytes)
    {
        lock (m_WriteLock)
        {
            try
            {
                var span = new ReadOnlySpan<byte>((void*)pixels, bytes);
                if (m_Last == null || m_Last.Length != bytes) m_Last = new byte[bytes];
                span.CopyTo(m_Last); m_LastLen = bytes; m_Kicks = 0;
                long nowMs = Environment.TickCount64; if (m_LastWriteMs > 0) m_GapMs = m_GapMs * 0.8 + Math.Min(500, nowMs - m_LastWriteMs) * 0.2; m_LastWriteMs = nowMs;
                m_Slots.Enqueue(false);
                m_In.Write(m_Last, 0, bytes); m_In.Flush();
            }
            catch { m_Dead = true; }
        }
    }

    void ReadRtp()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        var au = new MemoryStream(1 << 20);
        bool key = false, broken = false; int lastSeq = -1;
        try
        {
            while (!m_Dead)
            {
                byte[] p;
                try { p = m_Udp.Receive(ref ep); } catch { break; }
                if (p.Length < 14 || (p[1] & 0x7f) != 96) continue;                     // RTP payload type 96 only: ffmpeg also sends RTCP reports to the next port, which can be another stream's receiver
                int seq = (p[2] << 8) | p[3];
                if (lastSeq >= 0 && seq != ((lastSeq + 1) & 0xffff)) { if (!broken) Console.WriteLine($"video: RTP sequence jump {lastSeq} -> {seq} (datagram {p.Length} bytes, {au.Length} bytes of this picture so far)"); broken = true; }           // a datagram was lost: this picture (and the chain after it) is damaged
                lastSeq = seq;
                bool marker = (p[1] & 0x80) != 0;
                int off = 12 + 4 * (p[0] & 0x0f);
                if ((p[0] & 0x10) != 0 && p.Length >= off + 4) off += 4 + 4 * ((p[off + 2] << 8) | p[off + 3]);
                int type = (p[off] >> 1) & 0x3f;
                if (type == 48)                                                                // aggregation packet: several NAL units
                {
                    int i = off + 2;
                    while (i + 2 <= p.Length) { int n = (p[i] << 8) | p[i + 1]; i += 2; if (n <= 0 || i + n > p.Length) break; AddNal(au, p, i, n, ref key); i += n; }
                }
                else if (type == 49)                                                           // fragmentation unit
                {
                    byte fu = p[off + 2]; int nalType = fu & 0x3f;
                    if ((fu & 0x80) != 0)
                    {
                        au.WriteByte(0); au.WriteByte(0); au.WriteByte(0); au.WriteByte(1);
                        au.WriteByte((byte)((p[off] & 0x81) | (nalType << 1))); au.WriteByte(p[off + 1]);
                        if (nalType >= 16 && nalType <= 21) key = true;
                    }
                    au.Write(p, off + 3, p.Length - off - 3);
                }
                else AddNal(au, p, off, p.Length - off, ref key);
                if (marker)
                {
                    if (!broken && au.Length > 0) { m_Slots.TryDequeue(out bool dup); m_OnUnit(au.ToArray(), key, dup); }
                    else if (broken) { Console.WriteLine("video: a packet was lost, restarting the encoder"); m_Dead = true; }
                    au.SetLength(0); key = false;
                }
            }
        }
        catch (Exception e) { Console.WriteLine("video reader ended: " + e.Message); m_Dead = true; }
    }

    static void AddNal(MemoryStream au, byte[] p, int off, int len, ref bool key)
    {
        int t = (p[off] >> 1) & 0x3f;
        if (t >= 16 && t <= 21) key = true;
        au.WriteByte(0); au.WriteByte(0); au.WriteByte(0); au.WriteByte(1);
        au.Write(p, off, len);
    }

    public void Dispose()
    {
        m_Dead = true;
        try { m_In.Close(); } catch { }
        try { if (!m_Proc.WaitForExit(800)) m_Proc.Kill(); } catch { }
        try { m_Udp.Close(); } catch { }
    }
}
