using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>Which transport new PC-window streams use: JPEG (the proven default) or hardware-encoded HEVC. Saved on the headset; takes effect when a screen's stream is (re)opened.</summary>
    public static class VideoMode
    {
        const string Key = "xrss.codec";
        public static bool Hevc
        {
            get
            {
                try
                {
                    // test hook: <persistentDataPath>/codec.txt containing "hevc" or "jpeg" overrides the saved choice (set over adb)
                    var f = System.IO.Path.Combine(Application.persistentDataPath, "codec.txt");
                    if (System.IO.File.Exists(f)) { var t = System.IO.File.ReadAllText(f).Trim().ToLowerInvariant(); if (t == "hevc") return true; if (t == "jpeg") return false; }
                    return PlayerPrefs.GetInt(Key, 0) == 1;
                }
                catch { return false; }
            }
            set { try { PlayerPrefs.SetInt(Key, value ? 1 : 0); PlayerPrefs.Save(); } catch { } }
        }
    }

    /// <summary>A backend that delivers decoded video as three 8-bit planes instead of an RGBA texture; <see cref="ScreenSource"/> converts them on the GPU into the shared source texture.</summary>
    public interface IYuvBackend
    {
        /// <summary>True when a new picture was uploaded into the three plane textures (created or resized as needed).</summary>
        bool PollYuv(ref Texture2D y, ref Texture2D u, ref Texture2D v, out int width, out int height);
        /// <summary>False while the stream is in (or has fallen back to) JPEG mode and the normal PollInto path applies.</summary>
        bool IsYuvActive { get; }
    }
}
