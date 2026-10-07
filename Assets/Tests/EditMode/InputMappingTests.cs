using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>The pure parts of input forwarding (the live parts are covered by Tools/test-click.ps1 against a real window).</summary>
    public class InputMappingTests
    {
        static Vector3[] Rect(float w, float h) => new[] { new Vector3(-w / 2, h / 2, 2), new Vector3(w / 2, h / 2, 2), new Vector3(w / 2, -h / 2, 2), new Vector3(-w / 2, -h / 2, 2) };

        [Test]
        public void MinimapExample_CentreOfTheCroppedPanelIsThePointAt1700x850()
        {
            // A 1920x1080 window, minimap cropped at x 1500..1900, y 700..1000.
            var crop = new Rect(1500f / 1920f, 700f / 1080f, 400f / 1920f, 300f / 1080f);
            var src = QuadMath.PanelUvToSource(new Vector2(0.5f, 0.5f), crop);
            Assert.AreEqual(1700f, src.x * 1920f, 0.5f);
            Assert.AreEqual(850f, src.y * 1080f, 0.5f);
            var px = InputForwarder.SourceToDesktop(src, 100, 50, 1920, 1080);        // window placed at desktop (100, 50)
            Assert.AreEqual(100 + 1700, px.x, 1); Assert.AreEqual(50 + 850, px.y, 1);
        }

        [Test]
        public void NegativeDesktopOrigin_MapsToTheRightAbsoluteCoordinates()
        {
            // Virtual desktop: a monitor to the left (origin -1920) and one above (origin -1080); total 5360 x 2520.
            var left = InputForwarder.DesktopToAbsolute(new Vector2Int(-1920, -1080), -1920, -1080, 5360, 2520);
            Assert.AreEqual(0, left.x); Assert.AreEqual(0, left.y);
            var right = InputForwarder.DesktopToAbsolute(new Vector2Int(5359 - 1920, 2519 - 1080), -1920, -1080, 5360, 2520);
            Assert.AreEqual(65535, right.x); Assert.AreEqual(65535, right.y);
            var origin = InputForwarder.DesktopToAbsolute(Vector2Int.zero, -1920, -1080, 5360, 2520);
            Assert.AreEqual(1920f / 5359f * 65535f, origin.x, 1f); Assert.AreEqual(1080f / 2519f * 65535f, origin.y, 1f);
            // A window on the left monitor at negative coordinates.
            var px = InputForwarder.SourceToDesktop(new Vector2(0.5f, 0.5f), -1700, 100, 1000, 600);
            Assert.AreEqual(-1200, px.x, 1); Assert.AreEqual(400, px.y, 1);
        }

        [Test]
        public void Edges_NeverLeaveTheWindowRectangle()
        {
            var a = InputForwarder.SourceToDesktop(new Vector2(-0.2f, 1.4f), 200, 100, 800, 600);
            Assert.AreEqual(200, a.x); Assert.AreEqual(699, a.y);
            var b = InputForwarder.SourceToDesktop(new Vector2(1f, 0f), 200, 100, 800, 600);
            Assert.AreEqual(999, b.x); Assert.AreEqual(100, b.y);
        }

        [Test]
        public void DeadZone_HoldsThePressPoint_UntilTheLaserMovesFarEnough()
        {
            bool dragging = false;
            var press = new Vector2Int(500, 400);
            Assert.AreEqual(press, InputForwarder.ApplyDeadZone(press, new Vector2Int(503, 402), 6, ref dragging));
            Assert.IsFalse(dragging);
            var far = new Vector2Int(520, 410);
            Assert.AreEqual(far, InputForwarder.ApplyDeadZone(press, far, 6, ref dragging));
            Assert.IsTrue(dragging);
            // Once dragging, coming back near the start follows the laser (no sticking).
            var back = new Vector2Int(502, 401);
            Assert.AreEqual(back, InputForwarder.ApplyDeadZone(press, back, 6, ref dragging));
        }

        [Test]
        public void SkewedQuad_RayThroughASourcePoint_ComesBackToThatSourcePoint()
        {
            // An edited (non-rectangular) screen at an angle, cropped: a ray through the world point of a source position must report that same source position.
            var q = new[] { new Vector3(-1.0f, 0.8f, 2.0f), new Vector3(0.9f, 0.6f, 2.6f), new Vector3(0.9f, -0.5f, 2.6f), new Vector3(-1.0f, -0.7f, 2.0f) };
            var crop = new Rect(0.55f, 0.35f, 0.4f, 0.5f);
            foreach (var s in new[] { new Vector2(0.6f, 0.4f), new Vector2(0.75f, 0.6f), new Vector2(0.93f, 0.82f) })
            {
                var world = QuadMath.UvToWorld(QuadMath.SourceToPanelUv(s, crop), q);
                var ray = new Ray(new Vector3(0.1f, 0.2f, -0.5f), (world - new Vector3(0.1f, 0.2f, -0.5f)).normalized);
                Assert.IsTrue(QuadMath.RayToUv(ray, q, out var uv, out _));
                var back = QuadMath.PanelUvToSource(uv, crop);
                Assert.AreEqual(s.x, back.x, 1e-3f); Assert.AreEqual(s.y, back.y, 1e-3f);
            }
        }
    }
}
