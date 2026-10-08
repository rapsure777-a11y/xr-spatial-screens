using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// Headset-side counterpart of <see cref="InputForwarder"/>: while the tool is in interact mode and the laser is on a panel, the laser position on the panel is
    /// turned into a position on the source image (crop aware, <see cref="PanelView.UvToSource"/>) and sent to the PC host, which maps it onto the real window and injects the
    /// input with the checks of the Windows forwarder (window to the front, covered-spot refusal, dead zone, double-click snap). The press target is locked while a button is held,
    /// and everything is released when tracking is lost.
    /// Controls (same as the PC version): trigger = left button (hold to drag), secondary button = right click, stick up/down = wheel.
    /// </summary>
    public sealed class RemoteInputForwarder : MonoBehaviour
    {
        const byte Move = 0, LeftDown = 1, LeftUp = 2, RightClick = 3, Wheel = 4;

        public SurfaceTool Tool;
        public bool Enabled = true;
        public string LastAction { get; private set; } = "none";

        bool m_Left;
        PanelView m_Target;
        Vector2 m_LastUv, m_LastSent;
        float m_NextWheel;

        void Update()
        {
            if (!Enabled || !Tool || !Tool.InteractMode || !RemoteHost.Active) { Release("interact off"); return; }
            var s = Tool.InteractState;
            PanelView panel = m_Left ? m_Target : Tool.InteractPanel;

            if (m_Left && (!Tool.InteractTracking || !panel)) { Release("tracking lost"); return; }
            if (!m_Left && (!Tool.InteractLive || !panel)) return;
            if (panel.Source == null || panel.Source.Def.kind == "pattern") { Release("no window"); return; }   // the test pattern is not a real window

            // Where on the source is the laser? While a button is held the laser may wander off the panel: keep the destination and clamp to its edge.
            Vector2 uv;
            if (m_Left) uv = QuadMath.RayToUv(s.ray, panel.Def.corners, out var hit, out _, false) ? new Vector2(Mathf.Clamp01(hit.x), Mathf.Clamp01(hit.y)) : m_LastUv;
            else uv = Tool.InteractUv;
            m_LastUv = uv;
            Vector2 src = panel.UvToSource(uv);

            if (s.triggerDown && !m_Left) { RemoteHost.SendPointer(LeftDown, src.x, src.y, 0); m_Left = true; m_Target = panel; m_LastSent = src; LastAction = "left down"; }
            else if (m_Left && (s.triggerUp || !s.triggerHeld)) { RemoteHost.SendPointer(LeftUp, src.x, src.y, 0); m_Left = false; m_Target = null; LastAction = "left up"; }
            else if ((src - m_LastSent).sqrMagnitude > 4e-7f) { RemoteHost.SendPointer(Move, src.x, src.y, 0); m_LastSent = src; }

            if (s.secondaryDown && !m_Left) { RemoteHost.SendPointer(RightClick, src.x, src.y, 0); LastAction = "right click"; }
            if (Mathf.Abs(s.stick.y) > 0.5f && Time.unscaledTime > m_NextWheel && !m_Left) { RemoteHost.SendPointer(Wheel, src.x, src.y, Mathf.Sign(s.stick.y)); m_NextWheel = Time.unscaledTime + 0.08f; LastAction = "wheel"; }
        }

        /// <summary>Lets go of a held button (tracking lost, interaction switched off) so nothing stays pressed on the PC.</summary>
        void Release(string why)
        {
            if (!m_Left) return;
            RemoteHost.SendLost();
            m_Left = false; m_Target = null; LastAction = "released (" + why + ")";
        }

        void OnDisable() => Release("disabled");
    }
}
