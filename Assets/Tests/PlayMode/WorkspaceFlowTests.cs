using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.App;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>A pointer we drive from the test: edge flags (down/up) last one frame, held state persists.</summary>
    sealed class ScriptedPointer : IPointerSource
    {
        public PointerState State;
        public Transform HeadTransform;
        public Transform PaletteAnchor => null;
        public Transform Head => HeadTransform;
        public int Haptics;
        public PointerState Poll()
        {
            var s = State; s.valid = true;
            State.triggerDown = State.triggerUp = State.gripDown = State.gripUp = State.secondaryDown = State.menuDown = State.paletteToggle = false;
            return s;
        }
        public void Haptic(float amplitude, float seconds) { Haptics++; }
        public void Aim(Vector3 from, Vector3 at) { State.ray = new Ray(from, (at - from).normalized); State.rotation = Quaternion.LookRotation(State.ray.direction); }
    }

    public class WorkspaceFlowTests
    {
        SpatialApp m_App;
        ScriptedPointer m_Ptr;
        string m_Dir;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "xrss-flow-" + System.Guid.NewGuid().ToString("N"));
            m_App = SpatialApp.Create(new SpatialApp.Options { forceDesktop = true, showControlPanel = false, autoLoadLast = false, parseCommandLine = false, layoutDirectory = m_Dir, background = BackgroundMode.Void });
            yield return null;
            m_Ptr = new ScriptedPointer { HeadTransform = m_App.Cam.transform };
            m_App.Tool.Pointer = m_Ptr;
            m_App.Tool.Ui = null;                                  // the palette is not under test here
            m_App.Tool.AutoFitAspect = false;                      // exact-corner tests below; auto-fit has its own test
            m_App.Workspace.AutoSave = false;
            m_App.AddPattern();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_App) Object.Destroy(m_App.gameObject);
            yield return null;
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        Vector3 Head => m_App.Cam.transform.position;
        Vector3 PointAt(float x, float y, float z) => new Vector3(x, y, z);

        IEnumerator Click(Vector3 target)
        {
            m_Ptr.Aim(Head, target);
            m_Ptr.State.triggerDown = true; m_Ptr.State.triggerHeld = true;
            yield return null;
            m_Ptr.State.triggerHeld = false; m_Ptr.State.triggerUp = true;
            yield return null;
        }

        /// <summary>Four points at exactly the placement distance along the rays, clockwise from the top-left as the viewer sees them.</summary>
        static Vector3[] FourPoints(Vector3 head, float dist)
        {
            Vector3 P(float yawDeg, float pitchDeg) => head + Quaternion.Euler(-pitchDeg, yawDeg, 0f) * Vector3.forward * dist;
            return new[] { P(-25f, 14f), P(25f, 14f), P(25f, -12f), P(-25f, -12f) };
        }

        [UnityTest]
        public IEnumerator PlaceFourPoints_CreatesAFlatScreen_ThePictureIsNotMirrored()
        {
            var tool = m_App.Tool;
            tool.BeginPlace();
            Assert.AreEqual(ToolMode.Place, tool.Mode);
            var pts = FourPoints(Head, tool.PlaceDistance);
            for (int i = 0; i < 4; i++) { yield return Click(pts[i]); if (i < 3) Assert.AreEqual(i + 1, tool.PlacedCount); }
            Assert.AreEqual(ToolMode.Idle, tool.Mode, "the fourth point closes the screen");
            Assert.AreEqual(1, m_App.Workspace.Panels.Count);
            var def = m_App.Workspace.Panels[0].Def;
            for (int i = 0; i < 4; i++) Assert.AreEqual(0f, Vector3.Distance(pts[i], def.corners[i]), 0.02f, "corner " + i + " sits where it was placed (planarised)");
            Assert.IsTrue(QuadMath.IsClockwiseFrom(def.corners, Head));
            Assert.IsTrue(QuadMath.IsValidQuad(def.corners));
            Assert.Less(QuadMath.NonPlanarity(def.corners), 1e-4f);
            Assert.AreSame(tool.Selected, m_App.Workspace.Panels[0]);
        }

        [UnityTest]
        public IEnumerator PlacingAnticlockwise_IsReorderedSoThePictureReadsCorrectly()
        {
            var tool = m_App.Tool;
            tool.BeginPlace();
            var pts = FourPoints(Head, tool.PlaceDistance);
            var anti = new[] { pts[0], pts[3], pts[2], pts[1] };           // drawn the other way round
            for (int i = 0; i < 4; i++) yield return Click(anti[i]);
            var def = m_App.Workspace.Panels[0].Def;
            Assert.IsTrue(QuadMath.IsClockwiseFrom(def.corners, Head));
            Assert.AreEqual(0f, Vector3.Distance(pts[0], def.corners[0]), 0.02f, "the first point is still the top-left corner");
        }

        [UnityTest]
        public IEnumerator BadPoints_AreRejectedWithAMessage_AndNothingIsCreated()
        {
            var tool = m_App.Tool;
            tool.BeginPlace();
            var pts = FourPoints(Head, tool.PlaceDistance);
            var bow = new[] { pts[0], pts[2], pts[1], pts[3] };            // a bow-tie
            for (int i = 0; i < 4; i++) yield return Click(bow[i]);
            Assert.AreEqual(0, m_App.Workspace.Panels.Count);
            StringAssert.Contains("flat, convex", tool.Message);
        }

        [UnityTest]
        public IEnumerator DraggingACorner_MovesIt_AndKeepsTheScreenFlat()
        {
            var tool = m_App.Tool;
            var panel = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            Assert.NotNull(panel);
            var before = (Vector3[])panel.Def.corners.Clone();
            // Grab the bottom-right corner with the laser and pull it outward and down.
            Vector3 c = before[QuadMath.BR];
            m_Ptr.Aim(Head, c);
            m_Ptr.State.triggerDown = true; m_Ptr.State.triggerHeld = true;
            yield return null;
            Vector3 target = c + new Vector3(0.25f, -0.15f, 0.1f);
            m_Ptr.Aim(Head, target);
            m_Ptr.State.triggerDown = false;
            yield return null; yield return null;
            m_Ptr.State.triggerHeld = false; m_Ptr.State.triggerUp = true;
            yield return null; yield return null;
            var after = panel.Def.corners;
            Assert.Greater(Vector3.Distance(before[QuadMath.BR], after[QuadMath.BR]), 0.1f, "the corner moved");
            foreach (int i in new[] { QuadMath.TL, QuadMath.TR, QuadMath.BL }) Assert.AreEqual(0f, Vector3.Distance(before[i], after[i]), 0.02f, "the other corners stayed (the screen stays planar by moving one corner within the plane of the others)");
            Assert.Less(QuadMath.NonPlanarity(after), 1e-3f);
            Assert.IsTrue(QuadMath.IsValidQuad(after));
        }

        [UnityTest]
        public IEnumerator Grip_GrabsAndMovesTheWholeScreen_StickScalesIt()
        {
            var tool = m_App.Tool;
            var panel = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            var before = (Vector3[])panel.Def.corners.Clone();
            var centre = QuadMath.Centroid(before);
            m_Ptr.Aim(Head, centre);
            m_Ptr.State.gripDown = true; m_Ptr.State.gripHeld = true;
            yield return null;
            m_Ptr.State.gripDown = false;
            m_Ptr.Aim(Head, centre + new Vector3(0.5f, 0.1f, 0f));
            yield return null; yield return null;
            var moved = QuadMath.Centroid(panel.Def.corners);
            Assert.Greater((moved - centre).magnitude, 0.2f, "the whole screen followed the laser");
            Assert.AreEqual(QuadMath.Size(before).x, QuadMath.Size(panel.Def.corners).x, 0.02f, "moving does not resize");
            m_Ptr.State.stick = new Vector2(1f, 0f);                       // scale up
            float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < 0.8f) yield return null;
            Assert.Greater(QuadMath.Size(panel.Def.corners).x, QuadMath.Size(before).x * 1.05f, "the stick scaled the screen");
            m_Ptr.State.stick = Vector2.zero; m_Ptr.State.gripHeld = false; m_Ptr.State.gripUp = true;
            yield return null;
            Assert.IsTrue(QuadMath.IsValidQuad(panel.Def.corners));
        }

        [UnityTest]
        public IEnumerator Crop_DraggingARectangle_MakesAnIndependentPanelOfTheSameSource()
        {
            var tool = m_App.Tool;
            var panel = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            tool.BeginCrop();
            var a = QuadMath.UvToWorld(new Vector2(0.7f, 0.9f), panel.Def.corners);   // the top-right "minimap" of the test pattern
            var b = QuadMath.UvToWorld(new Vector2(0.9f, 0.7f), panel.Def.corners);
            m_Ptr.Aim(Head, a); m_Ptr.State.triggerDown = true; m_Ptr.State.triggerHeld = true;
            yield return null;
            m_Ptr.State.triggerDown = false; m_Ptr.Aim(Head, b);
            yield return null; yield return null;
            m_Ptr.State.triggerHeld = false; m_Ptr.State.triggerUp = true;
            yield return null; yield return null;
            Assert.AreEqual(2, m_App.Workspace.Panels.Count);
            var crop = tool.Selected.Def;
            Assert.AreEqual(panel.Def.sourceId, crop.sourceId, "same captured source");
            Assert.AreEqual(0.7f, crop.crop.xMin, 0.03f); Assert.AreEqual(0.9f, crop.crop.xMax, 0.03f);
            Assert.AreEqual(0.1f, crop.crop.yMin, 0.03f); Assert.AreEqual(0.3f, crop.crop.yMax, 0.03f);
            Assert.AreNotSame(panel, tool.Selected);
            Assert.AreEqual(1, m_App.Workspace.Layout.sources.Count, "still one source feeding two panels");
        }

        [UnityTest]
        public IEnumerator AutoFit_ShapesARoughRectangleToThePicturesAspect_AndLeavesTrapezoidsAlone()
        {
            var tool = m_App.Tool;
            tool.AutoFitAspect = true;
            for (int i = 0; i < 40 && !(m_App.Workspace.GetSource(m_App.Workspace.ActiveSourceId)?.HasFrame ?? false); i++) yield return null;
            var rect = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            var s = QuadMath.Size(rect.Def.corners);
            Assert.AreEqual(16f / 9f, s.x / s.y, 0.01f, "a roughly rectangular drawing is fitted to the picture's 16:9");
            var trap = new[] { Head + new Vector3(-0.3f, 0.5f, 1.6f), Head + new Vector3(0.3f, 0.5f, 1.6f), Head + new Vector3(0.9f, -0.5f, 1.6f), Head + new Vector3(-0.9f, -0.5f, 1.6f) };
            var t = tool.CreateFromPoints(trap, Head);
            Assert.AreEqual(0f, Vector3.Distance(t.Def.corners[QuadMath.TR], trap[1]), 0.02f, "a deliberate trapezoid is kept exactly as drawn");
            tool.Select(rect);
            rect.Def.corners = QuadMath.Scale(rect.Def.corners, 1f);
            rect.Def.corners = QuadMath.RectAt(QuadMath.Centroid(rect.Def.corners), Vector3.right, Vector3.up, 1.0f, 1.0f);
            tool.FitSelectedAspect();
            var s2 = QuadMath.Size(rect.Def.corners);
            Assert.AreEqual(16f / 9f, s2.x / s2.y, 0.01f, "the palette's Fit picture button restores the true shape");
        }

        [UnityTest]
        public IEnumerator Layout_SurvivesASaveAndReload_IncludingCrops()
        {
            var tool = m_App.Tool;
            var panel = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            tool.CreateCropPanel(panel, new Rect(0.7f, 0.1f, 0.2f, 0.2f));
            m_App.Workspace.Layout.appKey = "flow-test";
            m_App.Workspace.SaveNow();
            m_App.Workspace.LoadLayout("flow-test");
            yield return null;
            Assert.AreEqual(2, m_App.Workspace.Panels.Count);
            Assert.AreEqual(1, m_App.Workspace.Layout.sources.Count);
            Assert.IsTrue(System.Linq.Enumerable.Any(m_App.Workspace.Panels, p => p.Def.crop.width < 0.3f));
            Assert.IsNotNull(m_App.Workspace.GetSource(m_App.Workspace.Layout.sources[0].id), "the live source was recreated");
        }

        [UnityTest]
        public IEnumerator Source_ProducesFrames_AndPanelsShareOneTexture()
        {
            var tool = m_App.Tool;
            var p1 = tool.CreateFromPoints(FourPoints(Head, 1.6f), Head);
            var p2 = tool.CreateCropPanel(p1, new Rect(0.0f, 0.0f, 0.3f, 0.3f));
            for (int i = 0; i < 30 && !p1.Source.HasFrame; i++) yield return null;
            Assert.IsTrue(p1.Source.HasFrame);
            Assert.AreSame(p1.Source, p2.Source);
            Assert.AreEqual(1920, p1.Source.Width);
            Assert.AreEqual(16f / 9f, p1.Source.Aspect, 1e-3f);
        }
    }
}
