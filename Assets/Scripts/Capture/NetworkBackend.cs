using System.Diagnostics;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Capture backend for the headset build: the pixels come from the PC host over the network (<see cref="RemoteHost"/>) instead of a local helper process.
    /// Decodes at most one JPEG per rendered frame and acknowledges it so the host can measure the round trip.
    /// </summary>
    public sealed class NetworkBackend : ICaptureBackend
    {
        int m_W, m_H;
        float m_Fps; int m_Count; float m_FpsStart;
        public double LastDecodeMs { get; private set; }

        public string Status => RemoteHost.State + (m_W > 0 ? $" ({m_W}x{m_H}, {m_Fps:0} fps)" : "");
        public bool IsRunning => RemoteHost.Active;
        public bool HasEnded => !RemoteHost.Active;
        public int Width => m_W;
        public int Height => m_H;
        public float CaptureFps => m_Fps;
        /// <summary>The window as the PC reports it; on the headset only its size matters (hwnd 0: the PC host does the input injection and its own checks).</summary>
        public WindowGeometry Window => new WindowGeometry { x = 0, y = 0, width = m_W, height = m_H, hwnd = 0, foreground = true, alive = RemoteHost.Active && m_W > 0 };

        public bool PollInto(ref Texture2D target)
        {
            if (!RemoteHost.TakeFrame(out var jpeg, out int w, out int h, out uint seq)) return false;
            if (!target) target = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var sw = Stopwatch.StartNew();
            bool ok = target.LoadImage(jpeg, false);
            LastDecodeMs = sw.Elapsed.TotalMilliseconds;
            if (!ok) return false;
            m_W = target.width; m_H = target.height;
            RemoteHost.Ack(seq);
            m_Count++;
            if (Time.unscaledTime - m_FpsStart >= 2f) { m_Fps = m_Count / (Time.unscaledTime - m_FpsStart); m_Count = 0; m_FpsStart = Time.unscaledTime; }
            return true;
        }

        public void Dispose() { }
    }
}
