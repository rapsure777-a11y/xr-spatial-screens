using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Tests
{
    public class CursorMathTests
    {
        [Test]
        public void SourceToPanel_WholeSourceMapsStraightThrough_VIsUp()
        {
            var whole = new Rect(0, 0, 1, 1);
            Assert.IsTrue(CursorMath.SourceToPanel(new Vector2(0.25f, 0.2f), whole, out var uv));
            Assert.AreEqual(0.25f, uv.x, 1e-5f);
            Assert.AreEqual(0.8f, uv.y, 1e-5f);                       // source y is down, panel v is up
        }

        [Test]
        public void SourceToPanel_CropShowsTheCursorOnlyInsideAndRelativeToTheCrop()
        {
            var crop = new Rect(0.5f, 0.25f, 0.4f, 0.5f);
            Assert.IsTrue(CursorMath.SourceToPanel(new Vector2(0.7f, 0.5f), crop, out var uv));
            Assert.AreEqual(0.5f, uv.x, 1e-5f);
            Assert.AreEqual(0.5f, uv.y, 1e-5f);
            Assert.IsFalse(CursorMath.SourceToPanel(new Vector2(0.3f, 0.5f), crop, out _), "left of the crop");
            Assert.IsFalse(CursorMath.SourceToPanel(new Vector2(0.7f, 0.9f), crop, out _), "below the crop");
        }

        [Test]
        public void SourceToPanel_InAnSbsModeOnlyTheEyePictureIsShown_SameRectAsControllerInput()
        {
            var crop = new Rect(0f, 0f, 1f, 1f);
            var eye = StereoMath.InputRect(StereoMode.SbsHalf, false, crop);
            Assert.IsTrue(CursorMath.SourceToPanel(new Vector2(0.25f, 0.5f), eye, out var uv));
            Assert.AreEqual(0.5f, uv.x, 1e-5f);
            Assert.IsFalse(CursorMath.SourceToPanel(new Vector2(0.75f, 0.5f), eye, out _), "the other eye's half is not drawn twice");
            var swapped = StereoMath.InputRect(StereoMode.SbsHalf, true, crop);
            Assert.IsTrue(CursorMath.SourceToPanel(new Vector2(0.75f, 0.5f), swapped, out uv));
            Assert.AreEqual(0.5f, uv.x, 1e-5f);
        }

        [Test]
        public void SourceToPanel_RoundTripsThroughTheControllerMapping()
        {
            // the controller turns a panel uv into a source position with PanelUvToSource; the cursor must turn that source position back into the same uv
            var rect = new Rect(0.1f, 0.2f, 0.6f, 0.5f);
            foreach (var uv in new[] { new Vector2(0.1f, 0.9f), new Vector2(0.5f, 0.5f), new Vector2(0.99f, 0.01f) })
            {
                var src = QuadMath.PanelUvToSource(uv, rect);
                Assert.IsTrue(CursorMath.SourceToPanel(src, rect, out var back));
                Assert.AreEqual(uv.x, back.x, 1e-4f); Assert.AreEqual(uv.y, back.y, 1e-4f);
            }
        }

        [Test]
        public void PixelToWindowUv_IsTheExactInverseOfWhatTheInputInjectorDoes()
        {
            // the injector turns (u, v) into the pixel left + clamp(round(u * w), 0, w - 1); a cursor on that pixel must report the u that produces it
            const int left = 37, top = -12, w = 811, h = 457;
            for (int dx = 0; dx < w; dx += 13)
            {
                Assert.IsTrue(CursorMath.PixelToWindowUv(left + dx, top + 5, left, top, w, h, out float u, out _));
                Assert.AreEqual(dx, Mathf.Clamp(Mathf.RoundToInt(u * w), 0, w - 1), "x " + dx);
            }
            for (int dy = 0; dy < h; dy += 11)
            {
                Assert.IsTrue(CursorMath.PixelToWindowUv(left + 5, top + dy, left, top, w, h, out _, out float v));
                Assert.AreEqual(dy, Mathf.Clamp(Mathf.RoundToInt(v * h), 0, h - 1), "y " + dy);
            }
        }

        [Test]
        public void PixelToWindowUv_EdgesAndOutside()
        {
            Assert.IsTrue(CursorMath.PixelToWindowUv(100, 100, 100, 100, 1000, 500, out float u, out float v));
            Assert.AreEqual(0f, u); Assert.AreEqual(0f, v);
            Assert.IsTrue(CursorMath.PixelToWindowUv(1099, 599, 100, 100, 1000, 500, out u, out v));
            Assert.AreEqual(0.999f, u, 1e-5f); Assert.AreEqual(0.998f, v, 1e-5f);
            Assert.IsFalse(CursorMath.PixelToWindowUv(1100, 300, 100, 100, 1000, 500, out _, out _), "right edge is outside");
            Assert.IsFalse(CursorMath.PixelToWindowUv(99, 300, 100, 100, 1000, 500, out _, out _));
            Assert.IsFalse(CursorMath.PixelToWindowUv(500, 300, 100, 100, 0, 500, out _, out _), "an empty window has no inside");
        }

        [Test]
        public void Height_IsAFixedAngle_GrowsWithDistance_HighIsLargerButNotHuge()
        {
            float n1 = CursorMath.Height(1f, PcCursorMode.Normal), n2 = CursorMath.Height(2f, PcCursorMode.Normal), h1 = CursorMath.Height(1f, PcCursorMode.High);
            Assert.AreEqual(2f, n2 / n1, 1e-3f);
            Assert.Greater(h1, n1);
            Assert.Less(h1 / n1, 1.7f, "High Visibility is not giant");
            Assert.That(n1, Is.InRange(0.02f, 0.05f), "about 3 cm at 1 m");
        }

        [Test]
        public void ModeCycle_IsOffNormalHighOff()
        {
            Assert.AreEqual(PcCursorMode.Normal, CursorMath.Next(PcCursorMode.Off));
            Assert.AreEqual(PcCursorMode.High, CursorMath.Next(PcCursorMode.Normal));
            Assert.AreEqual(PcCursorMode.Off, CursorMath.Next(PcCursorMode.High));
            Assert.AreEqual("Off", CursorMath.Name(PcCursorMode.Off));
        }

        [Test]
        public void Art_EveryShapeHasPixels_ArrowPointsUpLeft_AndTheHaloOnlySurroundsTheShape()
        {
            var px = CursorArt.Render();
            Assert.AreEqual(CursorArt.Width * CursorArt.Height * 4, px.Length);
            for (int cell = 0; cell < CursorArt.ShapeCount; cell++)
            {
                int ox = (cell % CursorArt.Columns) * CursorArt.Cell, oy = (cell / CursorArt.Columns) * CursorArt.Cell, fill = 0;
                for (int y = 0; y < CursorArt.Cell; y++) for (int x = 0; x < CursorArt.Cell; x++) if (px[((oy + y) * CursorArt.Width + ox + x) * 4] > 128) fill++;
                Assert.Greater(fill, 80, "shape " + cell + " is drawn");
                Assert.Less(fill, CursorArt.Cell * CursorArt.Cell / 2, "shape " + cell + " is not a filled square");
            }
            // arrow: its tip is at the top-left (texture row 0 is the bottom, so the shape's top is the highest row)
            int R(int cx, int cyFromTop) => px[((CursorArt.Cell - 1 - cyFromTop) * CursorArt.Width + cx) * 4];
            Assert.Greater(R(6, 12), 128, "just under the tip is filled");
            Assert.Less(R(50, 6), 40, "the top-right of the arrow cell is empty");
            Assert.Less(R(6, 60), 40, "the bottom-left of the arrow cell is empty");
        }

        [Test]
        public void Art_HotspotsAndCellsAreSane()
        {
            Assert.AreEqual(0, CursorArt.CellOf(CursorMath.ShapeOther), "a game's own cursor shows as the arrow");
            Assert.AreEqual(CursorMath.ShapeIBeam, CursorArt.CellOf(CursorMath.ShapeIBeam));
            Assert.AreEqual(new Vector2(0.5f, 0.5f), CursorArt.Hotspot(CursorMath.ShapeIBeam));
            var tip = CursorArt.Hotspot(CursorMath.ShapeArrow);
            Assert.Less(tip.x, 0.15f); Assert.Less(tip.y, 0.15f);
            var rect = CursorArt.CellRect(5);
            Assert.AreEqual(0.25f, rect.x, 1e-5f); Assert.AreEqual(1f / 3f, rect.y, 1e-5f);
        }
    }
}
