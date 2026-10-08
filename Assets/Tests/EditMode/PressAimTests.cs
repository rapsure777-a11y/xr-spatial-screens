using NUnit.Framework;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Tests
{
    /// <summary>A click lands where the laser was just before the trigger pull moved it, but only for a small nudge.</summary>
    public class PressAimTests
    {
        [Test]
        public void ASmallNudgeDuringTheTriggerPull_ClicksWhereTheLaserWasBefore()
        {
            var aim = new PressAim();
            for (int i = 0; i < 20; i++) aim.Record(i * 0.01f, new Vector2(0.50f, 0.50f), 7);      // steady for 0.2 s
            for (int i = 20; i < 26; i++) aim.Record(i * 0.01f, new Vector2(0.50f + (i - 19) * 0.003f, 0.50f), 7);   // the pull drifts it sideways
            var chosen = aim.Choose(0.26f, new Vector2(0.518f, 0.50f), 7);
            Assert.AreEqual(0.50f, chosen.x, 0.0035f, "back to about where it rested 70 ms ago");
        }

        [Test]
        public void ADeliberateMove_ClicksWhereTheLaserIsNow()
        {
            var aim = new PressAim();
            for (int i = 0; i < 30; i++) aim.Record(i * 0.01f, new Vector2(0.10f + i * 0.02f, 0.5f), 7);   // sweeping fast across the screen
            var now = new Vector2(0.10f + 29 * 0.02f, 0.5f);
            Assert.AreEqual(now, aim.Choose(0.30f, now, 7));
        }

        [Test]
        public void NoHistory_OrAnotherScreen_ClicksWhereTheLaserIsNow()
        {
            var aim = new PressAim();
            var now = new Vector2(0.3f, 0.3f);
            Assert.AreEqual(now, aim.Choose(1f, now, 7), "nothing remembered");
            for (int i = 0; i < 20; i++) aim.Record(i * 0.01f, new Vector2(0.3f, 0.3f), 9);
            Assert.AreEqual(now, aim.Choose(0.3f, now, 7), "the earlier samples were on another screen");
        }

        [Test]
        public void SamplesNewerThanTheLookbackAreIgnored()
        {
            var aim = new PressAim();
            aim.Record(0.00f, new Vector2(0.40f, 0.4f), 7);
            aim.Record(0.05f, new Vector2(0.41f, 0.4f), 7);
            aim.Record(0.09f, new Vector2(0.42f, 0.4f), 7);
            var chosen = aim.Choose(0.10f, new Vector2(0.415f, 0.4f), 7);          // only the 0.00 sample is 70 ms old, and it is within the nudge limit
            Assert.AreEqual(0.40f, chosen.x, 1e-4f);
        }
    }
}
