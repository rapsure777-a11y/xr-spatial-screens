using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Tests
{
    /// <summary>How many pixels of a streamed window a screen needs: stable, geometric, never the tiny values that made screens turn blocky.</summary>
    public class StreamMathTests
    {
        const int Eye = 2700;
        static readonly Vector3 Head = new Vector3(0f, 1.6f, 0f);

        /// <summary>A flat screen straight ahead, <paramref name="width"/> m wide and 1 m tall, <paramref name="distance"/> m away, centred at eye height.</summary>
        static Vector3[] ScreenAhead(float width, float distance, float yaw = 0f)
        {
            var local = new[] { new Vector3(-width / 2, 0.5f, 0), new Vector3(width / 2, 0.5f, 0), new Vector3(width / 2, -0.5f, 0), new Vector3(-width / 2, -0.5f, 0) };
            var rot = Quaternion.Euler(0, yaw, 0);
            var centre = Head + rot * new Vector3(0, 0, distance);
            var c = new Vector3[4];
            for (int i = 0; i < 4; i++) c[i] = centre + rot * local[i];
            return c;
        }

        [Test]
        public void AScreenAheadIsInView_AndNeedsMorePixelsTheWiderItLooks()
        {
            float small = StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(0.6f, 2f), 1f, Eye, 1.5f, out bool inViewSmall);
            float big = StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(2.4f, 2f), 1f, Eye, 1.5f, out bool inViewBig);
            Assert.IsTrue(inViewSmall && inViewBig);
            Assert.Greater(big, small * 2.5f);
        }

        [Test]
        public void AScreenTwoMetresWideAtTwoMetres_NeedsRoughlyTheEyePixelsItCovers()
        {
            // 2 m at 2 m is about 53 degrees; at 2700 px over 105 degrees that is about 1370 eye pixels, 2050 with 50 percent headroom
            float w = StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(2f, 2f), 1f, Eye, 1.5f, out _);
            Assert.That(w, Is.InRange(1900f, 2200f));
        }

        [Test]
        public void TheSameScreenGivesTheSameAnswerWhateverWayTheHeadTurns_ButInViewChanges()
        {
            var screen = ScreenAhead(2f, 2f);
            float ahead = StreamMath.RequiredSourceWidth(Head, Vector3.forward, screen, 1f, Eye, 1.5f, out bool a);
            float turned = StreamMath.RequiredSourceWidth(Head, Quaternion.Euler(0, 60, 0) * Vector3.forward, screen, 1f, Eye, 1.5f, out bool t);
            Assert.AreEqual(ahead, turned, 1f, "the width comes from where the head is, not where it points");
            Assert.IsTrue(a);
            Assert.IsTrue(t, "60 degrees off axis is still inside the margin, so a quick turn finds the screen ready");
        }

        [Test]
        public void AScreenBehindTheHead_IsOutOfView()
        {
            StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(2f, 2f, 180f), 1f, Eye, 1.5f, out bool inView);
            Assert.IsFalse(inView);
        }

        [Test]
        public void ACroppedScreenNeedsMoreOfTheSource()
        {
            float whole = StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(1f, 2f), 1f, Eye, 1.5f, out _);
            float quarter = StreamMath.RequiredSourceWidth(Head, Vector3.forward, ScreenAhead(1f, 2f), 0.25f, Eye, 1.5f, out _);
            Assert.AreEqual(whole * 4f, quarter, whole * 0.01f);
        }

        [Test]
        public void ADegenerateOrEmptyScreen_NeverCrashesAndNeverAsksForANonsenseSize()
        {
            Assert.AreEqual(0f, StreamMath.RequiredSourceWidth(Head, Vector3.forward, new Vector3[0], 1f, Eye, 1.5f, out bool inView));
            Assert.IsFalse(inView);
            var overHead = new[] { new Vector3(0, 5, 0), new Vector3(0, 5, 0), new Vector3(0, 5, 0), new Vector3(0, 5, 0) };
            float w = StreamMath.RequiredSourceWidth(Head, Vector3.forward, overHead, 1f, Eye, 1.5f, out _);
            Assert.IsFalse(float.IsNaN(w) || float.IsInfinity(w));
            Assert.GreaterOrEqual(w, 0f);
        }

        [Test]
        public void Quantize_RoundsUpInStepsAndClamps()
        {
            Assert.AreEqual(1280, StreamMath.Quantize(10f, 160, 1280, 3840));
            Assert.AreEqual(1440, StreamMath.Quantize(1281f, 160, 1280, 3840));
            Assert.AreEqual(3840, StreamMath.Quantize(99999f, 160, 1280, 3840));
        }
    }
}
