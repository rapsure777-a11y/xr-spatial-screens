using System;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// The picture of the PC cursor, drawn by code (no art files): one atlas of small shapes, 64 px each, 4 columns by 3 rows. Each texel holds three masks:
    /// R the cursor's fill, G its dark outline (two pixels out from the fill), B a wider soft halo used by High Visibility. The shapes are the Windows ones people meet most:
    /// arrow, text bar, link hand, busy ring, cross, move, the four resize arrows and "not allowed". A cursor a game draws itself matches none of these and shows as the arrow
    /// (see <see cref="CursorMath.ShapeOther"/>); that is the known limit of this first version.
    /// </summary>
    public static class CursorArt
    {
        public const int Cell = 64, Columns = 4, Rows = 3, ShapeCount = 11;
        public const int Width = Cell * Columns, Height = Cell * Rows;

        /// <summary>Atlas cell (0..10) for a shape code sent by the host; unknown codes use the arrow.</summary>
        public static int CellOf(byte shape) => shape <= CursorMath.ShapeNo ? shape : 0;

        /// <summary>Cell origin and size in texture coordinates (v up), for the shader's _Cell.</summary>
        public static Vector4 CellRect(byte shape)
        {
            int c = CellOf(shape);
            return new Vector4((c % Columns) / (float)Columns, (c / Columns) / (float)Rows, 1f / Columns, 1f / Rows);
        }

        /// <summary>The point of the shape that sits on the position Windows reports (unit square, x right, y down): the arrow's tip, the hand's fingertip, the centre for the rest.</summary>
        public static Vector2 Hotspot(byte shape)
        {
            switch (CellOf(shape))
            {
                case CursorMath.ShapeArrow: return new Vector2(0.06f, 0.06f);
                case CursorMath.ShapeHand: return new Vector2(0.42f, 0.08f);
                default: return new Vector2(0.5f, 0.5f);
            }
        }

        // ------------------------------------------------------------------ shapes (unit square, y down)

        static bool Rect(float x, float y, float x0, float y0, float x1, float y1) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        static bool Poly(float x, float y, float[] p)
        {
            bool inside = false;
            for (int i = 0, j = p.Length - 2; i < p.Length; j = i, i += 2)
            {
                float xi = p[i], yi = p[i + 1], xj = p[j], yj = p[j + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        static readonly float[] ArrowPoly = { 0.06f, 0.06f, 0.06f, 0.74f, 0.22f, 0.60f, 0.33f, 0.86f, 0.43f, 0.82f, 0.32f, 0.57f, 0.53f, 0.57f };
        static readonly float[] TriLeft = { 0.04f, 0.5f, 0.26f, 0.28f, 0.26f, 0.72f };
        static readonly float[] TriRight = { 0.96f, 0.5f, 0.74f, 0.28f, 0.74f, 0.72f };
        static readonly float[] TriUp = { 0.5f, 0.04f, 0.28f, 0.26f, 0.72f, 0.26f };
        static readonly float[] TriDown = { 0.5f, 0.96f, 0.28f, 0.74f, 0.72f, 0.74f };

        static bool ResizeWE(float x, float y) => Rect(x, y, 0.2f, 0.45f, 0.8f, 0.55f) || Poly(x, y, TriLeft) || Poly(x, y, TriRight);

        static bool Inside(int cell, float x, float y)
        {
            float dx = x - 0.5f, dy = y - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy);
            const float s = 0.70710678f;
            switch (cell)
            {
                case CursorMath.ShapeArrow: return Poly(x, y, ArrowPoly);
                case CursorMath.ShapeIBeam: return Rect(x, y, 0.46f, 0.12f, 0.54f, 0.88f) || Rect(x, y, 0.34f, 0.12f, 0.66f, 0.2f) || Rect(x, y, 0.34f, 0.8f, 0.66f, 0.88f);
                case CursorMath.ShapeHand:
                    return Rect(x, y, 0.36f, 0.08f, 0.48f, 0.55f) || Rect(x, y, 0.28f, 0.46f, 0.74f, 0.86f) || Rect(x, y, 0.48f, 0.36f, 0.58f, 0.55f) || Rect(x, y, 0.58f, 0.40f, 0.68f, 0.55f) || Rect(x, y, 0.68f, 0.44f, 0.76f, 0.58f);
                case CursorMath.ShapeBusy:
                    {
                        float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;                 // a ring with a gap at the right so it reads as "working", not a plain circle
                        return r >= 0.27f && r <= 0.40f && !(ang > -35f && ang < 35f);
                    }
                case CursorMath.ShapeCross: return Rect(x, y, 0.1f, 0.46f, 0.9f, 0.54f) || Rect(x, y, 0.46f, 0.1f, 0.54f, 0.9f);
                case CursorMath.ShapeSizeAll:
                    return Rect(x, y, 0.2f, 0.46f, 0.8f, 0.54f) || Rect(x, y, 0.46f, 0.2f, 0.54f, 0.8f) || Poly(x, y, TriLeft) || Poly(x, y, TriRight) || Poly(x, y, TriUp) || Poly(x, y, TriDown);
                case CursorMath.ShapeSizeWE: return ResizeWE(x, y);
                case CursorMath.ShapeSizeNS: return ResizeWE(y, x);
                case CursorMath.ShapeSizeNWSE: return ResizeWE(0.5f + (dx + dy) * s, 0.5f + (dy - dx) * s);
                case CursorMath.ShapeSizeNESW: return ResizeWE(0.5f + (dx - dy) * s, 0.5f + (dx + dy) * s);
                case CursorMath.ShapeNo: return (r >= 0.30f && r <= 0.40f) || (r <= 0.38f && Mathf.Abs(dx - dy) * s < 0.05f);
                default: return false;
            }
        }

        /// <summary>The atlas as RGBA32 bytes, row 0 at the bottom (a Texture2D's order). Costs a few milliseconds, once.</summary>
        public static byte[] Render()
        {
            var px = new byte[Width * Height * 4];
            var fill = new bool[Cell * Cell];
            var dist = new float[Cell * Cell];
            const int reach = 6;
            for (int cell = 0; cell < ShapeCount; cell++)
            {
                int ox = (cell % Columns) * Cell, oy = (cell / Columns) * Cell;
                var cover = new float[Cell * Cell];
                for (int j = 0; j < Cell; j++)
                    for (int i = 0; i < Cell; i++)
                    {
                        int hits = 0;                                                  // 2x2 samples per texel for a clean edge
                        for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++) if (Inside(cell, (i + 0.25f + 0.5f * sx) / Cell, (j + 0.25f + 0.5f * sy) / Cell)) hits++;
                        cover[j * Cell + i] = hits / 4f; fill[j * Cell + i] = hits >= 2;
                    }
                for (int j = 0; j < Cell; j++)
                    for (int i = 0; i < Cell; i++)
                    {
                        float best = reach + 1f;
                        for (int dj = -reach; dj <= reach && best > 0f; dj++)
                            for (int di = -reach; di <= reach; di++)
                            {
                                int a = i + di, b = j + dj;
                                if (a < 0 || b < 0 || a >= Cell || b >= Cell || !fill[b * Cell + a]) continue;
                                float d = Mathf.Sqrt(di * di + dj * dj);
                                if (d < best) best = d;
                            }
                        dist[j * Cell + i] = fill[j * Cell + i] ? 0f : best;
                    }
                for (int j = 0; j < Cell; j++)                                          // j counts down from the top of the shape; texture rows count up
                    for (int i = 0; i < Cell; i++)
                    {
                        float d = dist[j * Cell + i];
                        byte r = (byte)Mathf.RoundToInt(255f * cover[j * Cell + i]);
                        byte g = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(Mathf.Max(cover[j * Cell + i], 2.5f - d)));
                        byte bl = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01((reach - d) / 4f));
                        int o = ((oy + (Cell - 1 - j)) * Width + ox + i) * 4;
                        px[o] = r; px[o + 1] = g; px[o + 2] = bl; px[o + 3] = 255;
                    }
            }
            return px;
        }
    }
}
