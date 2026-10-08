using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Capture backend for the headset build: the pixels of one PC window come from the PC host over the network (<see cref="RemoteHost"/>) instead of a local helper process.
    /// On the headset the JPEG is decoded by Android on a worker thread and the main thread only copies the finished pixels into the texture (decoding on the main thread
    /// took ~80% of it with three large windows). Elsewhere (editor) it falls back to Texture2D.LoadImage. Each shown frame is acknowledged so the host can measure the round trip.
    /// Disposing closes the stream.
    /// </summary>
    public sealed class NetworkBackend : ICaptureBackend
    {
        static double s_UploadMsSum; static int s_UploadCount; static float s_LogAt;
        public readonly ushort StreamId;
        public readonly long Hwnd;
        int m_W, m_H;
        float m_Fps; int m_Count; float m_FpsStart;
        public double LastDecodeMs { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        readonly AndroidJpegDecoder m_Decoder = new AndroidJpegDecoder();
        volatile bool m_Busy, m_Ready, m_Failed;       // m_Busy: a decode is running or its pixels are waiting for upload
        uint m_ReadySeq;
        long m_DecodeTicks;
        /// <summary>False when the decoded picture is top-row-first (what the panel shader expects); true when it came through Texture2D.LoadImage (bottom row first).</summary>
        public bool NeedsFlip => m_Failed;
#else
        public bool NeedsFlip => true;
#endif

        public NetworkBackend(long hwnd) { Hwnd = hwnd; StreamId = RemoteHost.OpenStream(hwnd); }

        public string Status => RemoteHost.Connected ? $"{RemoteHost.GetState(StreamId)}" + (m_W > 0 ? $" ({m_W}x{m_H}, {m_Fps:0} fps)" : "") : RemoteHost.State;
        public bool IsRunning => RemoteHost.Active && RemoteHost.GetState(StreamId) != StreamState.Ended && RemoteHost.GetState(StreamId) != StreamState.Failed;
        /// <summary>True when the PC closed the window or could not capture it; the source then looks for the window again.</summary>
        public bool HasEnded => !RemoteHost.Active || RemoteHost.GetState(StreamId) == StreamState.Ended || RemoteHost.GetState(StreamId) == StreamState.Failed;
        public int Width => m_W;
        public int Height => m_H;
        public float CaptureFps => m_Fps;
        /// <summary>On the headset only the size matters (hwnd 0: the PC host does the input injection and its own checks).</summary>
        public WindowGeometry Window => new WindowGeometry { x = 0, y = 0, width = m_W, height = m_H, hwnd = 0, foreground = true, alive = !HasEnded && m_W > 0 };

#if UNITY_ANDROID && !UNITY_EDITOR
        public bool PollInto(ref Texture2D target)
        {
            if (m_Failed) return PollWithLoadImage(ref target);
            bool shown = false;
            if (m_Ready)
            {
                // the worker has finished: copy its pixels into the texture (the only main-thread work)
                var sw = Stopwatch.StartNew();
                int w = m_Decoder.Width, h = m_Decoder.Height;
                if (!target || target.width != w || target.height != h)
                {
                    if (target) Object.Destroy(target);
                    target = new Texture2D(w, h, TextureFormat.RGBA32, false);
                }
                target.LoadRawTextureData(m_Decoder.Pixels, m_Decoder.Bytes);
                target.Apply(false);
                m_W = w; m_H = h;
                RemoteHost.Ack(StreamId, m_ReadySeq);
                m_Ready = false; m_Busy = false;
                LastDecodeMs = m_DecodeTicks * 1000.0 / Stopwatch.Frequency;
                Account(sw.Elapsed.TotalMilliseconds);
                shown = true;
            }
            if (!m_Busy && RemoteHost.TakeFrame(StreamId, out var jpeg, out int fw, out int fh, out uint seq))
            {
                m_Busy = true;
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        if (m_Decoder.Decode(jpeg, jpeg.Length, fw, fh)) { m_DecodeTicks = Stopwatch.GetTimestamp() - t0; m_ReadySeq = seq; m_Ready = true; }
                        else m_Busy = false;                                      // undecodable frame: wait for the next one
                    }
                    catch (System.Exception e)
                    {
                        UnityEngine.Debug.LogWarning("[XrSpatial] Android JPEG decode failed, falling back to the main-thread decoder: " + e.Message);
                        m_Failed = true; m_Busy = false;
                        RemoteHost.Requeue(StreamId, jpeg, fw, fh, seq);
                    }
                }, null);
            }
            if (shown) CountFrame();
            return shown;
        }
#else
        public bool PollInto(ref Texture2D target) => PollWithLoadImage(ref target);
#endif

        bool PollWithLoadImage(ref Texture2D target)
        {
            if (!RemoteHost.TakeFrame(StreamId, out var jpeg, out int w, out int h, out uint seq)) return false;
            if (!target) target = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var sw = Stopwatch.StartNew();
            bool ok = target.LoadImage(jpeg, false);
            LastDecodeMs = sw.Elapsed.TotalMilliseconds;
            Account(LastDecodeMs);
            if (!ok) return false;
            m_W = target.width; m_H = target.height;
            RemoteHost.Ack(StreamId, seq);
            CountFrame();
            return true;
        }

        void CountFrame()
        {
            m_Count++;
            if (Time.unscaledTime - m_FpsStart >= 2f) { m_Fps = m_Count / (Time.unscaledTime - m_FpsStart); m_Count = 0; m_FpsStart = Time.unscaledTime; }
        }

        static void Account(double ms)
        {
            s_UploadMsSum += ms; s_UploadCount++;
            if (Time.unscaledTime - s_LogAt > 5f)
            {
                if (s_UploadCount > 0) UnityEngine.Debug.Log($"[XrSpatial] video on the main thread: {s_UploadCount / (Time.unscaledTime - s_LogAt):0.0} frames/s, {s_UploadMsSum / s_UploadCount:0.0} ms each, {s_UploadMsSum / (Time.unscaledTime - s_LogAt):0} ms per second");
                s_LogAt = Time.unscaledTime; s_UploadMsSum = 0; s_UploadCount = 0;
            }
        }

        public void Dispose()
        {
            RemoteHost.CloseStream(StreamId);
#if UNITY_ANDROID && !UNITY_EDITOR
            // a decode may still be running on a worker: let it finish before its buffers go away
            ThreadPool.UnsafeQueueUserWorkItem(_ => { for (int i = 0; i < 200 && m_Busy && !m_Ready; i++) Thread.Sleep(10); m_Decoder.Dispose(); }, null);
#endif
        }
    }
}
