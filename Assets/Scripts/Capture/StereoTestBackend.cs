using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Test source for side-by-side 3D: a generated 1920x1080 HALF-SBS picture (each eye is 960 wide, squeezed to half, as real half-SBS video is), so the 3D view can be judged without
    /// a media player. Each eye shows the same 16:9 scene with objects at known depths: a grid and white window frame on the screen plane, a green box BEHIND the screen, a gold box
    /// ON the screen, a red box and a yellow ball IN FRONT of it. With the right eye order the red box floats toward you and the green box sits back; with the eyes swapped it reverses
    /// (red behind, green in front), which is the Swap eyes check. A cyan square in the left picture's top-left corner and an orange square in the right picture's top-right corner
    /// are one-eye markers (they flicker slightly because each eye sees only one of them), useful in Flat mode to see which half is which.
    /// </summary>
    public sealed class StereoTestBackend : ICaptureBackend
    {
        public const int W = 1920, H = 1080, EyeW = W / 2;
        readonly Color32[] m_Pixels = new Color32[W * H];     // BGRA, TOP row first
        bool m_Delivered;

        static Color32 Px(int r, int g, int b) => new Color32((byte)b, (byte)g, (byte)r, 255);

        public StereoTestBackend()
        {
            for (int eye = 0; eye < 2; eye++) DrawEye(eye);
        }

        public string Status => "3D test: generated half side-by-side 1920x1080";
        public bool IsRunning => true;
        public bool HasEnded => false;
        public int Width => W;
        public int Height => H;
        public float CaptureFps => 0f;
        public WindowGeometry Window => new WindowGeometry { x = 0, y = 0, width = W, height = H, alive = true, foreground = true };

        public bool PollInto(ref Texture2D target)
        {
            if (m_Delivered) return false;
            m_Delivered = true;
            if (!target || target.width != W || target.height != H)
            {
                if (target) Object.Destroy(target);
                target = new Texture2D(W, H, TextureFormat.BGRA32, false, false) { name = "StereoTestFrame", wrapMode = TextureWrapMode.Clamp };
            }
            target.SetPixelData(m_Pixels, 0);
            target.Apply(false);
            return true;
        }

        public void Dispose() { }

        // ------------------------------------------------------------------ drawing (logical 1920x1080 coordinates, written squeezed into the eye's half)

        /// <summary>Puts a logical pixel (lx 0..1919) into the eye's squeezed half. The eye picture is 960 wide, so two logical columns share one stored column.</summary>
        void Put(int eye, int lx, int y, Color32 c)
        {
            if (lx < 0 || lx >= W || y < 0 || y >= H) return;
            m_Pixels[y * W + eye * EyeW + lx / 2] = c;
        }

        /// <summary>Disparity in logical pixels: positive = nearer than the screen (left eye sees it shifted right, right eye shifted left).</summary>
        static int Shift(int eye, int disparity) => eye == 0 ? disparity / 2 : -disparity / 2;

        void Rect(int eye, int x0, int y0, int x1, int y1, int disparity, Color32 c)
        {
            int s = Shift(eye, disparity);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x += 2) Put(eye, x + s, y, c);
        }

        void Ball(int eye, int cx, int cy, int r, int disparity, Color32 c, Color32 rim)
        {
            int s = Shift(eye, disparity);
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x += 2)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= r) Put(eye, x + s, y, d > r - 8 ? rim : c);
                }
        }

        void Frame(int eye, int x0, int y0, int x1, int y1, int t, int disparity, Color32 c)
        {
            Rect(eye, x0, y0, x1, y0 + t, disparity, c); Rect(eye, x0, y1 - t, x1, y1, disparity, c);
            Rect(eye, x0, y0, x0 + t, y1, disparity, c); Rect(eye, x1 - t, y0, x1, y1, disparity, c);
        }

        void DrawEye(int eye)
        {
            // background: dark blue with a light grid on the screen plane
            var back = Px(18, 24, 44);
            for (int y = 0; y < H; y++) for (int x = 0; x < EyeW; x++) m_Pixels[y * W + eye * EyeW + x] = back;
            var grid = Px(70, 90, 140);
            for (int gx = 120; gx < W; gx += 120) Rect(eye, gx - 2, 0, gx + 2, H, 0, grid);
            for (int gy = 120; gy < H; gy += 120) Rect(eye, 0, gy - 2, W, gy + 2, 0, grid);

            // far box (behind the screen), screen-plane box, near box and ball (in front)
            Rect(eye, 260, 300, 700, 760, -50, Px(40, 170, 90)); Frame(eye, 260, 300, 700, 760, 10, -50, Px(200, 255, 220));
            Rect(eye, 760, 380, 1160, 700, 0, Px(240, 190, 40)); Frame(eye, 760, 380, 1160, 700, 10, 0, Px(255, 245, 190));
            Rect(eye, 1240, 300, 1680, 760, 60, Px(220, 50, 50)); Frame(eye, 1240, 300, 1680, 760, 10, 60, Px(255, 210, 210));
            Ball(eye, 960, 880, 90, 100, Px(255, 235, 60), Px(120, 90, 0));

            // screen window frame on the plane (zero disparity), drawn last so it sits over everything
            Frame(eye, 0, 0, W, H, 14, 0, Px(240, 240, 250));

            // one-eye markers
            if (eye == 0) Rect(eye, 40, 40, 160, 160, 0, Px(40, 220, 240)); else Rect(eye, W - 160, 40, W - 40, 160, 0, Px(250, 150, 40));
        }
    }
}
