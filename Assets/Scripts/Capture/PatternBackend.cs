using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// A generated, animated test image with an asymmetric layout: coloured corner blocks (TL red, TR green, BR blue, BL yellow), a big "F" (so mirroring and
    /// rotation are obvious), a fine grid, a diagonal, a moving bar and a frame counter made of bit-blocks. Used to verify mapping, cropping and orientation
    /// without capturing anything, and as the source in tests and screenshots.
    /// </summary>
    public sealed class PatternBackend : ICaptureBackend
    {
        readonly int m_W, m_H;
        readonly Color32[] m_Base;
        readonly Color32[] m_Frame;
        readonly float m_Interval;
        float m_Next;
        int m_Counter;
        bool m_First = true;

        /// <summary>A colour as the BGRA32 byte order the frame texture expects (so red really is red).</summary>
        static Color32 Px(byte r, byte g, byte b, byte a) => new Color32(b, g, r, a);

        public PatternBackend(int width = 1920, int height = 1080, float fps = 30f)
        {
            m_W = width; m_H = height; m_Interval = 1f / Mathf.Max(1f, fps);
            m_Base = new Color32[width * height];
            m_Frame = new Color32[width * height];
            Build();
            Window = new WindowGeometry { x = 0, y = 0, width = width, height = height, alive = true, foreground = true };
        }

        public string Status => $"test pattern {m_W}x{m_H}";
        public bool IsRunning => true;
        public bool HasEnded => false;
        public int Width => m_W;
        public int Height => m_H;
        public float CaptureFps => 1f / m_Interval;
        public WindowGeometry Window { get; }

        public bool PollInto(ref Texture2D target)
        {
            if (!m_First && Time.realtimeSinceStartup < m_Next) return false;
            m_First = false;
            m_Next = Time.realtimeSinceStartup + m_Interval;
            m_Counter++;
            System.Array.Copy(m_Base, m_Frame, m_Base.Length);
            // Moving vertical bar and the binary frame counter (16 blocks along the bottom edge).
            int barX = (m_Counter * 6) % m_W;
            for (int y = 0; y < m_H; y++) for (int x = barX; x < barX + 8 && x < m_W; x++) m_Frame[y * m_W + x] = Px(255, 255, 255, 255);
            for (int b = 0; b < 16; b++)
            {
                bool on = ((m_Counter >> b) & 1) != 0;
                Fill(m_Frame, m_W / 2 - 16 * 14 + b * 28, m_H - 70, 24, 24, on ? Px(255, 255, 255, 255) : Px(30, 30, 30, 255));
            }
            if (!target || target.width != m_W || target.height != m_H)
            {
                if (target) Object.Destroy(target);
                target = new Texture2D(m_W, m_H, TextureFormat.BGRA32, false, false) { name = "PatternFrame", wrapMode = TextureWrapMode.Clamp };
            }
            // m_Frame is stored TOP row first, matching the capture helper's frames, so no flip is needed (see ScreenSource).
            target.SetPixelData(m_Frame, 0);
            target.Apply(false);
            return true;
        }

        public void Dispose() { }

        void Build()
        {
            for (int y = 0; y < m_H; y++)
                for (int x = 0; x < m_W; x++)
                {
                    float gx = x / (float)m_W, gy = y / (float)m_H;
                    byte r = (byte)(40 + 50 * gx), g = (byte)(40 + 50 * gy), b = (byte)(70 + 40 * (1f - gx));
                    bool grid = x % 120 == 0 || y % 120 == 0;
                    bool fine = x % 30 == 0 || y % 30 == 0;
                    if (grid) { r = g = b = 150; }
                    else if (fine) { r = (byte)(r + 12); g = (byte)(g + 12); b = (byte)(b + 12); }
                    m_Base[y * m_W + x] = Px(r, g, b, 255);
                }
            // Diagonal from the top-left to the bottom-right.
            for (int i = 0; i < m_H; i++) { int x = (int)((long)i * m_W / m_H); for (int t = -1; t <= 1; t++) Set(m_Base, x + t, i, Px(255, 160, 0, 255)); }
            // Border.
            for (int i = 0; i < m_W; i++) for (int t = 0; t < 4; t++) { Set(m_Base, i, t, Px(255, 255, 255, 255)); Set(m_Base, i, m_H - 1 - t, Px(255, 255, 255, 255)); }
            for (int i = 0; i < m_H; i++) for (int t = 0; t < 4; t++) { Set(m_Base, t, i, Px(255, 255, 255, 255)); Set(m_Base, m_W - 1 - t, i, Px(255, 255, 255, 255)); }
            // Corner blocks.
            int s = m_H / 6;
            Fill(m_Base, 6, 6, s, s, Px(230, 40, 40, 255));                          // TL red
            Fill(m_Base, m_W - 6 - s, 6, s, s, Px(40, 200, 60, 255));                // TR green
            Fill(m_Base, m_W - 6 - s, m_H - 6 - s, s, s, Px(50, 90, 240, 255));      // BR blue
            Fill(m_Base, 6, m_H - 6 - s, s, s, Px(240, 220, 40, 255));               // BL yellow
            // A big "F" in the middle (stem, top bar, middle bar): not symmetric, so a mirror or a turn is obvious.
            int fx = m_W / 2 - m_H / 6, fy = m_H / 2 - m_H / 4, t2 = m_H / 14, fh = m_H / 2, fw = m_H / 3;
            var white = Px(250, 250, 250, 255);
            Fill(m_Base, fx, fy, t2, fh, white); Fill(m_Base, fx, fy, fw, t2, white); Fill(m_Base, fx, fy + fh / 2 - t2 / 2, fw * 3 / 4, t2, white);
            // A 10% inset marker and a small crop-test box at (0.7..0.9, 0.1..0.3) in source coordinates (a "minimap").
            Fill(m_Base, (int)(m_W * 0.7f), (int)(m_H * 0.1f), (int)(m_W * 0.2f), (int)(m_H * 0.2f), Px(20, 20, 20, 255));
            for (int i = 0; i < 5; i++) Fill(m_Base, (int)(m_W * 0.72f) + i * 60, (int)(m_H * 0.13f) + i * 28, 40, 40, Px(60, 220, 255, 255));
        }

        static void Fill(Color32[] buf, int w, int h, int x0, int y0, int rw, int rh, Color32 c)
        {
            for (int y = Mathf.Max(0, y0); y < Mathf.Min(h, y0 + rh); y++)
                for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x0 + rw); x++) buf[y * w + x] = c;
        }

        void Fill(Color32[] buf, int x0, int y0, int rw, int rh, Color32 c) => Fill(buf, m_W, m_H, x0, y0, rw, rh, c);
        void Set(Color32[] buf, int x, int y, Color32 c) { if (x >= 0 && y >= 0 && x < m_W && y < m_H) buf[y * m_W + x] = c; }
    }
}
