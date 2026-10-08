using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Capture
{
    public enum StreamState { Opening, Live, Ended, Failed }

    /// <summary>
    /// Connection from the headset app to the PC host (Tools/XrssStream). One TCP connection carries the PC's window list, any number of window streams (JPEG frames),
    /// and the pointer events going back. Wire format: see Tools/XrssStream/Program.cs. The host address is 127.0.0.1:5600, reached through `adb reverse`.
    /// Streams the app wants are remembered, so they are reopened after the connection comes back. Active only after <see cref="Enable"/> (the headset build does that).
    /// </summary>
    public static class RemoteHost
    {
        public const string Address = "127.0.0.1";
        public const int Port = 5600;
        const uint FrameMagic = 0x32535258, ListMagic = 0x4C535258, StatusMagic = 0x53535258;       // 'XRS2', 'XRSL', 'XRSS'

        sealed class Stream
        {
            public long hwnd; public StreamState state = StreamState.Opening;
            public byte[] latest; public int w, h; public uint seq; public long received;
        }

        public static bool Active { get; private set; }
        public static bool Connected { get; private set; }
        public static string State { get; private set; } = "off";
        public static long FramesReceived { get; private set; }
        public static string LastInputSent { get; private set; } = "none";

        static Thread s_Thread, s_Timer;
        static volatile bool s_Quit;
        static readonly object s_Lock = new object(), s_WriteLock = new object();
        static readonly Dictionary<ushort, Stream> s_Streams = new Dictionary<ushort, Stream>();
        static ushort s_NextId = 1;
        static NetworkStream s_Net;
        static CapturableWindow[] s_Windows = new CapturableWindow[0];
        static readonly List<TaskCompletionSource<List<CapturableWindow>>> s_ListWaiters = new List<TaskCompletionSource<List<CapturableWindow>>>();
        static int s_SentInputs;

        public static void Enable()
        {
            if (Active) return;
            Active = true; s_Quit = false; State = "connecting";
            s_Thread = new Thread(Run) { IsBackground = true, Name = "RemoteHost" }; s_Thread.Start();
            s_Timer = new Thread(() => { while (!s_Quit) { Thread.Sleep(4000); if (Connected) RequestList(); } }) { IsBackground = true, Name = "RemoteHostList" }; s_Timer.Start();
        }

        public static void Disable() { Active = false; s_Quit = true; try { s_Net?.Close(); } catch { } State = "off"; }

        // ------------------------------------------------------------------ connection

        static void Run()
        {
            var hdr = new byte[24];
            while (!s_Quit)
            {
                try
                {
                    using var c = new TcpClient { NoDelay = true };
                    c.Connect(Address, Port);
                    var net = c.GetStream(); s_Net = net; Connected = true; State = "connected";
                    Debug.Log("[XrSpatial] remote: connected to the PC host");
                    RequestList();
                    lock (s_Lock) foreach (var kv in s_Streams) { kv.Value.state = StreamState.Opening; SendOpen(kv.Key, kv.Value.hwnd); }     // streams wanted before the (re)connect
                    while (!s_Quit)
                    {
                        Read(net, hdr, 4);
                        uint magic = BitConverter.ToUInt32(hdr, 0);
                        if (magic == FrameMagic)
                        {
                            Read(net, hdr, 20);
                            int len = (int)BitConverter.ToUInt32(hdr, 0), w = BitConverter.ToInt32(hdr, 4), h = BitConverter.ToInt32(hdr, 8);
                            uint seq = BitConverter.ToUInt32(hdr, 12); ushort id = BitConverter.ToUInt16(hdr, 16);
                            var data = new byte[len]; Read(net, data, len);
                            lock (s_Lock)
                            {
                                if (s_Streams.TryGetValue(id, out var s)) { s.latest = data; s.w = w; s.h = h; s.seq = seq; s.received++; s.state = StreamState.Live; }
                            }
                            FramesReceived++;
                        }
                        else if (magic == ListMagic)
                        {
                            Read(net, hdr, 4); int len = (int)BitConverter.ToUInt32(hdr, 0);
                            var body = new byte[len]; Read(net, body, len);
                            var list = CaptureCatalog.Parse(Encoding.UTF8.GetString(body));
                            List<TaskCompletionSource<List<CapturableWindow>>> waiters;
                            lock (s_Lock) { s_Windows = list; waiters = new List<TaskCompletionSource<List<CapturableWindow>>>(s_ListWaiters); s_ListWaiters.Clear(); }
                            foreach (var t in waiters) t.TrySetResult(new List<CapturableWindow>(list));
                        }
                        else if (magic == StatusMagic)
                        {
                            Read(net, hdr, 8);
                            ushort id = BitConverter.ToUInt16(hdr, 0); byte st = hdr[2];
                            lock (s_Lock) { if (s_Streams.TryGetValue(id, out var s)) s.state = st == 1 ? StreamState.Live : st == 2 ? StreamState.Failed : StreamState.Ended; }
                        }
                        else throw new Exception("bad message magic");
                    }
                }
                catch (Exception e) { if (!s_Quit) { State = "waiting for the PC host (" + e.GetType().Name + ")"; } }
                Connected = false; s_Net = null;
                lock (s_Lock) foreach (var s in s_Streams.Values) if (s.state == StreamState.Live) s.state = StreamState.Opening;
                if (!s_Quit) Thread.Sleep(1000);
            }
        }

        static void Read(NetworkStream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) throw new System.IO.EndOfStreamException(); got += r; }
        }

        static void Write(byte[] msg)
        {
            var net = s_Net;
            if (net == null) return;
            try { lock (s_WriteLock) net.Write(msg, 0, msg.Length); } catch { }
        }

        // ------------------------------------------------------------------ windows

        public static CapturableWindow[] Windows { get { lock (s_Lock) return s_Windows; } }

        public static void RequestList() => Write(new[] { (byte)'W' });

        /// <summary>Asks the PC for its window list and completes when it arrives (or with the last known list after a few seconds).</summary>
        public static Task<List<CapturableWindow>> ListAsync()
        {
            var tcs = new TaskCompletionSource<List<CapturableWindow>>();
            lock (s_Lock) s_ListWaiters.Add(tcs);
            RequestList();
            Task.Delay(3000).ContinueWith(_ => { lock (s_Lock) s_ListWaiters.Remove(tcs); tcs.TrySetResult(new List<CapturableWindow>(Windows)); });
            return tcs.Task;
        }

        /// <summary>The PC window that best matches a saved source (same process, title containing the saved fragment; the largest wins), or null while none is open.</summary>
        public static CapturableWindow Resolve(string process, string titleContains)
        {
            CapturableWindow best = null;
            foreach (var w in Windows)
            {
                if (!string.IsNullOrEmpty(process) && !string.Equals(w.process, process, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(titleContains) && (w.title == null || w.title.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (best == null || (long)w.w * w.h > (long)best.w * best.h) best = w;
            }
            return best;
        }

        // ------------------------------------------------------------------ streams

        static void SendOpen(ushort id, long hwnd)
        {
            var m = new byte[11]; m[0] = (byte)'O'; BitConverter.GetBytes(id).CopyTo(m, 1); BitConverter.GetBytes(hwnd).CopyTo(m, 3);
            Write(m);
        }

        public static ushort OpenStream(long hwnd)
        {
            ushort id;
            lock (s_Lock) { id = s_NextId++; s_Streams[id] = new Stream { hwnd = hwnd }; }
            SendOpen(id, hwnd);
            return id;
        }

        public static void CloseStream(ushort id)
        {
            lock (s_Lock) s_Streams.Remove(id);
            var m = new byte[3]; m[0] = (byte)'X'; BitConverter.GetBytes(id).CopyTo(m, 1);
            Write(m);
        }

        public static StreamState GetState(ushort id) { lock (s_Lock) return s_Streams.TryGetValue(id, out var s) ? s.state : StreamState.Ended; }

        /// <summary>The newest undelivered frame of a stream (JPEG bytes, size and sequence number), or false when there is none.</summary>
        public static bool TakeFrame(ushort id, out byte[] jpeg, out int w, out int h, out uint seq)
        {
            lock (s_Lock)
            {
                if (s_Streams.TryGetValue(id, out var s)) { jpeg = s.latest; w = s.w; h = s.h; seq = s.seq; s.latest = null; }
                else { jpeg = null; w = h = 0; seq = 0; }
            }
            return jpeg != null;
        }

        public static void Ack(ushort id, uint seq)
        {
            var m = new byte[7]; m[0] = (byte)'A'; BitConverter.GetBytes(id).CopyTo(m, 1); BitConverter.GetBytes(seq).CopyTo(m, 3);
            Write(m);
        }

        /// <summary>Pointer event on a stream's window: kind 0 move, 1 left down, 2 left up, 3 right click, 4 wheel; (u, v) on the source image, origin top-left.</summary>
        public static void SendPointer(ushort id, byte kind, float u, float v, float wheel)
        {
            var m = new byte[16]; m[0] = (byte)'P'; BitConverter.GetBytes(id).CopyTo(m, 1); m[3] = kind;
            BitConverter.GetBytes(u).CopyTo(m, 4); BitConverter.GetBytes(v).CopyTo(m, 8); BitConverter.GetBytes(wheel).CopyTo(m, 12);
            Write(m);
            if (kind != 0) { s_SentInputs++; LastInputSent = $"#{s_SentInputs} stream {id} kind {kind} at {u:0.000},{v:0.000}"; }
        }

        public static void SendLost(ushort id)
        {
            var m = new byte[3]; m[0] = (byte)'L'; BitConverter.GetBytes(id).CopyTo(m, 1);
            Write(m);
        }
    }
}
