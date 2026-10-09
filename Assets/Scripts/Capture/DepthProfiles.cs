using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>
    /// Depth Lab, gate D2: simple fixed depth shapes that can be applied to a LIVE source (a real window) while real per-pixel depth does not exist yet.
    /// They only bend the whole picture (like a curved monitor); they know nothing about what is in the window. 0 = far, 1 = near.
    /// </summary>
    public static class DepthProfiles
    {
        public enum Profile { Off, Curved, Dome, Tilt }

        /// <summary>The shape applied to live window and monitor sources; Off leaves them flat even when Depth is on.</summary>
        public static Profile Live = Profile.Off;

        static readonly Texture2D[] s_Textures = new Texture2D[4];

        public static string Name(Profile p) => p == Profile.Curved ? "Curved (edges toward you)" : p == Profile.Dome ? "Dome (centre toward you)" : p == Profile.Tilt ? "Tilt (top far)" : "Off";

        public static void Next() => Live = (Profile)(((int)Live + 1) % 4);

        /// <summary>The depth texture for the current live profile, or null when Off. Built once per shape.</summary>
        public static Texture2D LiveTexture
        {
            get
            {
                if (Live == Profile.Off) return null;
                int i = (int)Live;
                if (s_Textures[i]) return s_Textures[i];
                const int w = 128, h = 72;
                var bytes = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h * 2f - 1f;          // -1..1, v grows downwards (row 0 = top)
                        float d;
                        switch (Live)
                        {
                            case Profile.Curved: d = 0.15f + 0.85f * u * u; break;                  // a cylinder: the sides come toward you
                            case Profile.Dome: d = Mathf.Clamp01(1f - 0.85f * (u * u * 0.6f + v * v * 0.9f)); break;
                            default: d = Mathf.Clamp01(0.5f + 0.5f * v); break;                     // the bottom is near, the top far
                        }
                        bytes[y * w + x] = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(d));
                    }
                var t = new Texture2D(w, h, TextureFormat.R8, true, true) { name = "DepthProfile-" + Live, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
                t.SetPixelData(bytes, 0); t.Apply(true);
                return s_Textures[i] = t;
            }
        }
    }
}
