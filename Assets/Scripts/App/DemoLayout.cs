using UnityEngine;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// A showcase arrangement of one source: upright, sideways, a near-horizontal "desk" screen, a diagonal one, a trapezoid, and a cropped minimap. It exists to judge how
    /// convincing a flat capture looks when freely angled in 3D (the point of the product) and doubles as a regression screenshot (`--xrss-demo`).
    /// </summary>
    public static class DemoLayout
    {
        /// <summary>Corners (TL, TR, BR, BL) of a width x height rectangle centred at <paramref name="centre"/>, turned by yaw (about Y), pitch (about its own X) and roll (about its normal).</summary>
        public static Vector3[] Rect(Vector3 centre, float yaw, float pitch, float roll, float width, float height)
        {
            // Local frame: right = +x, up = +y, the front faces -z (toward a viewer at the origin looking down +z).
            var rot = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(pitch, 0f, 0f) * Quaternion.Euler(0f, 0f, roll);
            var q = QuadMath.RectAt(Vector3.zero, Vector3.right, Vector3.up, width, height);
            for (int i = 0; i < 4; i++) q[i] = centre + rot * q[i];
            return q;
        }

        public static void Build(SpatialWorkspace ws, string sourceId, float aspect, Vector3 head)
        {
            float h = 1f / aspect;
            Vector3 P(float yawDeg, float dist, float y) => head + Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward * dist + Vector3.up * (y - head.y);
            // Main: upright straight ahead.
            var main = ws.AddSurface(sourceId, Rect(P(0f, 2.6f, 1.55f), 0f, 0f, 0f, 2.2f, 2.2f * h), null, "main");
            // Desk: lying almost flat in front of the knees, tilted toward the user.
            ws.AddSurface(sourceId, Rect(P(0f, 0.85f, 0.78f), 0f, -72f, 0f, 1.0f, 1.0f * h), null, "desk");
            // Left: turned to face the user, leaning back a little.
            ws.AddSurface(sourceId, Rect(P(-55f, 2.2f, 1.7f), -55f, -8f, 0f, 1.5f, 1.5f * h), null, "left");
            // Diagonal: rolled 25 degrees and yawed, up on the right.
            ws.AddSurface(sourceId, Rect(P(50f, 2.0f, 2.1f), 50f, 12f, -25f, 1.3f, 1.3f * h), null, "diagonal");
            // A trapezoid: the user drew four points that are not a rectangle (the far edge shorter), still showing an undistorted picture in perspective.
            var trap = Rect(P(28f, 1.4f, 0.95f), 28f, -35f, 0f, 1.2f, 1.2f * h);
            trap[QuadMath.TL] += (trap[QuadMath.TR] - trap[QuadMath.TL]) * 0.18f;
            trap[QuadMath.TR] -= (trap[QuadMath.TR] - trap[QuadMath.TL]) * 0.2f;
            ws.AddSurface(sourceId, QuadMath.Planarise(trap), null, "trapezoid");
            // Minimap: a crop of the top-right of the source, close and upright to the left of centre.
            var crop = new Rect(0.7f, 0.1f, 0.2f, 0.2f);
            float cropAspect = crop.width * aspect / crop.height;
            ws.AddSurface(sourceId, Rect(P(-22f, 1.5f, 1.15f), -22f, 0f, 0f, 0.7f, 0.7f / cropAspect), crop, "minimap");
        }
    }
}
