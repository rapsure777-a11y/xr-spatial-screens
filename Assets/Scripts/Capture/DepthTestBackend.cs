using System.IO;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Depth Lab test source: one still picture plus a prepared depth map (0 = far, 1 = near), no streaming involved.
    /// By default the picture is a generated strategy-game-like frame with obvious layers (terrain sloping towards the viewer, hills, a river, a town of
    /// buildings, units, trees) and a flat HUD with real text, and the depth map is generated from the same shapes. If <c>depth_source.png</c> and
    /// <c>depth_map.png</c> exist in the app's data folder they are used instead (a real screenshot and a depth map made offline).
    /// </summary>
    public sealed class DepthTestBackend : ICaptureBackend, IDepthProvider
    {
        const int W = 1280, H = 720;
        readonly Color32[] m_Pixels;              // BGRA, TOP row first (like the capture helper's frames)
        int m_Width = W, m_Height = H;
        Texture2D m_Depth;
        bool m_Delivered;
        string m_Origin = "generated strategy scene";

        static Color32 Px(int r, int g, int b) => new Color32((byte)Mathf.Clamp(b, 0, 255), (byte)Mathf.Clamp(g, 0, 255), (byte)Mathf.Clamp(r, 0, 255), 255);

        public DepthTestBackend()
        {
            string src = Path.Combine(Application.persistentDataPath, "depth_source.png"), dep = Path.Combine(Application.persistentDataPath, "depth_map.png");
            if (File.Exists(src) && File.Exists(dep) && TryLoadFiles(src, dep, out m_Pixels, out m_Depth)) { m_Origin = "your picture and depth map"; return; }
            m_Pixels = new Color32[W * H];
            m_Depth = Generate(m_Pixels);
        }

        // ------------------------------------------------------------------ ICaptureBackend

        public string Status => $"depth test: {m_Origin} {m_Width}x{m_Height}";
        public bool IsRunning => true;
        public bool HasEnded => false;
        public int Width => m_Width;
        public int Height => m_Height;
        public float CaptureFps => 0f;
        public WindowGeometry Window => new WindowGeometry { x = 0, y = 0, width = m_Width, height = m_Height, alive = true, foreground = true };
        public Texture2D Depth => m_Depth;

        public bool PollInto(ref Texture2D target)
        {
            if (m_Delivered) return false;
            m_Delivered = true;
            if (!target || target.width != m_Width || target.height != m_Height)
            {
                if (target) Object.Destroy(target);
                target = new Texture2D(m_Width, m_Height, TextureFormat.BGRA32, false, false) { name = "DepthTestFrame", wrapMode = TextureWrapMode.Clamp };
            }
            target.SetPixelData(m_Pixels, 0);
            target.Apply(false);
            return true;
        }

        public void Dispose() { if (m_Depth) Object.Destroy(m_Depth); m_Depth = null; }

        // ------------------------------------------------------------------ files

        bool TryLoadFiles(string srcPath, string depPath, out Color32[] pixels, out Texture2D depth)
        {
            pixels = null; depth = null;
            try
            {
                var s = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var d = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!s.LoadImage(File.ReadAllBytes(srcPath)) || !d.LoadImage(File.ReadAllBytes(depPath))) return false;
                m_Width = s.width; m_Height = s.height;
                var sp = s.GetPixels32();
                pixels = new Color32[sp.Length];
                for (int y = 0; y < m_Height; y++)                                  // LoadImage gives the bottom row first; frames are top row first
                    for (int x = 0; x < m_Width; x++)
                    {
                        var c = sp[(m_Height - 1 - y) * m_Width + x];
                        pixels[y * m_Width + x] = new Color32(c.b, c.g, c.r, 255);
                    }
                var dp = d.GetPixels32();
                var bytes = new byte[dp.Length];
                for (int y = 0; y < d.height; y++)
                    for (int x = 0; x < d.width; x++) bytes[y * d.width + x] = dp[(d.height - 1 - y) * d.width + x].r;
                depth = new Texture2D(d.width, d.height, TextureFormat.R8, true, true) { name = "DepthMap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                depth.SetPixelData(bytes, 0); depth.Apply(true);
                Object.Destroy(s); Object.Destroy(d);
                return true;
            }
            catch (System.Exception e) { Debug.LogWarning("[XrSpatial] depth test files not usable: " + e.Message); return false; }
        }

        // ------------------------------------------------------------------ generated scene

        struct Box { public int x0, y0, x1, y1; public float h; public Color32 roof, wall; }

        static float Hash(int x, int y) { unchecked { uint n = (uint)(x * 374761393 + y * 668265263); n = (n ^ (n >> 13)) * 1274126177u; return ((n ^ (n >> 16)) & 0xffff) / 65535f; } }
        static float Noise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y); float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            return Mathf.Lerp(Mathf.Lerp(Hash(xi, yi), Hash(xi + 1, yi), fx), Mathf.Lerp(Hash(xi, yi + 1), Hash(xi + 1, yi + 1), fx), fy);
        }

        const int HudTop = 50, HudBottom = 580;

        static Texture2D Generate(Color32[] px)
        {
            var depthFull = new float[W * H];
            var text = new bool[W * H];
            // HUD text first, as a mask
            DrawText(text, "GOLD 1200   WOOD 340   ARMY 12", 24, 14, 3);
            DrawText(text, "BUILD", 214, 612, 2); DrawText(text, "TRAIN", 344, 612, 2); DrawText(text, "ARMY", 474, 612, 2); DrawText(text, "GOLD", 604, 612, 2);
            DrawText(text, "ARMY 12", 870, 606, 3); DrawText(text, "GOLD 1200", 870, 646, 3);

            var boxes = new[]
            {
                new Box { x0 = 330, y0 = 300, x1 = 420, y1 = 372, h = 0.30f, roof = Px(190, 70, 60), wall = Px(150, 130, 100) },
                new Box { x0 = 450, y0 = 320, x1 = 520, y1 = 380, h = 0.26f, roof = Px(175, 85, 55), wall = Px(140, 122, 96) },
                new Box { x0 = 380, y0 = 395, x1 = 480, y1 = 470, h = 0.34f, roof = Px(70, 90, 170), wall = Px(160, 150, 130) },
                new Box { x0 = 760, y0 = 210, x1 = 850, y1 = 290, h = 0.36f, roof = Px(110, 110, 120), wall = Px(120, 118, 125) },
                new Box { x0 = 880, y0 = 250, x1 = 940, y1 = 310, h = 0.24f, roof = Px(180, 80, 60), wall = Px(150, 128, 100) },
            };
            var hills = new[] { new Vector4(250, 190, 150, 0.22f), new Vector4(990, 180, 130, 0.20f), new Vector4(720, 440, 120, 0.12f) };   // x, y, radius, height
            var trees = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < 46; i++) trees.Add(new Vector3(60 + Hash(i, 3) * 1160, HudTop + 30 + Hash(i, 7) * (HudBottom - HudTop - 60), 11 + Hash(i, 11) * 8));
            var units = new Vector3[24];
            for (int i = 0; i < units.Length; i++) units[i] = new Vector3(520 + Hash(i, 21) * 380, 300 + Hash(i, 23) * 220, i % 2);

            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    Color32 c; float d;
                    if (y < HudTop || y >= HudBottom)
                    {
                        Hud(x, y, text[i], out c, out d);
                    }
                    else
                    {
                        float ty = (y - HudTop) / (float)(HudBottom - HudTop);
                        d = 0.16f + 0.30f * ty;                                                        // the ground slopes towards the viewer
                        float n = Noise(x / 38f, y / 38f) * 0.6f + Noise(x / 9f, y / 9f) * 0.4f;
                        float gr = 70 + 60 * n, gg = 120 + 60 * n, gb = 55 + 25 * n;
                        c = Px((int)gr, (int)gg, (int)gb);
                        foreach (var h in hills)
                        {
                            float dd = ((x - h.x) * (x - h.x) + (y - h.y) * (y - h.y)) / (h.z * h.z);
                            if (dd < 4f) { float k = Mathf.Exp(-dd); d += h.w * k; c = Px((int)(gr + 40 * k), (int)(gg + 25 * k), (int)(gb + 20 * k)); }
                        }
                        float rx = 640 + 190 * Mathf.Sin(y * 0.011f + 0.6f);                          // river
                        if (Mathf.Abs(x - rx) < 24f) { d -= 0.07f; float w = Mathf.Abs(x - rx) / 24f; c = Px((int)(40 + 30 * w), (int)(100 + 40 * w), (int)(190 - 20 * w)); }
                        foreach (var b in boxes)
                            if (x >= b.x0 && x < b.x1 && y >= b.y0 && y < b.y1)
                            {
                                d += b.h; bool roof = y < b.y0 + (b.y1 - b.y0) * 0.55f; c = roof ? b.roof : b.wall;
                                if (x == b.x0 || x == b.x1 - 1 || y == b.y0 || y == b.y1 - 1) c = Px(c.r / 2, c.g / 2, c.b / 2);
                            }
                        foreach (var t in trees)
                        {
                            float dd = (x - t.x) * (x - t.x) + (y - t.y) * (y - t.y);
                            if (dd < t.z * t.z) { float k = 1f - Mathf.Sqrt(dd) / t.z; d += 0.05f + 0.16f * k; c = Px((int)(20 + 30 * k), (int)(70 + 55 * k), (int)(25 + 20 * k)); }
                        }
                        foreach (var u in units)
                        {
                            float dd = (x - u.x) * (x - u.x) + (y - u.y) * (y - u.y);
                            if (dd < 56f) { d += 0.14f; c = u.z > 0 ? Px(220, 60, 50) : Px(60, 110, 230); }
                            else if ((x - u.x - 6) * (x - u.x - 6) + (y - u.y - 7) * (y - u.y - 7) < 50f) c = Px(c.r * 6 / 10, c.g * 6 / 10, c.b * 6 / 10);
                        }
                    }
                    px[i] = c; depthFull[i] = Mathf.Clamp01(d);
                }

            // depth map at half resolution (smooth enough for a vertex grid), top row first
            int dw = W / 2, dh = H / 2;
            var bytes = new byte[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float s = depthFull[(2 * y) * W + 2 * x] + depthFull[(2 * y) * W + 2 * x + 1] + depthFull[(2 * y + 1) * W + 2 * x] + depthFull[(2 * y + 1) * W + 2 * x + 1];
                    bytes[y * dw + x] = (byte)Mathf.RoundToInt(255f * s / 4f);
                }
            var tex = new Texture2D(dw, dh, TextureFormat.R8, true, true) { name = "DepthMap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixelData(bytes, 0); tex.Apply(true);
            return tex;
        }

        /// <summary>The HUD: flat dark bars with text, a minimap and buttons. It carries one constant, "near" depth, as a depth model typically gives an overlay.</summary>
        static void Hud(int x, int y, bool textPx, out Color32 c, out float d)
        {
            d = 0.80f;
            c = Px(24, 28, 36);
            if (textPx) { c = Px(240, 225, 160); return; }
            if (y < HudTop) { if (y == HudTop - 1) c = Px(90, 100, 120); return; }
            if (y == HudBottom) c = Px(90, 100, 120);
            if (x >= 20 && x < 160 && y >= 596 && y < 706)                                              // minimap
            {
                float n = Noise(x / 11f, y / 11f);
                c = n > 0.55f ? Px(60, 140, 70) : n > 0.28f ? Px(80, 120, 60) : Px(50, 100, 180);
                if (x == 20 || x == 159 || y == 596 || y == 705) c = Px(180, 190, 200);
            }
            for (int b = 0; b < 4; b++)                                                                 // buttons
                if (x >= 200 + b * 130 && x < 310 + b * 130 && y >= 600 && y < 650) c = Px(48, 60, 84);
            if (x >= 850 && x < 1260 && y >= 596 && y < 706) c = Px(34, 40, 52);                        // info panel
        }

        // ------------------------------------------------------------------ 5x7 text for the HUD

        static readonly System.Collections.Generic.Dictionary<char, string> Glyphs = new System.Collections.Generic.Dictionary<char, string>
        {
            ['G'] = "01110100011000010111100011000101110", ['O'] = "01110100011000110001100011000101110", ['L'] = "10000100001000010000100001000011111",
            ['D'] = "11110100011000110001100011000111110", ['W'] = "10001100011000110101101011101110001", ['A'] = "01110100011000111111100011000110001",
            ['R'] = "11110100011000111110101001001010001", ['M'] = "10001110111010110101100011000110001", ['Y'] = "10001100010101000100001000010000100",
            ['B'] = "11110100011000111110100011000111110", ['U'] = "10001100011000110001100011000101110", ['I'] = "01110001000010000100001000010001110",
            ['T'] = "11111001000010000100001000010000100", ['N'] = "10001110011010110011100011000110001",
            ['0'] = "01110100011001110101110011000101110", ['1'] = "00100011000010000100001000010001110", ['2'] = "01110100010000100010001000100011111",
            ['3'] = "11110000010000101110000010000111110", ['4'] = "00010001100101010010111110001000010",
        };

        static void DrawText(bool[] mask, string s, int x0, int y0, int k)
        {
            int cx = x0;
            foreach (char ch in s)
            {
                if (Glyphs.TryGetValue(ch, out var g))
                    for (int gy = 0; gy < 7; gy++)
                        for (int gx = 0; gx < 5; gx++)
                            if (g[gy * 5 + gx] == '1')
                                for (int py = 0; py < k; py++)
                                    for (int pxl = 0; pxl < k; pxl++)
                                    {
                                        int x = cx + gx * k + pxl, y = y0 + gy * k + py;
                                        if (x >= 0 && x < W && y >= 0 && y < H) mask[y * W + x] = true;
                                    }
                cx += 6 * k;
            }
        }
    }
}
