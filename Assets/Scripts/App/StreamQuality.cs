using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XrSpatial.Capture;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// Smart quality for the streamed windows. Every half second each stream is given only what its screens need: the width in pixels the screen takes up in the
    /// headset's eye image (corrected for any crop, with some headroom) and, when none of its screens is in view, a slow frame rate. This keeps the headset's
    /// picture decoding to what can actually be seen, so several large windows no longer starve each other.
    /// </summary>
    public sealed class StreamQuality : MonoBehaviour
    {
        public SpatialWorkspace Workspace;
        public Camera Cam;

        const int MinWidth = 640, MaxWidth = 3840, Step = 160, OutOfViewFps = 2, FullFps = 60;
        const float Headroom = 1.5f, Hysteresis = 0.2f, Period = 0.5f;

        const float HoldLowerFor = 4f;
        readonly Dictionary<ushort, (float width, float since)> m_Held = new Dictionary<ushort, (float width, float since)>();
        struct Sent { public int width, fps, epoch; }
        readonly Dictionary<ushort, Sent> m_Sent = new Dictionary<ushort, Sent>();
        float m_Next;
        public string Summary { get; private set; } = "";

        void Update()
        {
            if (!RemoteHost.Active || !RemoteHost.Connected || !Workspace || !Cam || Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + Period;
            int eyeWidth = XRSettings.eyeTextureWidth > 0 ? XRSettings.eyeTextureWidth : 2160;

            var need = new Dictionary<ushort, (float width, bool visible)>();
            foreach (var panel in Workspace.Panels)
            {
                if (panel == null || panel.Source == null || !panel.Def.visible) continue;
                var nb = panel.Source.Backend as NetworkBackend;
                if (nb == null || nb.HasEnded) continue;
                Measure(panel, eyeWidth, out float srcWidth, out bool visible);
                need.TryGetValue(nb.StreamId, out var cur);
                need[nb.StreamId] = (Mathf.Max(cur.width, srcWidth), cur.visible || visible);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var kv in need)
            {
                ushort id = kv.Key;
                // Smoothing: a higher need is honoured at once, a lower one only after it has lasted a few seconds, so quick head turns do not make the width flap.
                if (!m_Held.TryGetValue(id, out var held) || kv.Value.width >= held.width || Time.unscaledTime - held.since > HoldLowerFor) held = (kv.Value.width, Time.unscaledTime);
                else held = (held.width, held.since);
                if (kv.Value.width >= held.width) held = (kv.Value.width, Time.unscaledTime);
                m_Held[id] = held;
                int width = Quantize(Mathf.Max(held.width, kv.Value.width));
                int fps = kv.Value.visible ? FullFps : OutOfViewFps;
                m_Sent.TryGetValue(id, out var last);
                bool fresh = last.epoch != RemoteHost.ConnectionEpoch;
                if (!kv.Value.visible) width = fresh ? width : last.width;             // out of view: keep the width, just slow down
                bool widthChanged = fresh || Mathf.Abs(width - last.width) > last.width * Hysteresis;
                if (widthChanged || fps != last.fps || fresh)
                {
                    int use = widthChanged ? width : last.width;
                    RemoteHost.SetStreamQuality(id, use, fps);
                    m_Sent[id] = new Sent { width = use, fps = fps, epoch = RemoteHost.ConnectionEpoch };
                    Debug.Log($"[XrSpatial] stream quality: stream {id} -> {use} px wide, {(kv.Value.visible ? "in view" : "out of view, " + fps + " fps")}");
                }
                sb.Append($"#{id}:{m_Sent[id].width}px{(kv.Value.visible ? "" : " paused")} ");
            }
            Summary = sb.ToString();
        }

        /// <summary>The source width needed for a panel: its on-screen width in the eye image divided by the fraction of the source it shows. visible: any corner in front of the head and the outline overlapping the view.</summary>
        void Measure(PanelView panel, int eyeWidth, out float srcWidth, out bool visible)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            bool anyFront = false;
            foreach (var c in panel.Def.corners)
            {
                var v = Cam.WorldToViewportPoint(panel.transform.TransformPoint(c));
                if (v.z <= 0.05f) continue;
                anyFront = true;
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x); minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            // a screen counts as in view up to one view-width beyond each edge, so a fast head turn finds it already running at full rate
            visible = anyFront && maxX > -1.0f && minX < 2.0f && maxY > -0.6f && minY < 1.6f;
            if (!anyFront) { srcWidth = 0f; return; }
            float onScreenPx = Mathf.Clamp(maxX - minX, 0.02f, 3f) * eyeWidth;
            float cropWidth = Mathf.Max(0.05f, panel.Def.crop.width);
            srcWidth = onScreenPx / cropWidth * Headroom;
        }

        static int Quantize(float w) => Mathf.Clamp(Mathf.CeilToInt(w / Step) * Step, MinWidth, MaxWidth);
    }
}
