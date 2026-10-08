using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XrSpatial.Capture;
using XrSpatial.Core;
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

        const int MinWidth = 1280, MaxWidth = 3840, Step = 160, OutOfViewFps = 2, FullFps = 60;
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
                // A screen that is out of view (need 0) keeps whatever it had.
                float needW = kv.Value.visible ? kv.Value.width : 0f;
                if (!m_Held.TryGetValue(id, out var held)) held = (needW, Time.unscaledTime);
                else if (needW >= held.width) held = (needW, Time.unscaledTime);
                else if (kv.Value.visible && Time.unscaledTime - held.since > HoldLowerFor) held = (needW, Time.unscaledTime);
                m_Held[id] = held;
                int width = Quantize(held.width);
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

        /// <summary>The source width needed for a panel (see <see cref="StreamMath"/>) and whether it is in or near the view.</summary>
        void Measure(PanelView panel, int eyeWidth, out float srcWidth, out bool visible)
        {
            var corners = panel.Def.corners;
            var world = new Vector3[corners.Length];
            for (int i = 0; i < corners.Length; i++) world[i] = panel.transform.TransformPoint(corners[i]);
            srcWidth = StreamMath.RequiredSourceWidth(Cam.transform.position, Cam.transform.forward, world, panel.Def.crop.width, eyeWidth, Headroom, out visible);
        }

        static int Quantize(float w) => StreamMath.Quantize(w, Step, MinWidth, MaxWidth);    }
}
