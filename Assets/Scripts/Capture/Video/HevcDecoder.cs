#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Decodes one stream's HEVC with Android's MediaCodec on a worker thread (the Frame exposes only the software decoder, which measured 6 ms per 2880x1200 picture at 0.65 core).
    /// Pictures land as three 8-bit planes in native memory (triple-buffered, latest wins); the main thread uploads them and <see cref="ScreenSource"/> converts them on the GPU.
    /// The decoder is created from the first unit's size and recreated when the PC switches quality tier (a new size starts with a keyframe).
    /// </summary>
    sealed unsafe class HevcDecoder : IDisposable
    {
        sealed class Buf { public IntPtr y, u, v; public int w, h, lenY, lenC; }

        readonly ushort m_Stream;
        readonly Thread m_Thread;
        readonly ManualResetEventSlim m_Wake = new ManualResetEventSlim(false);
        volatile bool m_Quit;
        public volatile bool Failed;
        public string FailReason = "";

        readonly object m_Lock = new object();
        readonly Buf[] m_Bufs = new Buf[3];
        int m_Ready = -1, m_InUse = -1;
        uint m_ReadySeq; bool m_ReadyAck;
        public double DecodeMs;                                      // smoothed per-picture decode time
        public long Pictures;
        public int Queued => RemoteHost.UnitBacklog(m_Stream);
        public double LastAgeMs;                                      // arrival of a unit to its picture being ready

        public HevcDecoder(ushort stream)
        {
            m_Stream = stream;
            m_Thread = new Thread(Run) { IsBackground = true, Name = "HevcDecode-" + stream };
            m_Thread.Start();
        }

        public void Wake() => m_Wake.Set();

        // ------------------------------------------------------------------ main thread side

        /// <summary>Takes the newest finished picture for upload; <paramref name="done"/> must be called after the planes were copied to the textures.</summary>
        public bool TryAcquire(out IntPtr y, out IntPtr u, out IntPtr v, out int w, out int h, out int lenY, out int lenC, out uint seq, out bool ack)
        {
            lock (m_Lock)
            {
                if (m_Ready < 0) { y = u = v = IntPtr.Zero; w = h = lenY = lenC = 0; seq = 0; ack = false; return false; }
                m_InUse = m_Ready; m_Ready = -1;
                var b = m_Bufs[m_InUse];
                y = b.y; u = b.u; v = b.v; w = b.w; h = b.h; lenY = b.lenY; lenC = b.lenC; seq = m_ReadySeq; ack = m_ReadyAck;
                return true;
            }
        }

        public void Release() { lock (m_Lock) m_InUse = -1; }

        // ------------------------------------------------------------------ worker

        Buf TakeBack(int w, int h)
        {
            while (!m_Quit)
            {
                lock (m_Lock)
                {
                    bool sizeOk = m_Bufs[0] != null && m_Bufs[0].w == w && m_Bufs[0].h == h;
                    if (!sizeOk && m_InUse >= 0) { } // wait for the main thread to finish uploading before the planes are freed
                    else
                    {
                        if (!sizeOk)
                        {
                            for (int i = 0; i < 3; i++) FreeBuf(m_Bufs[i]);
                            m_Ready = -1;
                            for (int i = 0; i < 3; i++)
                                m_Bufs[i] = new Buf { w = w, h = h, lenY = w * h, lenC = (w / 2) * (h / 2), y = Marshal.AllocHGlobal(w * h), u = Marshal.AllocHGlobal((w / 2) * (h / 2)), v = Marshal.AllocHGlobal((w / 2) * (h / 2)) };
                        }
                        for (int i = 0; i < 3; i++) if (i != m_Ready && i != m_InUse) { m_Back = i; return m_Bufs[i]; }
                    }
                }
                Thread.Sleep(1);
            }
            return null;
        }
        int m_Back;

        static void FreeBuf(Buf b) { if (b == null) return; Marshal.FreeHGlobal(b.y); Marshal.FreeHGlobal(b.u); Marshal.FreeHGlobal(b.v); }

        void Run()
        {
            AndroidJNI.AttachCurrentThread();
            AndroidJavaObject codec = null, info = null;
            int cw = 0, ch = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                info = new AndroidJavaObject("android.media.MediaCodec$BufferInfo");
                RemoteHost.VideoUnit pending = default; bool havePending = false;
                var meta = new System.Collections.Generic.Queue<(uint seq, bool dup, long arrived)>();
                while (!m_Quit)
                {
                    bool worked = false;
                    // late by more than ~8 pictures: jump to the newest keyframe rather than staying behind
                    if (RemoteHost.UnitBacklog(m_Stream) > 8 && RemoteHost.SkipToLatestKey(m_Stream)) { havePending = false; meta.Clear(); try { codec?.Call("flush"); } catch { } }

                    if (!havePending && RemoteHost.TakeUnit(m_Stream, out pending)) havePending = true;
                    if (havePending)
                    {
                        if (codec == null || pending.w != cw || pending.h != ch)
                        {
                            if (!pending.key) { havePending = false; continue; }                  // wait for a keyframe at the new size
                            if (codec != null) { try { codec.Call("stop"); codec.Call("release"); } catch { } codec.Dispose(); codec = null; meta.Clear(); }
                            codec = Create(pending.w, pending.h); cw = pending.w; ch = pending.h;
                        }
                        int ii = codec.Call<int>("dequeueInputBuffer", 0L);
                        if (ii >= 0)
                        {
                            using (var bb = codec.Call<AndroidJavaObject>("getInputBuffer", ii))
                            {
                                IntPtr addr = (IntPtr)AndroidJNI.GetDirectBufferAddress(bb.GetRawObject());
                                Marshal.Copy(pending.data, 0, addr, pending.data.Length);
                                codec.Call("queueInputBuffer", ii, 0, pending.data.Length, 0L, pending.key ? 1 : 0);
                            }
                            meta.Enqueue((pending.seq, pending.dup, pending.arrived));
                            havePending = false; worked = true;
                        }
                    }
                    if (codec != null)
                    {
                        int oi = codec.Call<int>("dequeueOutputBuffer", info, 0L);
                        if (oi >= 0)
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            if (meta.Count > 0)
                            {
                                var m = meta.Dequeue();
                                if (CopyOut(codec, oi, cw, ch, m.seq, m.dup))
                                {
                                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                                    DecodeMs = DecodeMs * 0.9 + ms * 0.1; Pictures++;
                                    LastAgeMs = (Stopwatch.GetTimestamp() - m.arrived) * 1000.0 / Stopwatch.Frequency;
                                }
                            }
                            codec.Call("releaseOutputBuffer", oi, false);
                            worked = true;
                        }
                        else if (oi == -2 || oi == -3) worked = true;                         // format / buffers changed: nothing to do, planes are read per picture
                    }
                    if (!worked) { m_Wake.Wait(2); m_Wake.Reset(); }
                }
            }
            catch (Exception e)
            {
                FailReason = e.GetType().Name + ": " + e.Message; Failed = true;
                Debug.LogWarning("[XrSpatial] HEVC decoder failed: " + FailReason);
            }
            finally
            {
                try { if (codec != null) { codec.Call("stop"); codec.Call("release"); codec.Dispose(); } } catch { }
                info?.Dispose();
                AndroidJNI.DetachCurrentThread();
            }
        }

        AndroidJavaObject Create(int w, int h)
        {
            using (var fmtClass = new AndroidJavaClass("android.media.MediaFormat"))
            using (var codecClass = new AndroidJavaClass("android.media.MediaCodec"))
            using (var fmt = fmtClass.CallStatic<AndroidJavaObject>("createVideoFormat", "video/hevc", w, h))
            {
                var codec = codecClass.CallStatic<AndroidJavaObject>("createDecoderByType", "video/hevc");
                codec.Call("configure", fmt, null, null, 0);
                codec.Call("start");
                Debug.Log($"[XrSpatial] HEVC decoder {codec.Call<string>("getName")} started for stream {m_Stream} at {w}x{h}");
                return codec;
            }
        }

        bool CopyOut(AndroidJavaObject codec, int index, int w, int h, uint seq, bool dup)
        {
            using (var image = codec.Call<AndroidJavaObject>("getOutputImage", index))
            {
                if (image == null) return false;
                int iw = image.Call<int>("getWidth"), ih = image.Call<int>("getHeight");
                if (iw != w || ih != h) return false;
                var back = TakeBack(w, h);
                if (back == null) return false;
                var planes = image.Call<AndroidJavaObject[]>("getPlanes");
                for (int p = 0; p < 3; p++)
                {
                    using (var plane = planes[p])
                    using (var buf = plane.Call<AndroidJavaObject>("getBuffer"))
                    {
                        int rowStride = plane.Call<int>("getRowStride"), pixStride = plane.Call<int>("getPixelStride");
                        byte* src = (byte*)AndroidJNI.GetDirectBufferAddress(buf.GetRawObject());
                        byte* dst = (byte*)(p == 0 ? back.y : p == 1 ? back.u : back.v);
                        int pw = p == 0 ? w : w / 2, ph = p == 0 ? h : h / 2;
                        if (pixStride == 1)
                        {
                            if (rowStride == pw) Buffer.MemoryCopy(src, dst, (long)pw * ph, (long)pw * ph);
                            else for (int r = 0; r < ph; r++) Buffer.MemoryCopy(src + (long)r * rowStride, dst + (long)r * pw, pw, pw);
                        }
                        else for (int r = 0; r < ph; r++) { byte* s = src + (long)r * rowStride; byte* d = dst + (long)r * pw; for (int c = 0; c < pw; c++) d[c] = s[c * pixStride]; }
                    }
                }
                lock (m_Lock) { m_Ready = m_Back; m_ReadySeq = seq; m_ReadyAck = !dup; }
                return true;
            }
        }

        public void Dispose()
        {
            m_Quit = true; m_Wake.Set();
            var t = m_Thread;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                t.Join(2000);
                lock (m_Lock) { for (int i = 0; i < 3; i++) { FreeBuf(m_Bufs[i]); m_Bufs[i] = null; } }
            }, null);
        }
    }
}
#endif
