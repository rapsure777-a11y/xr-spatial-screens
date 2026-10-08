using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Connection from the headset app to the PC host (Tools/XrssStream). The host sends the newest JPEG frame of the captured window; the headset sends back an
    /// acknowledgement per shown frame and pointer events. Wire format: see Tools/XrssStream/Program.cs. For now one streamed window; the host address is
    /// 127.0.0.1:5600, reached through `adb reverse`. Active only when <see cref="Enable"/> was called (the headset build does that).
    /// </summary>
    public static class RemoteHost
    {
        public const string Address = "127.0.0.1";
        public const int Port = 5600;
        const uint FrameMagic = 0x46535258;                         // 'XRSF'

        public static bool Active { get; private set; }
        public static string State { get; private set; } = "off";
        public static int FrameWidth { get; private set; }
        public static int FrameHeight { get; private set; }
        public static long FramesReceived { get; private set; }

        static Thread s_Thread;
        static volatile bool s_Quit;
        static readonly object s_Lock = new object();
        static byte[] s_Latest; static int s_LatestW, s_LatestH; static uint s_LatestSeq;
        static NetworkStream s_Net;
        static readonly object s_WriteLock = new object();
        public static string LastInputSent { get; private set; } = "none";
        static int s_SentInputs;

        public static void Enable()
        {
            if (Active) return;
            Active = true; s_Quit = false; State = "connecting";
            s_Thread = new Thread(Run) { IsBackground = true, Name = "RemoteHost" };
            s_Thread.Start();
        }

        public static void Disable() { Active = false; s_Quit = true; try { s_Net?.Close(); } catch { } State = "off"; }

        static void Run()
        {
            var hdr = new byte[20];
            while (!s_Quit)
            {
                try
                {
                    using var c = new TcpClient { NoDelay = true };
                    c.Connect(Address, Port);
                    var net = c.GetStream(); s_Net = net; State = "connected";
                    Debug.Log("[XrSpatial] remote: connected to the PC host");
                    while (!s_Quit)
                    {
                        Read(net, hdr, 20);
                        if (BitConverter.ToUInt32(hdr, 0) != FrameMagic) throw new Exception("bad frame magic");
                        int len = (int)BitConverter.ToUInt32(hdr, 4), w = BitConverter.ToInt32(hdr, 8), h = BitConverter.ToInt32(hdr, 12);
                        uint seq = BitConverter.ToUInt32(hdr, 16);
                        var data = new byte[len];
                        Read(net, data, len);
                        lock (s_Lock) { s_Latest = data; s_LatestW = w; s_LatestH = h; s_LatestSeq = seq; }
                        FramesReceived++;
                    }
                }
                catch (Exception e) { if (!s_Quit) { State = "waiting for the PC host (" + e.GetType().Name + ")"; Thread.Sleep(1000); } }
                s_Net = null;
            }
        }

        static void Read(NetworkStream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) throw new System.IO.EndOfStreamException(); got += r; }
        }

        /// <summary>The newest undelivered frame (its JPEG bytes, size and sequence number), or false when there is none.</summary>
        public static bool TakeFrame(out byte[] jpeg, out int w, out int h, out uint seq)
        {
            lock (s_Lock) { jpeg = s_Latest; w = s_LatestW; h = s_LatestH; seq = s_LatestSeq; s_Latest = null; }
            if (jpeg == null) return false;
            FrameWidth = w; FrameHeight = h;
            return true;
        }

        static void Write(byte[] msg)
        {
            var net = s_Net;
            if (net == null) return;
            try { lock (s_WriteLock) net.Write(msg, 0, msg.Length); } catch { }
        }

        public static void Ack(uint seq) { var m = new byte[5]; m[0] = (byte)'A'; BitConverter.GetBytes(seq).CopyTo(m, 1); Write(m); }

        /// <summary>Pointer event: kind 0 move, 1 left down, 2 left up, 3 right click, 4 wheel; (u, v) on the source image, origin top-left.</summary>
        public static void SendPointer(byte kind, float u, float v, float wheel)
        {
            var m = new byte[14]; m[0] = (byte)'P'; m[1] = kind;
            BitConverter.GetBytes(u).CopyTo(m, 2); BitConverter.GetBytes(v).CopyTo(m, 6); BitConverter.GetBytes(wheel).CopyTo(m, 10);
            Write(m);
            if (kind != 0) { s_SentInputs++; LastInputSent = $"#{s_SentInputs} kind {kind} at {u:0.000},{v:0.000}"; }
        }

        public static void SendLost() => Write(new[] { (byte)'L' });
    }
}
