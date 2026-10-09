using System.Collections.Generic;
using UnityEngine;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// Depth Lab (experimental 2.5D panels). Global switches and the maths that turn them into shader parameters, plus the subdivided mesh a depth panel uses.
    /// Everything here is inert while <see cref="Enabled"/> is false (the default at every launch): no mesh is built, no material is created.
    /// </summary>
    public static class DepthLab
    {
        /// <summary>Flat (false, the proven path) or 2.5D (true).</summary>
        public static bool Enabled;
        /// <summary>0 = flat, 1 = the intentionally exaggerated maximum. Useful range is much lower.</summary>
        public static float Strength = 0.20f;
        /// <summary>The depth value (0 far .. 1 near) that stays on the original panel plane: low = the picture comes forward from a receding floor, high = it recedes behind the window.</summary>
        public static float Focus = 0.50f;
        /// <summary>Bumped on every change, so panels only push parameters when something changed.</summary>
        public static int Version { get; private set; }

        public const float MaxDisplacement = 0.35f;                        // metres: nothing ever moves further than this from the panel plane
        public const float ReliefPerWidth = 0.30f;                         // strength 1 = depth 0 to depth 1 differ by 30% of the panel width (before the clamp)
        public const int GridColumns = 128;

        public static void Set(bool enabled, float strength, float focus)
        {
            Enabled = enabled; Strength = Mathf.Clamp01(strength); Focus = Mathf.Clamp01(focus); Version++;
        }
        public static void SetEnabled(bool on) { Enabled = on; Version++; }
        public static void SetStrength(float s) { Strength = Mathf.Clamp01(s); Version++; }
        public static void SetFocus(float f) { Focus = Mathf.Clamp01(f); Version++; }

        /// <summary>Relief (metres between depth 0 and depth 1) for a panel of the given width, capped so the farthest displacement stays within <see cref="MaxDisplacement"/>.</summary>
        public static float Relief(float panelWidth, float strength, float focus)
        {
            float relief = strength * ReliefPerWidth * panelWidth;
            float reach = Mathf.Max(focus, 1f - focus, 0.01f);               // the largest |depth - focus| that can occur
            return Mathf.Min(relief, MaxDisplacement / reach);
        }

        /// <summary>
        /// A grid over the quad. Positions are bilinear in the four corners; each vertex's (u*q, v*q, q) is the exact affine function of its in-plane position
        /// (derived from three corners), so quads seen in perspective map correctly, same as the flat two-triangle mesh.
        /// </summary>
        public static Mesh BuildGrid(Vector3[] q, int columns = GridColumns)
        {
            var uvq = QuadMath.CornerUvq(q);
            Vector3 e1 = q[1] - q[0], e2 = q[3] - q[0];
            float w = Mathf.Max(0.01f, ((q[1] - q[0]).magnitude + (q[2] - q[3]).magnitude) * 0.5f);
            float h = Mathf.Max(0.01f, ((q[3] - q[0]).magnitude + (q[2] - q[1]).magnitude) * 0.5f);
            int cols = Mathf.Clamp(columns, 8, 200), rows = Mathf.Clamp(Mathf.RoundToInt(cols * h / w), 4, 200);
            // in-plane coordinates (a, b): P - q0 = a e1 + b e2, from the 2x2 Gram system
            float g11 = Vector3.Dot(e1, e1), g12 = Vector3.Dot(e1, e2), g22 = Vector3.Dot(e2, e2);
            float det = g11 * g22 - g12 * g12;
            bool ok = Mathf.Abs(det) > 1e-12f;
            var verts = new List<Vector3>((cols + 1) * (rows + 1));
            var uvs = new List<Vector3>((cols + 1) * (rows + 1));
            for (int r = 0; r <= rows; r++)
                for (int c = 0; c <= cols; c++)
                {
                    float s = c / (float)cols, t = r / (float)rows;                       // s along the top edge, t down the left edge
                    Vector3 top = Vector3.Lerp(q[0], q[1], s), bottom = Vector3.Lerp(q[3], q[2], s);
                    Vector3 pos = Vector3.Lerp(top, bottom, t);
                    float a = s, b = t;
                    if (ok)
                    {
                        Vector3 d = pos - q[0];
                        float r1 = Vector3.Dot(d, e1), r2 = Vector3.Dot(d, e2);
                        a = (r1 * g22 - r2 * g12) / det; b = (r2 * g11 - r1 * g12) / det;
                    }
                    verts.Add(pos);
                    uvs.Add(uvq[0] + a * (uvq[1] - uvq[0]) + b * (uvq[3] - uvq[0]));
                }
            var tris = new List<int>(cols * rows * 6);
            int stride = cols + 1;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i0 = r * stride + c, i1 = i0 + 1, i2 = i0 + stride + 1, i3 = i0 + stride;
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    tris.Add(i0); tris.Add(i2); tris.Add(i3);
                }
            var m = new Mesh { name = "PanelDepthGrid", indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(verts); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            m.bounds = new Bounds(m.bounds.center, m.bounds.size + Vector3.one * (2f * MaxDisplacement + 0.1f));
            return m;
        }
    }
}
