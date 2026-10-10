using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Tests
{
    public class StereoMathTests
    {
        const float Eps = 1e-5f;

        [Test]
        public void DefaultEyes_LeftEyeReadsTheLeftHalf_RightEyeTheRightHalf()
        {
            StereoMath.EyeWindow(0, false, 0f, out float lx, out float lw);
            StereoMath.EyeWindow(1, false, 0f, out float rx, out float rw);
            Assert.AreEqual(0f, lx, Eps); Assert.AreEqual(0.5f, lw, Eps);
            Assert.AreEqual(0.5f, rx, Eps); Assert.AreEqual(0.5f, rw, Eps);
        }

        [Test]
        public void SwapEyes_ExchangesTheHalves()
        {
            StereoMath.EyeWindow(0, true, 0f, out float lx, out _);
            StereoMath.EyeWindow(1, true, 0f, out float rx, out _);
            Assert.AreEqual(0.5f, lx, Eps);
            Assert.AreEqual(0f, rx, Eps);
        }

        [Test]
        public void PositiveOffset_ShiftsEachPictureTowardTheOtherSide_NegativeTheOpposite()
        {
            // positive = nearer: left picture moves right (its sampling window moves left), right picture moves left (window moves right)
            StereoMath.EyeWindow(0, false, 0.01f, out float lx, out _);
            StereoMath.EyeWindow(1, false, 0.01f, out float rx, out _);
            Assert.Less(lx, 0f); Assert.Greater(rx, 0.5f);
            StereoMath.EyeWindow(0, false, -0.01f, out lx, out _);
            StereoMath.EyeWindow(1, false, -0.01f, out rx, out _);
            Assert.Greater(lx, 0f); Assert.Less(rx, 0.5f);
        }

        [Test]
        public void Offset_IsClampedToTheComfortableRange_AndNaNIsZero()
        {
            Assert.AreEqual(StereoMath.MaxOffset, StereoMath.ClampOffset(5f), Eps);
            Assert.AreEqual(-StereoMath.MaxOffset, StereoMath.ClampOffset(-5f), Eps);
            Assert.AreEqual(0f, StereoMath.ClampOffset(float.NaN), Eps);
            StereoMath.EyeWindow(0, false, 5f, out float a, out _);
            StereoMath.EyeWindow(0, false, StereoMath.MaxOffset, out float b, out _);
            Assert.AreEqual(b, a, Eps);
            Assert.LessOrEqual(StereoMath.MaxOffset, 0.01f, "the offset limit is a comfort limit: do not raise it casually");
        }

        [Test]
        public void StepOffset_StepsBothWays_StaysInRange_AndComesBackToExactlyZero()
        {
            float o = 0f;
            for (int i = 0; i < 10; i++) o = StereoMath.StepOffset(o, 1);
            Assert.AreEqual(StereoMath.MaxOffset, o, Eps);
            for (int i = 0; i < 4; i++) o = StereoMath.StepOffset(o, -1);
            Assert.AreEqual(0f, o);                                   // exactly zero, no float noise
            for (int i = 0; i < 10; i++) o = StereoMath.StepOffset(o, -1);
            Assert.AreEqual(-StereoMath.MaxOffset, o, Eps);
        }

        [Test]
        public void PanelAspect_HalfSbsUnsqueezes_FullSbsHalvesTheSourceWidth()
        {
            float wide = 1920f / 1080f;
            Assert.AreEqual(16f / 9f, StereoMath.PanelAspect(StereoMode.SbsHalf, 1920f / 1080f), 1e-4f);   // 1920x1080 half-SBS = one 16:9 picture per eye
            Assert.AreEqual(16f / 9f, StereoMath.PanelAspect(StereoMode.SbsFull, 3840f / 1080f), 1e-4f);   // 3840x1080 full-SBS = one 16:9 picture per eye
            Assert.AreEqual(wide, StereoMath.PanelAspect(StereoMode.Flat, wide), 1e-6f);
        }

        [Test]
        public void ModeCycle_IsFlatHalfFullFlat()
        {
            Assert.AreEqual(StereoMode.SbsHalf, StereoMath.Next(StereoMode.Flat));
            Assert.AreEqual(StereoMode.SbsFull, StereoMath.Next(StereoMode.SbsHalf));
            Assert.AreEqual(StereoMode.Flat, StereoMath.Next(StereoMode.SbsFull));
            Assert.IsFalse(StereoMath.IsStereo(StereoMode.Flat));
        }

        [Test]
        public void InputRect_IsTheWholeSourceWhenFlat_AndOneEyeHalfWhenStereo()
        {
            Assert.AreEqual(new Rect(0, 0, 1, 1), StereoMath.InputRect(StereoMode.Flat, false));
            Assert.AreEqual(new Rect(0, 0, 0.5f, 1), StereoMath.InputRect(StereoMode.SbsHalf, false));
            Assert.AreEqual(new Rect(0.5f, 0, 0.5f, 1), StereoMath.InputRect(StereoMode.SbsFull, true));
        }

        [Test]
        public void ShaderParams_MatchTheEyeWindows()
        {
            var v = StereoMath.ShaderParams(false, 0f);
            Assert.AreEqual(new Vector4(0f, 0.5f, 0f, 0.5f), v);
            var s = StereoMath.ShaderParams(true, 0f);
            Assert.AreEqual(new Vector4(0.5f, 0f, 0.5f, 0f), s);
        }

        [Test]
        public void OldLayoutFilesWithoutStereoFields_LoadAsFlat_AndSettingsSurviveASaveAndLoad()
        {
            var made = new Layout();
            made.surfaces.Add(new SurfaceDef
            {
                id = "s1", sourceId = "a", label = "L", crop = new Rect(0.1f, 0.2f, 0.5f, 0.5f),
                corners = new[] { new Vector3(-1, 1, 2), new Vector3(1, 1, 2), new Vector3(1, -1, 2), new Vector3(-1, -1, 2) },
            });
            // a file written before SBS existed has none of the three new fields (remove each with its leading comma)
            string old = System.Text.RegularExpressions.Regex.Replace(made.ToJson(), @",\s*""(stereo|swapEyes|stereoOffset)"":\s*[^,\r\n}]+", "");
            StringAssert.DoesNotContain("stereo", old);
            var l = Layout.FromJson(old);
            Assert.AreEqual(StereoMode.Flat, l.surfaces[0].stereo);
            Assert.IsFalse(l.surfaces[0].swapEyes);
            Assert.AreEqual(0f, l.surfaces[0].stereoOffset);
            Assert.AreEqual(0.1f, l.surfaces[0].crop.x, Eps);                       // the crop is untouched
            l.surfaces[0].stereo = StereoMode.SbsFull; l.surfaces[0].swapEyes = true; l.surfaces[0].stereoOffset = 0.005f;
            var back = Layout.FromJson(l.ToJson());
            Assert.AreEqual(StereoMode.SbsFull, back.surfaces[0].stereo);
            Assert.IsTrue(back.surfaces[0].swapEyes);
            Assert.AreEqual(0.005f, back.surfaces[0].stereoOffset, Eps);
            Assert.AreEqual(0.1f, back.surfaces[0].crop.x, Eps);
            var clone = back.surfaces[0].Clone();
            Assert.AreEqual(StereoMode.SbsFull, clone.stereo); Assert.IsTrue(clone.swapEyes); Assert.AreEqual(0.005f, clone.stereoOffset, Eps);
        }
    }
}
