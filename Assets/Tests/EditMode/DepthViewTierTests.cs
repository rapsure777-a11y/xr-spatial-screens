using NUnit.Framework;
using UnityEngine;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    public class DepthViewTierTests
    {
        // a 1.6 m x 0.9 m panel centred on (cx, 0, dist), facing the origin
        static Vector3[] Panel(float cx, float dist) => new[]
        {
            new Vector3(cx - 0.8f, 0.45f, dist), new Vector3(cx + 0.8f, 0.45f, dist), new Vector3(cx + 0.8f, -0.45f, dist), new Vector3(cx - 0.8f, -0.45f, dist),
        };

        [Test] public void A_panel_straight_ahead_is_looked_at() => Assert.AreEqual(2, DepthLab.ViewTier(Vector3.zero, Vector3.forward, Panel(0f, 2f)));

        [Test] public void A_panel_well_to_the_side_is_in_view_but_not_looked_at() => Assert.AreEqual(1, DepthLab.ViewTier(Vector3.zero, Vector3.forward, Panel(2.4f, 2f)));

        [Test] public void A_panel_far_to_the_side_is_not_seen() => Assert.AreEqual(0, DepthLab.ViewTier(Vector3.zero, Vector3.forward, Panel(8f, 1f)));

        [Test] public void A_panel_behind_the_head_is_not_seen() => Assert.AreEqual(0, DepthLab.ViewTier(Vector3.zero, Vector3.forward, Panel(0f, -2f)));

        [Test] public void A_big_panel_counts_as_looked_at_when_its_edge_is_where_you_look()
        {
            // centre is 40 degrees away, but its near edge is straight ahead
            var big = new[] { new Vector3(0f, 1f, 2f), new Vector3(4f, 1f, 2f), new Vector3(4f, -1f, 2f), new Vector3(0f, -1f, 2f) };
            Assert.AreEqual(2, DepthLab.ViewTier(Vector3.zero, Vector3.forward, big));
        }

        [Test] public void Turning_the_head_changes_the_tier()
        {
            var p = Panel(3f, 2f);
            Assert.AreEqual(1, DepthLab.ViewTier(Vector3.zero, Vector3.forward, p));
            Assert.AreEqual(2, DepthLab.ViewTier(Vector3.zero, new Vector3(3f, 0f, 2f).normalized, p));
        }

        [Test] public void Missing_corners_fall_back_to_looked_at() => Assert.AreEqual(2, DepthLab.ViewTier(Vector3.zero, Vector3.forward, null));
    }
}
