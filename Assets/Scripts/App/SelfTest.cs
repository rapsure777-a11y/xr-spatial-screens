using System;
using System.Collections;
using UnityEngine;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>A pointer driven by code (self tests and automation): the ray goes through a chosen world point; trigger edges are set explicitly.</summary>
    public sealed class SelfTestPointer : IPointerSource
    {
        public PointerState State;
        public Transform HeadTransform;
        public Transform PaletteAnchor => null;
        public Transform Head => HeadTransform;
        public PointerState Poll()
        {
            var s = State; s.valid = true;
            State.triggerDown = State.triggerUp = State.gripDown = State.gripUp = State.secondaryDown = false;
            return s;
        }
        public void Haptic(float amplitude, float seconds) { }
        public void Aim(Vector3 from, Vector3 at) { State.ray = new Ray(from, (at - from).normalized); State.rotation = Quaternion.LookRotation(State.ray.direction); }
    }

    public sealed partial class SpatialApp
    {
        /// <summary>--xrss-selftest-click SX SY: with the interact tool on, clicks the first screen at source coordinates (SX, SY) (0..1, origin top-left) and logs what was sent.</summary>
        static float P(string s) => float.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

        IEnumerator SelfTestClick(float sx, float sy)
        {
            // Wait for a screen with a live frame.
            // (a saved layout may also hold pattern screens, which have no window: pick the first screen that does)
            PanelView panel = null;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline && panel == null)
            {
                foreach (var p in Workspace.Panels)
                    if (p.Source != null && p.Source.Def.kind != "pattern" && p.Source.HasFrame && p.Source.Window.IsValid) { panel = p; break; }
                if (panel == null) yield return null;
            }
            if (panel == null) { Debug.Log("[XrSpatial] selftest: no screen with a live window"); yield break; }
            // Optional: click through a cropped and/or tilted copy instead of the whole panel (--xrss-selftest-crop X,Y,W,H in source units; --xrss-selftest-tilt DEGREES about the vertical axis).
            var args = Environment.GetCommandLineArgs();
            string ArgOf(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (ArgOf("--xrss-selftest-crop") is string cs)
            {
                var f = cs.Split(',');
                var crop = new Rect(P(f[0]), P(f[1]), P(f[2]), P(f[3]));
                panel = Tool.CreateCropPanel(panel, crop);
            }
            if (ArgOf("--xrss-selftest-tilt") is string ts)
            {
                var c = panel.Def.corners;
                var centre = QuadMath.Centroid(c);
                var rot = Quaternion.AngleAxis(P(ts), (c[QuadMath.TL] - c[QuadMath.BL]).normalized);
                for (int i = 0; i < 4; i++) c[i] = centre + rot * (c[i] - centre);
                panel.Rebuild();
            }
            var win = panel.Source.Window;
            var uv =QuadMath.SourceToPanelUv(new Vector2(sx, sy), panel.Def.crop);
            var target = QuadMath.UvToWorld(uv, panel.Def.corners);
            var ptr = new SelfTestPointer { HeadTransform = Cam.transform };
            Tool.Pointer = ptr;
            Tool.Ui = null;
            Tool.SetIdle();
            Tool.InteractMode = true;
            ptr.Aim(Cam.transform.position, target);
            var expect = InputForwarder.SourceToDesktop(new Vector2(sx, sy), win.x, win.y, win.width, win.height);
            Debug.Log($"[XrSpatial] selftest: window rect {win.x},{win.y} {win.width}x{win.height}; clicking source ({sx:0.000},{sy:0.000}); expected desktop pixel {expect.x},{expect.y}");
            yield return new WaitForSeconds(0.5f);
            ptr.State.triggerDown = true; ptr.State.triggerHeld = true;
            yield return null;
            yield return new WaitForSeconds(0.9f);
            ptr.State.triggerHeld = false; ptr.State.triggerUp = true;
            yield return null; yield return null;
            if (Array.IndexOf(args, "--xrss-selftest-right") >= 0) { ptr.State.secondaryDown = true; yield return null; yield return new WaitForSeconds(0.3f); }
            if (Array.IndexOf(args, "--xrss-selftest-wheel") >= 0) { ptr.State.stick = new Vector2(0f, 1f); yield return new WaitForSeconds(0.3f); ptr.State.stick = Vector2.zero; }
            Debug.Log($"[XrSpatial] selftest: click sent (last action: {Forwarder.LastAction}; focus attempts: {InputForwarder.FocusAttempts}; failed SendInput calls: {InputForwarder.FailedSends}, last error {InputForwarder.LastError})");
            yield return new WaitForSeconds(1.0f);
            Tool.InteractMode = false;
            Application.Quit();
        }
    }
}
