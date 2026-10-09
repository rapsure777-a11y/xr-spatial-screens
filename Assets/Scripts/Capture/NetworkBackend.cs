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
    public sealed class NetworkBackend : ICaptureBackend, IYuvBackend, IDepthProvider
    {
        static double s_UploadMsSum; static int s_UploadCount; static float s_LogAt;
        public readonly ushort StreamId;
        public readonly long Hwnd;
        int m_W, m_H;
        float m_Fps; int m_Count; float m_FpsStart;
        public double LastDecodeMs { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        readonly AndroidJpegDecoder m_Decoder = new AndroidJpegDecoder();
        HevcDecoder m_Hevc;                            // hardware-encoded HEVC mode (software-decoded here): see PollYuv
        volatile bool m_Busy, m_Ready, m_Failed;       // m_Busy: a decode is running or its pixels are waiting for upload
        uint m_ReadySeq;
        long m_DecodeTicks;
        /// <summary>False when the decoded picture is top-row-first (what the panel shader expects); true when it came through Texture2D.LoadImage (bottom row first).</summary>
        public bool NeedsFlip => m_Failed;
#else
        public bool NeedsFlip => true;
#endif

        // ------------------------------------------------------------------ Depth Lab (optional; nothing here runs while AI depth is not selected)

        const int DepthRequestFps = 5;                          // depth moves slowly: a few updates a second, smoothed between them here
        Texture2D m_DepthTex;
        byte[] m_DepthTarget, m_DepthCur;
        int m_DepthW, m_DepthH, m_DepthSeen, m_DepthEpoch = -1;
        bool m_DepthAsked;

        public Texture2D Depth => m_DepthTex;

        /// <summary>Called every frame by the source: asks the PC for depth while AI depth is selected (and again after a reconnect), and eases the picture towards each new estimate.</summary>
        public void TickDepth(float dt)
        {
            bool want = DepthProfiles.MasterOn && DepthProfiles.Live == DepthProfiles.Profile.Ai;
            if (want != m_DepthAsked || (want && m_DepthEpoch != RemoteHost.ConnectionEpoch))
            {
                m_DepthAsked = want; m_DepthEpoch = RemoteHost.ConnectionEpoch;
                RemoteHost.RequestDepth(StreamId, want ? DepthRequestFps : 0);
                if (!want) DisposeDepth();
            }
            if (!want) return;
            if (RemoteHost.TakeDepth(StreamId, ref m_DepthSeen, out var data, out int w, out int h))
            {
                if (m_DepthTex == null || m_DepthW != w || m_DepthH != h)
                {
                    if (m_DepthTex) UnityEngine.Object.Destroy(m_DepthTex);
                    m_DepthTex = new Texture2D(w, h, TextureFormat.R8, false, true) { name = "LiveDepth", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    m_DepthW = w; m_DepthH = h; m_DepthCur = (byte[])data.Clone();                  // the first picture is shown as it is
                    m_DepthTex.SetPixelData(m_DepthCur, 0); m_DepthTex.Apply(false);
                }
                m_DepthTarget = data;
            }
            if (m_DepthTarget != null && m_DepthCur != null && m_DepthTarget.Length == m_DepthCur.Length)
            {
                float k = 1f - Mathf.Exp(-dt * Mathf.Lerp(6f, 1.5f, DepthProfiles.Smoothing));                                                    // ease towards the newest estimate over about a fifth of a second
                bool changed = false;
                int dead = Mathf.RoundToInt(DepthProfiles.Smoothing * 14f);                                   // a surface only moves when its depth really changed by more than this (of 255)
                for (int i = 0; i < m_DepthCur.Length; i++)
                {
                    int diff = m_DepthTarget[i] - m_DepthCur[i];
                    if (diff == 0 || Mathf.Abs(diff) <= dead) continue;
                    int step = Mathf.RoundToInt(diff * k); if (step == 0) step = diff > 0 ? 1 : -1;
                    m_DepthCur[i] = (byte)(m_DepthCur[i] + step); changed = true;
                }
                if (changed) { m_DepthTex.SetPixelData(m_DepthCur, 0); m_DepthTex.Apply(false); }
            }
        }

        void DisposeDepth()
        {
            if (m_DepthTex) UnityEngine.Object.Destroy(m_DepthTex);
            m_DepthTex = null; m_DepthTarget = null; m_DepthCur = null; m_DepthW = m_DepthH = 0;
        }

        public NetworkBackend(long hwnd)
        {
            Hwnd = hwnd; StreamId = RemoteHost.OpenStream(hwnd);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (VideoMode.Hevc) { RemoteHost.SetCodec(StreamId, 1); m_Hevc = new HevcDecoder(StreamId); }
#endif
        }

        // ------------------------------------------------------------------ video (HEVC) mode

#if UNITY_ANDROID && !UNITY_EDITOR
        public bool IsYuvActive => m_Hevc != null;
        double m_VStatAt, m_VDecSum; int m_VStatN; long m_VBytes0;

        public bool PollYuv(ref Texture2D y, ref Texture2D u, ref Texture2D v, out int w, out int h)
        {
            w = m_W; h = m_H;
            if (m_Hevc == null) return false;
            if (m_Hevc.Failed)
            {
                // fall back to the proven JPEG path for this stream
                UnityEngine.Debug.LogWarning("[XrSpatial] HEVC unavailable (" + m_Hevc.FailReason + "), this screen goes back to JPEG");
                m_Hevc.Dispose(); m_Hevc = null; RemoteHost.SetCodec(StreamId, 0);
                return false;
            }
            if (!m_Hevc.TryAcquire(out var py, out var pu, out var pv, out int pw, out int ph, out int lenY, out int lenC, out uint seq, out bool ack)) return false;
            try
            {
                Plane(ref y, pw, ph); Plane(ref u, pw / 2, ph / 2); Plane(ref v, pw / 2, ph / 2);
                var sw = Stopwatch.StartNew();
                y.LoadRawTextureData(py, lenY); y.Apply(false);
                u.LoadRawTextureData(pu, lenC); u.Apply(false);
                v.LoadRawTextureData(pv, lenC); v.Apply(false);
                LastDecodeMs = m_Hevc.DecodeMs;
                Account(sw.Elapsed.TotalMilliseconds);
                w = pw; h = ph; m_W = pw; m_H = ph;
            }
            finally { m_Hevc.Release(); }
            if (ack) RemoteHost.Ack(StreamId, seq);
            CountFrame();
            m_VDecSum += m_Hevc.DecodeMs; m_VStatN++;
            if (Time.unscaledTime - m_VStatAt > 5f)
            {
                long bytes = RemoteHost.VideoBytes(StreamId);
                if (m_VStatAt > 0) UnityEngine.Debug.Log($"[XrSpatial] hevc stream {StreamId}: {m_Fps:0.0} fps shown, {pw}x{ph}, decode {m_VDecSum / Mathf.Max(1, m_VStatN):0.0} ms, backlog {m_Hevc.Queued}, unit age {m_Hevc.LastAgeMs:0} ms, {(bytes - m_VBytes0) * 8.0 / (Time.unscaledTime - m_VStatAt) / 1e6:0.0} Mbit/s");
                m_VStatAt = Time.unscaledTime; m_VDecSum = 0; m_VStatN = 0; m_VBytes0 = bytes;
            }
            return true;
        }

        static void Plane(ref Texture2D t, int w, int h)
        {
            if (t && t.width == w && t.height == h) return;
            if (t) Object.Destroy(t);
            t = new Texture2D(w, h, TextureFormat.R8, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        }
#else
        public bool IsYuvActive => false;
        public bool PollYuv(ref Texture2D y, ref Texture2D u, ref Texture2D v, out int w, out int h) { w = h = 0; return false; }
#endif

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
            DisposeDepth();
            RemoteHost.CloseStream(StreamId);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Hevc?.Dispose(); m_Hevc = null;
            // a decode may still be running on a worker: let it finish before its buffers go away
            ThreadPool.UnsafeQueueUserWorkItem(_ => { for (int i = 0; i < 200 && m_Busy && !m_Ready; i++) Thread.Sleep(10); m_Decoder.Dispose(); }, null);
#endif
        }
    }
}
