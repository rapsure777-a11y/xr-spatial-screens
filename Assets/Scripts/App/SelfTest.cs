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
        IEnumerator SelfTestClick(float sx, float sy)
        {
            // Wait for a screen with a live frame.
            float deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline && !(Workspace.Panels.Count > 0 && Workspace.Panels[0].Source != null && Workspace.Panels[0].Source.HasFrame && Workspace.Panels[0].Source.Window.IsValid)) yield return null;
            if (Workspace.Panels.Count == 0) { Debug.Log("[XrSpatial] selftest: no screen"); yield break; }
            var panel = Workspace.Panels[0];
            var win = panel.Source.Window;
            var uv = QuadMath.SourceToPanelUv(new Vector2(sx, sy), panel.Def.crop);
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
            Debug.Log($"[XrSpatial] selftest: click sent (last action: {Forwarder.LastAction}; failed SendInput calls: {InputForwarder.FailedSends}, last error {InputForwarder.LastError})");
            yield return new WaitForSeconds(1.0f);
            Tool.InteractMode = false;
            Application.Quit();
        }
    }
}
