using System;
using System.Collections.Generic;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// Geometry for a screen defined by four points placed in space. Corner order is always
    /// 0 = top-left, 1 = top-right, 2 = bottom-right, 3 = bottom-left of the image (clockwise as seen from the front).
    ///
    /// The four points need not form a rectangle or a parallelogram: any planar convex quad is a valid screen. The image is mapped with the
    /// projective (perspective-correct) map that takes the quad's corners to the unit square, so a quad that is the image of a rectangle seen
    /// at an angle shows the picture undistorted, and a trapezoid shows a correct perspective picture rather than an affine shear.
    /// Pure math, no scene dependencies: everything here is covered by EditMode tests.
    /// </summary>
    public static class QuadMath
    {
        public const int TL = 0, TR = 1, BR = 2, BL = 3;

        /// <summary>Panel-space UV of each corner: u to the right, v up (so TL = (0,1)).</summary>
        public static readonly Vector2[] CornerUv = { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };

        // ------------------------------------------------------------------ plane

        /// <summary>Area-weighted normal of a polygon (Newell method), in Unity's left-handed frame: it points toward a viewer who sees the points run clockwise.</summary>
        public static Vector3 NewellNormal(IList<Vector3> p)
        {
            Vector3 n = Vector3.zero;
            for (int i = 0; i < p.Count; i++)
            {
                Vector3 a = p[i], b = p[(i + 1) % p.Count];
                n.x += (a.y - b.y) * (a.z + b.z);
                n.y += (a.z - b.z) * (a.x + b.x);
                n.z += (a.x - b.x) * (a.y + b.y);
            }
            return n;
        }

        public static Vector3 Centroid(IList<Vector3> p)
        {
            Vector3 c = Vector3.zero;
            for (int i = 0; i < p.Count; i++) c += p[i];
            return c / Mathf.Max(1, p.Count);
        }

        /// <summary>Best-fit plane through the points (through the centroid, along the Newell normal). Returns false for degenerate input.</summary>
        public static bool FitPlane(IList<Vector3> p, out Vector3 centre, out Vector3 normal)
        {
            centre = Centroid(p);
            normal = NewellNormal(p);
            if (normal.sqrMagnitude < 1e-10f) { normal = Vector3.up; return false; }
            normal.Normalize();
            return true;
        }

        public static Vector3[] ProjectToPlane(IList<Vector3> p, Vector3 centre, Vector3 normal)
        {
            var r = new Vector3[p.Count];
            for (int i = 0; i < p.Count; i++) r[i] = p[i] - normal * Vector3.Dot(p[i] - centre, normal);
            return r;
        }

        /// <summary>Largest distance of any point from the best-fit plane, in metres (0 for a perfectly planar quad).</summary>
        public static float NonPlanarity(IList<Vector3> p)
        {
            if (!FitPlane(p, out var c, out var n)) return float.PositiveInfinity;
            float m = 0f;
            for (int i = 0; i < p.Count; i++) m = Mathf.Max(m, Mathf.Abs(Vector3.Dot(p[i] - c, n)));
            return m;
        }

        // ------------------------------------------------------------------ validity and orientation

        /// <summary>Convex, non-self-intersecting, non-degenerate quad (checked in its own plane).</summary>
        public static bool IsValidQuad(IList<Vector3> q, float minEdge = 0.02f)
        {
            if (q == null || q.Count != 4) return false;
            if (!FitPlane(q, out _, out var n)) return false;
            for (int i = 0; i < 4; i++)
                if ((q[(i + 1) % 4] - q[i]).magnitude < minEdge) return false;
            float sign = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 e0 = q[(i + 1) % 4] - q[i], e1 = q[(i + 2) % 4] - q[(i + 1) % 4];
                float s = Vector3.Dot(Vector3.Cross(e0, e1), n);
                if (Mathf.Abs(s) < 1e-7f) return false;       // a straight run of three points
                if (sign == 0f) sign = Mathf.Sign(s);
                else if (Mathf.Sign(s) != sign) return false;  // a reflex corner or a bow-tie
            }
            return true;
        }

        /// <summary>The quad front normal: the side from which its corners run clockwise (TL, TR, BR, BL). Unity is left-handed, so for a clockwise-from-the-front order the cross product of consecutive edges (and Newell's normal) points TOWARD the viewer.</summary>
        public static Vector3 FrontNormal(IList<Vector3> q)
        {

            Vector3 n = NewellNormal(q);
            return n.sqrMagnitude < 1e-12f ? Vector3.forward : n.normalized;
        }

        /// <summary>True if the corners 0,1,2,3 run clockwise as seen from <paramref name="viewer"/> (so the image would read correctly, not mirrored).</summary>
        public static bool IsClockwiseFrom(IList<Vector3> q, Vector3 viewer)
        {
            return Vector3.Dot(FrontNormal(q), viewer - Centroid(q)) > 0f;
        }

        /// <summary>
        /// Puts the four placed points in screen order for a viewer at <paramref name="viewer"/>. The user clicks points in the order they
        /// like (the first click is the TL corner); if they drew the quad anticlockwise as seen from where they stand, the order is mirrored
        /// (keeping the first point as TL) so the picture is never reversed.
        /// </summary>
        public static Vector3[] OrderForViewer(IList<Vector3> placed, Vector3 viewer)
        {
            var q = new[] { placed[0], placed[1], placed[2], placed[3] };
            if (IsClockwiseFrom(q, viewer)) return q;
            return new[] { placed[0], placed[3], placed[2], placed[1] };
        }

        /// <summary>Rotates which corner is the top-left (steps of a quarter turn), turning the picture in its own plane.</summary>
        public static Vector3[] RotateCorners(IList<Vector3> q, int steps)
        {
            var r = new Vector3[4];
            for (int i = 0; i < 4; i++) r[i] = q[((i + steps) % 4 + 4) % 4];
            return r;
        }

        /// <summary>Projects the corners to their best-fit plane and returns them (original order); the input is returned unchanged when degenerate.</summary>
        public static Vector3[] Planarise(IList<Vector3> q)
        {
            if (!FitPlane(q, out var c, out var n)) return new[] { q[0], q[1], q[2], q[3] };
            return ProjectToPlane(q, c, n);
        }

        // ------------------------------------------------------------------ projective mapping

        /// <summary>
        /// Per-corner homogeneous texture coordinates (u*q, v*q, q) for perspective-correct texturing of a quad (the diagonal-intersection
        /// method). Interpolate these across the two triangles and divide by q per pixel.
        /// </summary>
        public static Vector3[] CornerUvq(IList<Vector3> q)
        {
            var res = new Vector3[4];
            if (!DiagonalIntersection(q, out var c)) // degenerate: fall back to the affine map
            {
                for (int i = 0; i < 4; i++) res[i] = new Vector3(CornerUv[i].x, CornerUv[i].y, 1f);
                return res;
            }
            for (int i = 0; i < 4; i++)
            {
                float d = (q[i] - c).magnitude, dOpp = (q[(i + 2) % 4] - c).magnitude;
                float w = dOpp > 1e-9f ? (d + dOpp) / dOpp : 1f;
                res[i] = new Vector3(CornerUv[i].x * w, CornerUv[i].y * w, w);
            }
            return res;
        }

        /// <summary>Intersection of the two diagonals (0-2 and 1-3) of a (nearly) planar quad. False if they are parallel.</summary>
        public static bool DiagonalIntersection(IList<Vector3> q, out Vector3 point)
        {
            Vector3 p = q[0], r = q[2] - q[0], s = q[3] - q[1], qq = q[1];
            Vector3 cross = Vector3.Cross(r, s);
            float denom = cross.sqrMagnitude;
            if (denom < 1e-14f) { point = Centroid(q); return false; }
            float t = Vector3.Dot(Vector3.Cross(qq - p, s), cross) / denom;
            point = p + r * t;
            return true;
        }

        /// <summary>Panel UV (u right, v up, both 0..1 inside the screen) of a point lying on the quad's plane. False if the quad is degenerate.</summary>
        public static bool WorldToUv(Vector3 world, IList<Vector3> q, out Vector2 uv)
        {
            uv = default;
            var uvq = CornerUvq(q);
            // Affine map of the plane onto (uq, vq, q): solve for the barycentric coordinates of the point in triangle (0, 1, 2).
            Vector3 v0 = q[1] - q[0], v1 = q[2] - q[0], v2 = world - q[0];
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-12f) return false;
            float b1 = (d11 * d20 - d01 * d21) / den, b2 = (d00 * d21 - d01 * d20) / den, b0 = 1f - b1 - b2;
            Vector3 h = uvq[0] * b0 + uvq[1] * b1 + uvq[2] * b2;
            if (Mathf.Abs(h.z) < 1e-9f) return false;
            uv = new Vector2(h.x / h.z, h.y / h.z);
            return true;
        }

        /// <summary>World point of a panel UV (the inverse of <see cref="WorldToUv"/>): bilinear in the homogeneous space, exact for the projective map.</summary>
        public static Vector3 UvToWorld(Vector2 uv, IList<Vector3> q)
        {
            // Solve H^-1: find the plane point P whose (uq, vq, q) divides to uv. P = sum over corners of w_i * P_i with weights from the
            // projective map; use the corner weights of the unit square mapped through the diagonal q-values.
            var uvq = CornerUvq(q);
            // Affine function a(P) = (uq, vq, q) over the plane. Invert by solving a(P) = (uv.x * k, uv.y * k, k) for P and k, via the triangle (0,1,2).
            // a(P) = uvq0 + b1 (uvq1 - uvq0) + b2 (uvq2 - uvq0); require a(P).xy = uv * a(P).z.
            Vector3 e1 = uvq[1] - uvq[0], e2 = uvq[2] - uvq[0], a0 = uvq[0];
            // (a0.x + b1 e1.x + b2 e2.x) - uv.x (a0.z + b1 e1.z + b2 e2.z) = 0, and the same for y.
            float m00 = e1.x - uv.x * e1.z, m01 = e2.x - uv.x * e2.z, r0 = -(a0.x - uv.x * a0.z);
            float m10 = e1.y - uv.y * e1.z, m11 = e2.y - uv.y * e2.z, r1 = -(a0.y - uv.y * a0.z);
            float det = m00 * m11 - m01 * m10;
            if (Mathf.Abs(det) < 1e-12f) return Centroid(q);
            float b1 = (r0 * m11 - m01 * r1) / det, b2 = (m00 * r1 - r0 * m10) / det;
            return q[0] + (q[1] - q[0]) * b1 + (q[2] - q[0]) * b2;
        }

        // ------------------------------------------------------------------ rays

        /// <summary>Ray against the quad: the distance along the ray and the panel UV of the hit. Misses return false (outside the quad if <paramref name="clampToQuad"/>).</summary>
        public static bool RayToUv(Ray ray, IList<Vector3> q, out Vector2 uv, out float distance, bool clampToQuad = true)
        {
            uv = default; distance = 0f;
            if (!FitPlane(q, out var c, out var n)) return false;
            float denom = Vector3.Dot(n, ray.direction);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float t = Vector3.Dot(c - ray.origin, n) / denom;
            if (t < 0f) return false;
            Vector3 hit = ray.origin + ray.direction * t;
            if (!WorldToUv(hit, q, out uv)) return false;
            distance = t;
            if (clampToQuad && (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)) return false;
            return true;
        }

        /// <summary>Nearest corner to the ray (perpendicular distance), within <paramref name="radius"/> metres; -1 if none.</summary>
        public static int NearestCornerToRay(Ray ray, IList<Vector3> q, float radius, out float distanceAlongRay)
        {
            int best = -1; float bestD = radius; distanceAlongRay = 0f;
            for (int i = 0; i < q.Count; i++)
            {
                Vector3 to = q[i] - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along < 0f) continue;
                float perp = (to - ray.direction * along).magnitude;
                if (perp < bestD) { bestD = perp; best = i; distanceAlongRay = along; }
            }
            return best;
        }

        // ------------------------------------------------------------------ crop

        /// <summary>
        /// Source pixel position for a panel UV with a crop rectangle. The crop is in normalised source coordinates with the origin at the
        /// image's TOP-LEFT and y down: x, y, width, height. Returns source-normalised (x right, y down).
        /// </summary>
        public static Vector2 PanelUvToSource(Vector2 uv, Rect crop)
        {
            return new Vector2(crop.x + uv.x * crop.width, crop.y + (1f - uv.y) * crop.height);
        }

        /// <summary>Inverse of <see cref="PanelUvToSource"/>.</summary>
        public static Vector2 SourceToPanelUv(Vector2 src, Rect crop)
        {
            return new Vector2((src.x - crop.x) / Mathf.Max(1e-6f, crop.width), 1f - (src.y - crop.y) / Mathf.Max(1e-6f, crop.height));
        }

        /// <summary>Normalised crop rectangle from two points picked on the source (any order), clamped to the image and to a minimum size.</summary>
        public static Rect CropFromPoints(Vector2 a, Vector2 b, float minSize = 0.01f)
        {
            float x0 = Mathf.Clamp01(Mathf.Min(a.x, b.x)), x1 = Mathf.Clamp01(Mathf.Max(a.x, b.x));
            float y0 = Mathf.Clamp01(Mathf.Min(a.y, b.y)), y1 = Mathf.Clamp01(Mathf.Max(a.y, b.y));
            if (x1 - x0 < minSize) x1 = Mathf.Min(1f, x0 + minSize);
            if (y1 - y0 < minSize) y1 = Mathf.Min(1f, y0 + minSize);
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        // ------------------------------------------------------------------ construction helpers

        /// <summary>
        /// A rectangle of the given width and aspect (w/h) standing in front of <paramref name="head"/> at <paramref name="distance"/>, facing the head,
        /// vertically centred at head height. Used for the first panel and for panels created from a crop.
        /// </summary>
        public static Vector3[] RectInFront(Vector3 head, Vector3 forward, Vector3 up, float distance, float width, float aspect)
        {
            forward = Vector3.ProjectOnPlane(forward, Vector3.up).sqrMagnitude < 1e-6f ? forward : Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Vector3 centre = head + forward * distance;
            float h = width / Mathf.Max(0.1f, aspect);
            return RectAt(centre, right, Vector3.up, width, h);
        }

        public static Vector3[] RectAt(Vector3 centre, Vector3 right, Vector3 up, float width, float height)
        {
            Vector3 hr = right.normalized * (width * 0.5f), hu = up.normalized * (height * 0.5f);
            return new[] { centre - hr + hu, centre + hr + hu, centre + hr - hu, centre - hr - hu };
        }

        /// <summary>True when opposite edges are about the same length and parallel enough that the quad is a (skewed) rectangle rather than a deliberate trapezoid.</summary>
        public static bool IsNearParallelogram(IList<Vector3> q, float tolerance = 0.18f)
        {
            float top = (q[TR] - q[TL]).magnitude, bottom = (q[BR] - q[BL]).magnitude, left = (q[BL] - q[TL]).magnitude, right = (q[BR] - q[TR]).magnitude;
            if (Mathf.Abs(top - bottom) / Mathf.Max(top, bottom) > tolerance || Mathf.Abs(left - right) / Mathf.Max(left, right) > tolerance) return false;
            return Vector3.Angle(q[TR] - q[TL], q[BR] - q[BL]) < 12f && Vector3.Angle(q[BL] - q[TL], q[BR] - q[TR]) < 12f;
        }

        /// <summary>
        /// Turns a quad into a rectangle of the given picture aspect (width / height) in its own plane, about its centroid, keeping its orientation. With
        /// <paramref name="shrink"/> the result lies inside the original extents (the longer dimension is reduced); otherwise it grows the shorter one.
        /// </summary>
        public static Vector3[] FitAspect(IList<Vector3> q, float aspect, bool shrink = true)
        {
            aspect = Mathf.Max(0.05f, aspect);
            var c = Centroid(q);
            Vector3 r = ((q[TR] - q[TL]) + (q[BR] - q[BL])).normalized;
            Vector3 n = FrontNormal(q);
            Vector3 up = Vector3.Cross(n, r);                                    // Unity is left-handed: with n toward the viewer, n x right is the screen's up
            if (Vector3.Dot(up, (q[TL] - q[BL]) + (q[TR] - q[BR])) < 0f) up = -up;
            up = up.normalized;
            var size = Size(q);
            float w = size.x, h = size.y;
            if (shrink) { if (w / h > aspect) w = h * aspect; else h = w / aspect; }
            else { if (w / h > aspect) h = w / aspect; else w = h * aspect; }
            return RectAt(c, r, up, w, h);
        }

        /// <summary>Width and height of the quad as the average of opposite edges (for aspect-ratio hints and handle sizes).</summary>
        public static Vector2 Size(IList<Vector3> q)
        {
            float w = ((q[TR] - q[TL]).magnitude + (q[BR] - q[BL]).magnitude) * 0.5f;
            float h = ((q[TL] - q[BL]).magnitude + (q[TR] - q[BR]).magnitude) * 0.5f;
            return new Vector2(w, h);
        }

        /// <summary>Scales the quad about its centroid.</summary>
        public static Vector3[] Scale(IList<Vector3> q, float factor)
        {
            var c = Centroid(q);
            var r = new Vector3[q.Count];
            for (int i = 0; i < q.Count; i++) r[i] = c + (q[i] - c) * factor;
            return r;
        }

        /// <summary>Applies a rigid transform to the corners.</summary>
        public static Vector3[] Transform(IList<Vector3> q, Matrix4x4 m)
        {
            var r = new Vector3[q.Count];
            for (int i = 0; i < q.Count; i++) r[i] = m.MultiplyPoint3x4(q[i]);
            return r;
        }

        /// <summary>The triangle indices of the quad for a front face seen clockwise (Unity's front-face winding), two triangles along the 0-2 diagonal.</summary>
        public static readonly int[] Triangles = { 0, 1, 2, 0, 2, 3 };
    }
}
