using System;
using System.Collections;
using System.Runtime.InteropServices;
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
        /// <summary>Simulates a lost controller: Poll returns an invalid state.</summary>
        public bool Lost;
        public Transform PaletteAnchor => null;
        public Transform Head => HeadTransform;
        public PointerState Poll()
        {
            var s = State; s.valid = !Lost;
            State.triggerDown = State.triggerUp = State.gripDown = State.gripUp = State.secondaryDown = false;
            return s;
        }
        public void Haptic(float amplitude, float seconds) { }
        public void Aim(Vector3 from, Vector3 at) { State.ray = new Ray(from, (at - from).normalized); State.rotation = Quaternion.LookRotation(State.ray.direction); }
    }

    public sealed partial class SpatialApp
    {
        static float P(string s) => float.Parse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        static Vector2 P2(string s) { var f = s.Split(','); return new Vector2(P(f[0]), P(f[1])); }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        static void MoveWindowBy(long hwnd, int dx, int dy, RectInt now) => SetWindowPos(new IntPtr(hwnd), IntPtr.Zero, now.x + dx, now.y + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
        static void ResizeWindow(long hwnd, int w, int h) => SetWindowPos(new IntPtr(hwnd), IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 | 0x0010);                                      // NOMOVE | NOZORDER | NOACTIVATE
        static void CloseWindow(long hwnd) => PostMessage(new IntPtr(hwnd), 0x0010, IntPtr.Zero, IntPtr.Zero);                                                                      // WM_CLOSE
#else
        static void MoveWindowBy(long hwnd, int dx, int dy, RectInt now) { }
        static void ResizeWindow(long hwnd, int w, int h) { }
        static void CloseWindow(long hwnd) { }
#endif

        /// <summary>
        /// --xrss-selftest-click SX,SY: with the interact tool on, clicks the first real window screen at source coordinates (0..1, origin top-left) and logs what was sent and which desktop pixel was expected.
        /// Options: --xrss-selftest-crop X,Y,W,H (click through a cropped copy) · --xrss-selftest-tilt DEG · --xrss-selftest-right · --xrss-selftest-wheel · --xrss-selftest-double
        /// · --xrss-selftest-drag SX2,SY2 (press at the first point, drag to the second, release; may lie outside 0..1 to leave the panel) · --xrss-selftest-loss (controller tracking lost while held)
        /// · --xrss-selftest-move DX,DY / --xrss-selftest-resize W,H (move or resize the real window first) · --xrss-selftest-close (close the window first: nothing may be sent).
        /// </summary>
        IEnumerator SelfTestClick(float sx, float sy)
        {
            // Pick the first screen that shows a real window (a saved layout may also hold pattern screens).
            PanelView panel = null;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < deadline && panel == null)
            {
                foreach (var p in Workspace.Panels)
                    if (p.Source != null && p.Source.Def.kind != "pattern" && p.Source.HasFrame && p.Source.Window.IsValid) { panel = p; break; }
                if (panel == null) yield return null;
            }
            if (panel == null) { Debug.Log("[XrSpatial] selftest: no screen with a live window"); yield break; }

            var args = Environment.GetCommandLineArgs();
            string ArgOf(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool Flag(string n) => Array.IndexOf(args, n) >= 0;

            if (ArgOf("--xrss-selftest-crop") is string cs)
            {
                var f = cs.Split(',');
                panel = Tool.CreateCropPanel(panel, new Rect(P(f[0]), P(f[1]), P(f[2]), P(f[3])));
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
            if (ArgOf("--xrss-selftest-move") is string ms) { var d = P2(ms); MoveWindowBy(win.hwnd, Mathf.RoundToInt(d.x), Mathf.RoundToInt(d.y), InputForwarder.WindowRect(win)); yield return new WaitForSeconds(1.2f); }
            if (ArgOf("--xrss-selftest-resize") is string rs) { var d = P2(rs); ResizeWindow(win.hwnd, Mathf.RoundToInt(d.x), Mathf.RoundToInt(d.y)); yield return new WaitForSeconds(1.5f); }
            if (Flag("--xrss-selftest-close")) { CloseWindow(win.hwnd); yield return new WaitForSeconds(2.0f); }

            Vector3 World(Vector2 src) => QuadMath.UvToWorld(QuadMath.SourceToPanelUv(src, panel.Def.crop), panel.Def.corners);
            Vector2Int Expect(Vector2 src) { var r = InputForwarder.WindowRect(panel.Source.Window); return InputForwarder.SourceToDesktop(src, r.x, r.y, r.width, r.height); }

            var ptr = new SelfTestPointer { HeadTransform = Cam.transform };
            Tool.Pointer = ptr;
            Tool.Ui = null;
            Tool.SetIdle();
            Tool.InteractMode = true;
            var start = new Vector2(sx, sy);
            ptr.Aim(Cam.transform.position, World(start));
            var expect = Expect(start);
            var r0 = InputForwarder.WindowRect(panel.Source.Window);
            Debug.Log($"[XrSpatial] selftest: window rect {r0.x},{r0.y} {r0.width}x{r0.height}; clicking source ({sx:0.000},{sy:0.000}); expected desktop pixel {expect.x},{expect.y}");
            yield return new WaitForSeconds(0.5f);

            IEnumerator Click(float hold)
            {
                ptr.State.triggerDown = true; ptr.State.triggerHeld = true;
                yield return null;
                yield return new WaitForSeconds(hold);
                ptr.State.triggerHeld = false; ptr.State.triggerUp = true;
                yield return null; yield return null;
            }

            if (ArgOf("--xrss-selftest-drag") is string dg)
            {
                var end = P2(dg);
                ptr.State.triggerDown = true; ptr.State.triggerHeld = true;
                yield return new WaitForSeconds(0.3f);
                for (float t = 0f; t < 1f; t += Time.deltaTime / 0.8f) { ptr.Aim(Cam.transform.position, World(Vector2.Lerp(start, end, t))); yield return null; }
                ptr.Aim(Cam.transform.position, World(end));
                yield return new WaitForSeconds(0.3f);
                var ee = Expect(new Vector2(Mathf.Clamp01(end.x), Mathf.Clamp01(end.y)));
                Debug.Log($"[XrSpatial] selftest: expected drag end pixel {ee.x},{ee.y}");
                ptr.State.triggerHeld = false; ptr.State.triggerUp = true;
                yield return null; yield return null;
            }
            else if (Flag("--xrss-selftest-loss"))
            {
                ptr.State.triggerDown = true; ptr.State.triggerHeld = true;
                yield return new WaitForSeconds(0.5f);
                ptr.Lost = true;                       // the controller stops tracking while the button is held
                yield return new WaitForSeconds(0.6f);
                ptr.Lost = false; ptr.State.triggerHeld = false;
                yield return new WaitForSeconds(0.3f);
            }
            else if (Flag("--xrss-selftest-double"))
            {
                yield return Click(0.12f);
                yield return new WaitForSeconds(0.08f);
                ptr.Aim(Cam.transform.position, World(start + new Vector2(3f / Mathf.Max(1, r0.width), 2f / Mathf.Max(1, r0.height))));      // a second click a few pixels away, as a shaky hand makes
                yield return Click(0.12f);
            }
            else yield return Click(0.9f);

            if (Flag("--xrss-selftest-right")) { ptr.State.secondaryDown = true; yield return null; yield return new WaitForSeconds(0.3f); }
            if (Flag("--xrss-selftest-wheel")) { ptr.State.stick = new Vector2(0f, 1f); yield return new WaitForSeconds(0.3f); ptr.State.stick = Vector2.zero; }
            Debug.Log($"[XrSpatial] selftest: click sent (last action: {Forwarder.LastAction}; refused: {InputForwarder.RefusedPresses}; last refusal: {Forwarder.LastRefusal}; focus attempts: {InputForwarder.FocusAttempts}; failed SendInput calls: {InputForwarder.FailedSends}, last error {InputForwarder.LastError})");
            yield return new WaitForSeconds(1.0f);
            Tool.InteractMode = false;
            Application.Quit();
        }
    }
}
