using System.Linq;
using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Tests
{
    public class QuadMathTests
    {
        static Vector3[] Rect(float w = 1.6f, float h = 0.9f, float z = 2f) => new[]
        {
            new Vector3(-w / 2, h / 2, z), new Vector3(w / 2, h / 2, z), new Vector3(w / 2, -h / 2, z), new Vector3(-w / 2, -h / 2, z),
        };

        [Test]
        public void Rectangle_MapsCornersAndCentreToTheUnitSquare()
        {
            var q = Rect();
            Assert.IsTrue(QuadMath.WorldToUv(q[QuadMath.TL], q, out var tl)); Assert.AreEqual(0f, tl.x, 1e-4f); Assert.AreEqual(1f, tl.y, 1e-4f);
            Assert.IsTrue(QuadMath.WorldToUv(q[QuadMath.BR], q, out var br)); Assert.AreEqual(1f, br.x, 1e-4f); Assert.AreEqual(0f, br.y, 1e-4f);
            Assert.IsTrue(QuadMath.WorldToUv(new Vector3(0, 0, 2), q, out var c)); Assert.AreEqual(0.5f, c.x, 1e-4f); Assert.AreEqual(0.5f, c.y, 1e-4f);
        }

        [Test]
        public void Trapezoid_UsesPerspectiveCorrectMapping_NotAnAffineShear()
        {
            // A rectangle seen in perspective: the near (bottom) edge is wider than the far (top) edge. The midpoint of the left edge in world
            // space is NOT the middle of the picture's left edge; the homography puts the picture's centre at the diagonals' intersection.
            var q = new[] { new Vector3(-0.5f, 1f, 4f), new Vector3(0.5f, 1f, 4f), new Vector3(1f, -1f, 2f), new Vector3(-1f, -1f, 2f) };
            // These four points are planar only if they lie on a plane; build a planar trapezoid explicitly.
            q = QuadMath.Planarise(q);
            Assert.IsTrue(QuadMath.DiagonalIntersection(q, out var d));
            Assert.IsTrue(QuadMath.WorldToUv(d, q, out var uv));
            Assert.AreEqual(0.5f, uv.x, 1e-3f);
            Assert.AreEqual(0.5f, uv.y, 1e-3f, "the diagonals' intersection is the centre of the picture under the projective map");
            var mid = (q[QuadMath.TL] + q[QuadMath.BL]) * 0.5f;
            Assert.IsTrue(QuadMath.WorldToUv(mid, q, out var m));
            Assert.Greater(Mathf.Abs(m.y - 0.5f), 0.02f, "the world midpoint of the left edge is not the picture mid-height (perspective foreshortening)");
        }

        [Test]
        public void UvToWorld_IsTheInverseOfWorldToUv_ForSkewedQuads()
        {
            var rnd = new System.Random(5);
            for (int k = 0; k < 40; k++)
            {
                // A random convex planar quad: perturb a rectangle's corners within its plane, then tilt the whole thing.
                var q = Rect(1.2f, 0.8f, 0f);
                for (int i = 0; i < 4; i++) q[i] += new Vector3((float)(rnd.NextDouble() - 0.5) * 0.25f, (float)(rnd.NextDouble() - 0.5) * 0.25f, 0f);
                var tilt = Matrix4x4.TRS(new Vector3(0.3f, 1.2f, 2f), Quaternion.Euler((float)rnd.NextDouble() * 80f - 40f, (float)rnd.NextDouble() * 120f - 60f, (float)rnd.NextDouble() * 60f), Vector3.one);
                q = QuadMath.Transform(q, tilt);
                Assert.IsTrue(QuadMath.IsValidQuad(q));
                var uv = new Vector2((float)rnd.NextDouble(), (float)rnd.NextDouble());
                var w = QuadMath.UvToWorld(uv, q);
                Assert.IsTrue(QuadMath.WorldToUv(w, q, out var back));
                Assert.AreEqual(uv.x, back.x, 1e-3f); Assert.AreEqual(uv.y, back.y, 1e-3f);
            }
        }

        [Test]
        public void TiltedScreen_RayHitReportsTheRightPictureCoordinate()
        {
            // A screen lying almost flat in front of the user (a "table"): looking at its far edge hits v near 1.
            var q = QuadMath.Transform(Rect(1f, 0.6f, 0f), Matrix4x4.TRS(new Vector3(0f, 0.9f, 0.8f), Quaternion.Euler(80f, 0f, 0f), Vector3.one));
            Assert.IsTrue(QuadMath.IsValidQuad(q));
            var ray = new Ray(new Vector3(0f, 1.6f, 0f), (QuadMath.UvToWorld(new Vector2(0.25f, 0.8f), q) - new Vector3(0f, 1.6f, 0f)).normalized);
            Assert.IsTrue(QuadMath.RayToUv(ray, q, out var uv, out float dist));
            Assert.AreEqual(0.25f, uv.x, 1e-3f); Assert.AreEqual(0.8f, uv.y, 1e-3f); Assert.Greater(dist, 0.5f);
            Assert.IsFalse(QuadMath.RayToUv(new Ray(new Vector3(0, 1.6f, 0), Vector3.up), q, out _, out _), "a ray pointing away from the screen misses");
        }

        [Test]
        public void ClockwiseOrder_IsCorrectedForTheViewer_SoThePictureIsNotMirrored()
        {
            var viewer = new Vector3(0, 0, 0);
            var cw = Rect();                                     // TL TR BR BL seen from the origin looking +z
            Assert.IsTrue(QuadMath.IsClockwiseFrom(cw, viewer));
            var anticlockwise = new[] { cw[0], cw[3], cw[2], cw[1] };
            Assert.IsFalse(QuadMath.IsClockwiseFrom(anticlockwise, viewer));
            var fixedOrder = QuadMath.OrderForViewer(anticlockwise, viewer);
            Assert.IsTrue(QuadMath.IsClockwiseFrom(fixedOrder, viewer));
            Assert.AreEqual(anticlockwise[0], fixedOrder[0], "the first placed point stays the top-left corner");
            // Viewed from behind, the same four points are "anticlockwise", so the fix flips them: a screen is never mirrored for its viewer.
            Assert.IsFalse(QuadMath.IsClockwiseFrom(cw, new Vector3(0, 0, 5)));
        }

        [Test]
        public void FrontNormal_FacesTheViewerForAClockwiseQuad()
        {
            var q = Rect();
            Assert.Less(QuadMath.FrontNormal(q).z, -0.99f, "a rectangle at z=+2 drawn clockwise from the origin faces back toward the origin (-z)");
        }

        [Test]
        public void Validity_RejectsBowTiesReflexAndTinyQuads()
        {
            var q = Rect();
            Assert.IsTrue(QuadMath.IsValidQuad(q));
            Assert.IsFalse(QuadMath.IsValidQuad(new[] { q[0], q[2], q[1], q[3] }), "bow-tie");
            Assert.IsFalse(QuadMath.IsValidQuad(new[] { q[0], q[1], q[2], new Vector3(0.1f, 0.1f, 2f) }), "reflex corner");
            Assert.IsFalse(QuadMath.IsValidQuad(Rect(0.005f, 0.005f)), "too small");
            Assert.IsFalse(QuadMath.IsValidQuad(new[] { q[0], q[1], q[1], q[3] }), "repeated point");
        }

        [Test]
        public void Planarise_RemovesOutOfPlaneError_AndKeepsTheCentroid()
        {
            var q = Rect();
            q[2].z += 0.08f;
            Assert.Greater(QuadMath.NonPlanarity(q), 0.005f);
            var p = QuadMath.Planarise(q);
            Assert.Less(QuadMath.NonPlanarity(p), 1e-5f);
            Assert.AreEqual(0f, (QuadMath.Centroid(p) - QuadMath.Centroid(q)).magnitude, 1e-5f);
        }

        [Test]
        public void Crop_RoundTripsBetweenPanelAndSource()
        {
            var crop = new Rect(0.6f, 0.05f, 0.3f, 0.2f);                      // a minimap in the top-right of the game
            var src = QuadMath.PanelUvToSource(new Vector2(0f, 1f), crop);        // the panel's top-left
            Assert.AreEqual(0.6f, src.x, 1e-5f); Assert.AreEqual(0.05f, src.y, 1e-5f, "panel top-left is the crop's top-left (y down in the source)");
            var br = QuadMath.PanelUvToSource(new Vector2(1f, 0f), crop);
            Assert.AreEqual(0.9f, br.x, 1e-5f); Assert.AreEqual(0.25f, br.y, 1e-5f);
            var back = QuadMath.SourceToPanelUv(src, crop);
            Assert.AreEqual(0f, back.x, 1e-5f); Assert.AreEqual(1f, back.y, 1e-5f);
        }

        [Test]
        public void CropFromPoints_AcceptsAnyDragDirection_AndClamps()
        {
            var a = QuadMath.CropFromPoints(new Vector2(0.8f, 0.9f), new Vector2(0.3f, 0.2f));
            Assert.AreEqual(0.3f, a.xMin, 1e-5f); Assert.AreEqual(0.8f, a.xMax, 1e-5f); Assert.AreEqual(0.2f, a.yMin, 1e-5f); Assert.AreEqual(0.9f, a.yMax, 1e-5f);
            var b = QuadMath.CropFromPoints(new Vector2(-0.2f, 0.5f), new Vector2(1.4f, 0.5f));
            Assert.AreEqual(0f, b.xMin); Assert.AreEqual(1f, b.xMax); Assert.Greater(b.height, 0.0099f);
        }

        [Test]
        public void NearestCornerToRay_PicksTheClosestWithinRadius()
        {
            var q = Rect();
            var ray = new Ray(Vector3.zero, (q[QuadMath.BR] + new Vector3(0.02f, 0f, 0f)).normalized);
            Assert.AreEqual(QuadMath.BR, QuadMath.NearestCornerToRay(ray, q, 0.1f, out float along));
            Assert.Greater(along, 1f);
            Assert.AreEqual(-1, QuadMath.NearestCornerToRay(new Ray(Vector3.zero, Vector3.up), q, 0.1f, out _));
        }

        [Test]
        public void RotateCorners_TurnsThePictureAQuarter()
        {
            var q = Rect();
            var r = QuadMath.RotateCorners(q, 1);
            Assert.AreEqual(q[1], r[0]); Assert.AreEqual(q[0], r[3]);
            Assert.AreEqual(q[0], QuadMath.RotateCorners(r, 3)[0]);
        }

        [Test]
        public void RectInFront_FacesTheHeadAndHasTheRequestedShape()
        {
            var q = QuadMath.RectInFront(new Vector3(0, 1.6f, 0), Vector3.forward, Vector3.up, 2f, 1.6f, 16f / 9f);
            var size = QuadMath.Size(q);
            Assert.AreEqual(1.6f, size.x, 1e-3f); Assert.AreEqual(0.9f, size.y, 1e-3f);
            Assert.IsTrue(QuadMath.IsClockwiseFrom(q, new Vector3(0, 1.6f, 0)));
            Assert.AreEqual(1.6f, QuadMath.Centroid(q).y, 1e-3f);
        }
    }

    public class LayoutTests
    {
        [Test]
        public void Layout_RoundTripsThroughJson_AndDropsInvalidSurfaces()
        {
            var l = new Layout { appKey = "Some Game.exe" };
            l.sources.Add(new SourceDef { id = "src-1", kind = "window", processName = "SomeGame.exe", titleContains = "Some Game" });
            var good = new SurfaceDef { id = "s1", sourceId = "src-1", corners = new[] { new Vector3(-1, 1, 2), new Vector3(1, 1, 2), new Vector3(1, -1, 2), new Vector3(-1, -1, 2) } };
            var crop = good.Clone(); crop.id = "s2"; crop.crop = new Rect(0.7f, 0.1f, 0.2f, 0.2f);
            var bad = new SurfaceDef { id = "s3", sourceId = "src-1", corners = new Vector3[4] };
            l.surfaces.AddRange(new[] { good, crop, bad });
            var back = Layout.FromJson(l.ToJson());
            Assert.AreEqual(2, back.surfaces.Count, "a surface with degenerate corners is dropped on load");
            Assert.AreEqual(new Rect(0.7f, 0.1f, 0.2f, 0.2f), back.FindSurface("s2").crop);
            Assert.AreEqual("src-1", back.FindSurface("s1").sourceId);
            Assert.AreEqual(good.corners[2], back.FindSurface("s1").corners[2]);
        }

        [Test]
        public void Layout_SavesAtomically_AndLoadsPerApp()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xrss-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var l = new Layout { appKey = "Game: One/Two" };
                l.surfaces.Add(new SurfaceDef { id = "a", corners = new[] { new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 0, 1), new Vector3(0, 0, 1) } });
                l.Save(dir);
                l.surfaces.Add(l.surfaces[0].Clone()); l.surfaces[1].id = "b";
                l.Save(dir);                                              // overwrite
                var back = Layout.Load(dir, "Game: One/Two");
                Assert.AreEqual(2, back.surfaces.Count);
                Assert.IsNull(Layout.Load(dir, "Other"));
                Assert.IsFalse(System.IO.Directory.GetFiles(dir).Any(f => f.EndsWith(".tmp")));
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }

        [Test]
        public void SafeKey_IsFileNameSafe()
        {
            StringAssert.DoesNotContain("/", Layout.SafeKey("a/b:c*d"));
            Assert.AreEqual("default", Layout.SafeKey("  "));
        }
    }
}

namespace XrSpatial.Tests
{
    public class AspectTests
    {
        static UnityEngine.Vector3[] Skewed() => new[]
        {
            new UnityEngine.Vector3(-1f, 1.1f, 2f), new UnityEngine.Vector3(1f, 1.0f, 2f), new UnityEngine.Vector3(1.02f, -0.5f, 2f), new UnityEngine.Vector3(-1.02f, -0.45f, 2f),
        };

        [NUnit.Framework.Test]
        public void FitAspect_MakesARectangleOfTheRequestedShape_InsideTheOriginal_FacingTheSameWay()
        {
            var q = Skewed();
            NUnit.Framework.Assert.IsTrue(XrSpatial.Core.QuadMath.IsNearParallelogram(q));
            var f = XrSpatial.Core.QuadMath.FitAspect(q, 16f / 9f);
            var s = XrSpatial.Core.QuadMath.Size(f);
            NUnit.Framework.Assert.AreEqual(16f / 9f, s.x / s.y, 1e-3f);
            var os = XrSpatial.Core.QuadMath.Size(q);
            NUnit.Framework.Assert.LessOrEqual(s.x, os.x + 1e-3f); NUnit.Framework.Assert.LessOrEqual(s.y, os.y + 1e-3f);
            NUnit.Framework.Assert.IsTrue(XrSpatial.Core.QuadMath.IsValidQuad(f));
            var viewer = UnityEngine.Vector3.zero;
            NUnit.Framework.Assert.IsTrue(XrSpatial.Core.QuadMath.IsClockwiseFrom(f, viewer), "still faces the viewer, not mirrored");
            NUnit.Framework.Assert.Less(UnityEngine.Vector3.Distance(XrSpatial.Core.QuadMath.Centroid(f), XrSpatial.Core.QuadMath.Centroid(q)), 1e-4f);
            // Top edge stays on top.
            NUnit.Framework.Assert.Greater(f[0].y, f[3].y);
        }

        [NUnit.Framework.Test]
        public void FitAspect_WorksOnATiltedScreen_AndGrowMode_KeepsTheLargerSize()
        {
            var tilt = UnityEngine.Matrix4x4.TRS(new UnityEngine.Vector3(0.5f, 0.8f, 1.2f), UnityEngine.Quaternion.Euler(-70f, 20f, 0f), UnityEngine.Vector3.one);
            var q = XrSpatial.Core.QuadMath.Transform(new[] { new UnityEngine.Vector3(-1, 0.4f, 0), new UnityEngine.Vector3(1, 0.4f, 0), new UnityEngine.Vector3(1, -0.4f, 0), new UnityEngine.Vector3(-1, -0.4f, 0) }, tilt);
            var f = XrSpatial.Core.QuadMath.FitAspect(q, 1f, false);
            var s = XrSpatial.Core.QuadMath.Size(f);
            NUnit.Framework.Assert.AreEqual(1f, s.x / s.y, 1e-3f);
            NUnit.Framework.Assert.AreEqual(2f, s.x, 1e-3f, "grow mode keeps the longer dimension");
            NUnit.Framework.Assert.Less(XrSpatial.Core.QuadMath.NonPlanarity(f), 1e-4f);
        }

        [NUnit.Framework.Test]
        public void IsNearParallelogram_RejectsADeliberateTrapezoid()
        {
            var t = new[] { new UnityEngine.Vector3(-0.4f, 1f, 2f), new UnityEngine.Vector3(0.4f, 1f, 2f), new UnityEngine.Vector3(1f, -1f, 2f), new UnityEngine.Vector3(-1f, -1f, 2f) };
            NUnit.Framework.Assert.IsFalse(XrSpatial.Core.QuadMath.IsNearParallelogram(t));
        }
    }
}

namespace XrSpatial.Tests
{
    public class PanelSizingTests
    {
        float m_Cap;
        [SetUp] public void NoCap() { m_Cap = XrSpatial.Core.PanelSizing.SnapCapDegrees; XrSpatial.Core.PanelSizing.SnapCapDegrees = 0f; }
        [TearDown] public void RestoreCap() { XrSpatial.Core.PanelSizing.SnapCapDegrees = m_Cap; }
        static Vector3[] Rect(float w, float h) => XrSpatial.Core.QuadMath.RectAt(new Vector3(0, 1.5f, 2f), Vector3.right, Vector3.up, w, h);

        [Test]
        public void LockedResize_KeepsAspect_AndOppositeCorner()
        {
            var q = Rect(1.6f, 0.9f);
            // drag the bottom-right corner outwards and downwards by awkward amounts
            var t = q[XrSpatial.Core.QuadMath.BR] + new Vector3(0.7f, -0.1f, 0f);
            var r = XrSpatial.Core.PanelSizing.ResizeCornerLocked(q, XrSpatial.Core.QuadMath.BR, t, 16f / 9f);
            var size = XrSpatial.Core.QuadMath.Size(r);
            Assert.AreEqual(16f / 9f, size.x / size.y, 1e-3f);
            Assert.AreEqual(q[XrSpatial.Core.QuadMath.TL].x, r[XrSpatial.Core.QuadMath.TL].x, 1e-4f);   // the anchor (opposite corner) did not move
            Assert.AreEqual(q[XrSpatial.Core.QuadMath.TL].y, r[XrSpatial.Core.QuadMath.TL].y, 1e-4f);
            Assert.Greater(size.x, 1.6f);
        }

        [Test]
        public void LockedResize_CanShrink_AndStillKeepsAspect()
        {
            var q = Rect(1.6f, 0.9f);
            var t = q[XrSpatial.Core.QuadMath.TR] + new Vector3(-0.8f, -0.3f, 0f);
            var r = XrSpatial.Core.PanelSizing.ResizeCornerLocked(q, XrSpatial.Core.QuadMath.TR, t, 16f / 9f);
            var size = XrSpatial.Core.QuadMath.Size(r);
            Assert.AreEqual(16f / 9f, size.x / size.y, 1e-3f);
            Assert.Less(size.x, 1.6f);
        }

        [Test]
        public void Sharpness_PixelsPerDegreeMatchHeadset()
        {
            var s = XrSpatial.Core.PanelSizing.Compute(2880f, 1206f, 22f, 2f);
            Assert.AreEqual(22f, s.sourcePpd, 0.5f);
            Assert.AreEqual(2880f / 1206f, s.widthM / s.heightM, 1e-3f);
        }

        [Test]
        public void Sharpness_SmallCropGivesSmallPanel()
        {
            var full = XrSpatial.Core.PanelSizing.Compute(3440f, 1440f, 22f, 2f);
            var crop = XrSpatial.Core.PanelSizing.Compute(700f, 700f, 22f, 2f);
            Assert.Less(crop.widthM, full.widthM * 0.3f);
            Assert.AreEqual(1f, crop.widthM / crop.heightM, 1e-3f);
        }

        [Test]
        public void Sharpness_CapLimitsAngularWidth()
        {
            float old = XrSpatial.Core.PanelSizing.SnapCapDegrees;
            try
            {
                XrSpatial.Core.PanelSizing.SnapCapDegrees = 75f;
                var s = XrSpatial.Core.PanelSizing.Compute(2880f, 1206f, 22f, 2f);
                Assert.IsTrue(s.capped);
                Assert.AreEqual(75f, s.angWidthDeg, 0.1f);
                XrSpatial.Core.PanelSizing.SnapCapDegrees = 0f;
                Assert.IsFalse(XrSpatial.Core.PanelSizing.Compute(2880f, 1206f, 22f, 2f).capped);
            }
            finally { XrSpatial.Core.PanelSizing.SnapCapDegrees = old; }
        }

        [Test]
        public void WithSize_KeepsCentroidAndOrientation()
        {
            var q = Rect(1.6f, 0.9f);
            var r = XrSpatial.Core.PanelSizing.WithSize(q, 0.8f, 0.45f);
            Assert.AreEqual(0f, Vector3.Distance(XrSpatial.Core.QuadMath.Centroid(q), XrSpatial.Core.QuadMath.Centroid(r)), 1e-4f);
            Assert.AreEqual(0.8f, XrSpatial.Core.QuadMath.Size(r).x, 1e-4f);
        }
    }
}
