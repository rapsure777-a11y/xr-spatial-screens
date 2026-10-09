using System.Collections.Generic;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// Panel sizing experiment (Lock Aspect + Snap Sharpness). Pure maths on corner arrays; touches nothing else.
    /// Corner order is QuadMath's: TL, TR, BR, BL.
    /// </summary>
    public static class PanelSizing
    {
        /// <summary>The source pixels per degree to aim for, as a multiple of the headset's display pixels per degree. 1 = one source pixel per display pixel (no enlarging).</summary>
        public const float SourcePerDisplay = 1.0f;
        public const float MinWidth = 0.10f, MaxWidth = 6f;

        /// <summary>The panel's right and up directions (unit), from its own edges, as QuadMath.FitAspect derives them.</summary>
        public static void Axes(IList<Vector3> q, out Vector3 right, out Vector3 up)
        {
            right = ((q[QuadMath.TR] - q[QuadMath.TL]) + (q[QuadMath.BR] - q[QuadMath.BL])).normalized;
            Vector3 n = QuadMath.FrontNormal(q);
            up = Vector3.Cross(n, right);
            if (Vector3.Dot(up, (q[QuadMath.TL] - q[QuadMath.BL]) + (q[QuadMath.TR] - q[QuadMath.BR])) < 0f) up = -up;
            up = up.normalized;
        }

        /// <summary>
        /// Drag of one corner with the aspect (width / height) kept: the opposite corner stays where it is and the dragged corner follows the pointer
        /// as closely as the aspect allows (the larger of the two pulls decides the size). The result is a rectangle in the panel's own plane.
        /// </summary>
        public static Vector3[] ResizeCornerLocked(IList<Vector3> start, int corner, Vector3 target, float aspect)
        {
            aspect = Mathf.Max(0.05f, aspect);
            Axes(start, out var r, out var up);
            Vector3 anchor = start[(corner + 2) % 4];
            Vector3 d = target - anchor;
            float du = Vector3.Dot(d, r), dv = Vector3.Dot(d, up);
            float w = Mathf.Max(Mathf.Abs(du), Mathf.Abs(dv) * aspect), h = w / aspect;
            float su = du >= 0f ? 1f : -1f, sv = dv >= 0f ? 1f : -1f;
            float u0 = Mathf.Min(0f, su * w), u1 = Mathf.Max(0f, su * w), v0 = Mathf.Min(0f, sv * h), v1 = Mathf.Max(0f, sv * h);
            Vector3 P(float u, float v) => anchor + r * u + up * v;
            var q = new Vector3[4];
            q[QuadMath.TL] = P(u0, v1); q[QuadMath.TR] = P(u1, v1); q[QuadMath.BR] = P(u1, v0); q[QuadMath.BL] = P(u0, v0);
            return q;
        }

        /// <summary>A rectangle of the given size in the quad's plane, about the quad's centroid, with the quad's orientation.</summary>
        public static Vector3[] WithSize(IList<Vector3> q, float width, float height)
        {
            Axes(q, out var r, out var up);
            return QuadMath.RectAt(QuadMath.Centroid(q), r, up, width, height);
        }

        /// <summary>Source pixels per degree at the middle of a flat panel of the given width, seen face-on from the given distance.</summary>
        public static float CentrePpd(float pxW, float widthM, float distance) => pxW / Mathf.Max(0.001f, widthM / Mathf.Max(0.2f, distance)) * Mathf.Deg2Rad;

        public struct Sharpness
        {
            public float widthM, heightM, angWidthDeg, angHeightDeg, sourcePpd, headsetPpd;
            public bool clamped;
        }

        /// <summary>
        /// The panel size at which the picture's pixels across the panel match the headset's display pixels per degree (times <see cref="SourcePerDisplay"/>),
        /// for a panel flat-on at <paramref name="distance"/> metres. <paramref name="pxW"/> and <paramref name="pxH"/> are the effective (cropped) source pixels.
        /// </summary>
        public static Sharpness Compute(float pxW, float pxH, float headsetPpd, float distance)
        {
            var s = new Sharpness { headsetPpd = headsetPpd };
            pxW = Mathf.Max(1f, pxW); pxH = Mathf.Max(1f, pxH); distance = Mathf.Max(0.2f, distance);
            // matched at the panel's centre, where a flat panel face-on and the headset's flat-projected pixels agree: source pixels per unit of tan(angle) = display pixels per unit of tan(angle)
            float w = distance * pxW / (Mathf.Max(1f, headsetPpd) * SourcePerDisplay * Mathf.Rad2Deg);
            float cl = Mathf.Clamp(w, MinWidth, MaxWidth);
            s.clamped = !Mathf.Approximately(cl, w); w = cl;
            float h = w * pxH / pxW;
            s.widthM = w; s.heightM = h;
            s.angWidthDeg = 2f * Mathf.Atan(w * 0.5f / distance) * Mathf.Rad2Deg;
            s.angHeightDeg = 2f * Mathf.Atan(h * 0.5f / distance) * Mathf.Rad2Deg;
            s.sourcePpd = CentrePpd(pxW, w, distance);
            return s;
        }
    }
}
