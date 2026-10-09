using System.Collections.Generic;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// Edge tilt: grab the top or bottom edge of a panel and pull it towards or away from you to pitch the panel about its horizontal axis (through its centre).
    /// Pure maths; the interaction lives in SurfaceTool. Corner order is QuadMath's: TL, TR, BR, BL.
    /// </summary>
    public static class PanelTilt
    {
        public const float MaxTiltDegrees = 60f;
        /// <summary>Hand travel (towards or away from the body) that tips the edge the whole way; small panels use this minimum too, so tilt feels the same on every panel.</summary>
        public const float MinReferenceLength = 0.25f;
        public const float DeadZoneMetres = 0.005f;
        const float EdgeBandMetres = 0.05f, CornerKeepOut = 0.12f;

        /// <summary>+1 when the panel-space hit (u right, v up, both 0..1) is in the top tilt zone, -1 for the bottom, 0 elsewhere. The zones leave the corners free (corners keep their own handles).</summary>
        public static int ZoneAt(Vector2 uv, float panelHeightMetres)
        {
            if (uv.x < CornerKeepOut || uv.x > 1f - CornerKeepOut) return 0;
            float band = Mathf.Clamp(EdgeBandMetres / Mathf.Max(0.05f, panelHeightMetres), 0.06f, 0.25f);
            if (uv.y > 1f - band && uv.y <= 1f) return 1;
            if (uv.y < band && uv.y >= 0f) return -1;
            return 0;
        }

        /// <summary>
        /// The tilt angle (radians, positive = top edge towards the viewer) for a hand that has moved <paramref name="pull"/> metres towards the viewer along the panel's
        /// front normal since the grab. Grabbing the bottom edge reverses the relationship (pulling the bottom towards you tips the top away).
        /// </summary>
        public static float AngleFromPull(float pull, int edge, float halfHeight)
        {
            if (Mathf.Abs(pull) < DeadZoneMetres) pull = 0f; else pull -= Mathf.Sign(pull) * DeadZoneMetres;
            float reference = Mathf.Max(halfHeight, MinReferenceLength);
            float a = Mathf.Asin(Mathf.Clamp(pull / reference, -1f, 1f)) * (edge < 0 ? -1f : 1f);
            float max = MaxTiltDegrees * Mathf.Deg2Rad;
            return Mathf.Clamp(a, -max, max);
        }

        /// <summary>The corners tilted by <paramref name="angle"/> radians about the horizontal axis through the centroid (positive = top edge towards the viewer).</summary>
        public static Vector3[] Rotate(IList<Vector3> start, float angle)
        {
            PanelSizing.Axes(start, out var r, out var up);
            Vector3 n = QuadMath.FrontNormal(start);
            Vector3 c = QuadMath.Centroid(start);
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            var q = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                Vector3 o = start[i] - c;
                float x = Vector3.Dot(o, r), y = Vector3.Dot(o, up), z = Vector3.Dot(o, n);
                q[i] = c + r * x + up * (y * cos - z * sin) + n * (y * sin + z * cos);
            }
            return q;
        }
    }
}
