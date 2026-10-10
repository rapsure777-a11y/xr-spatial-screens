using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>How a panel turns its source picture into what each eye sees. Flat is the normal path and the default for every saved layout.</summary>
    public enum StereoMode { Flat = 0, SbsHalf = 1, SbsFull = 2 }

    /// <summary>
    /// Side-by-side stereo maths (pure, no Unity objects). The source texture holds the left-eye picture in its left half and the right-eye picture in its right half
    /// (swapped if <c>swap</c>). Half-SBS squeezes each eye to half width, Full-SBS stores each eye at full width, so the two are sampled identically and differ only
    /// in the picture shape the panel must have: Half-SBS 1920x1080 is two squeezed 960x1080 pictures that display as one 16:9 picture per eye; Full-SBS 3840x1080 is
    /// two 1920x1080 pictures, also 16:9 per eye. The eye windows are fractions of the SBS frame, which is the panel's crop rectangle of the source (the whole source when uncropped; the crop trims black bars or window borders off a capture).
    /// </summary>
    public static class StereoMath
    {
        /// <summary>Largest convergence shift, as a fraction of one eye picture's width, applied to each eye in opposite directions (so the total change in disparity is twice this).
        /// 1 percent of a 2 m wide panel is 2 cm per eye, 4 cm in total: the most we allow, because larger shifts force the eyes to diverge or cross hard.</summary>
        public const float MaxOffset = 0.01f;
        /// <summary>Step used by the palette's 3D depth buttons.</summary>
        public const float OffsetStep = 0.0025f;

        public static bool IsStereo(StereoMode m) => m != StereoMode.Flat;

        public static StereoMode Next(StereoMode m) => m == StereoMode.Flat ? StereoMode.SbsHalf : m == StereoMode.SbsHalf ? StereoMode.SbsFull : StereoMode.Flat;

        public static string Name(StereoMode m) => m == StereoMode.SbsHalf ? "SBS Half" : m == StereoMode.SbsFull ? "SBS Full" : "Flat";

        public static float ClampOffset(float offset) => float.IsNaN(offset) ? 0f : Mathf.Clamp(offset, -MaxOffset, MaxOffset);

        /// <summary>Offset after one palette step in the given direction (+1 / -1), clamped, with float noise removed so the default returns to exactly 0.</summary>
        public static float StepOffset(float offset, int direction) => Mathf.Round(ClampOffset(offset + direction * OffsetStep) / (OffsetStep / 4f)) * (OffsetStep / 4f);

        /// <summary>
        /// The source U range one eye samples: the left edge and width of that eye's window. <paramref name="eye"/> 0 = left eye, 1 = right eye. Positive <paramref name="offset"/>
        /// moves the picture nearer (each eye's picture shifts toward the other eye's side: left picture to the right, right picture to the left), negative moves it farther.
        /// The window stays inside its own half, so the other eye's picture never bleeds in; the picture's outer edge repeats for the few pixels the shift uncovers.
        /// </summary>
        public static void EyeWindow(int eye, bool swap, float offset, out float x0, out float width)
        {
            int half = (eye == 0) != swap ? 0 : 1;                      // which half of the source this eye reads
            width = 0.5f;
            // the picture moves right when its sampling window moves left, so the left eye's window goes left for a positive offset and the right eye's goes right
            float shift = (eye == 0 ? -1f : 1f) * ClampOffset(offset) * 0.5f;
            x0 = half * 0.5f + shift;
        }

        /// <summary>The shader's _Stereo vector: (left eye window start, right eye window start, left eye half start, right eye half start), all in source U.</summary>
        public static Vector4 ShaderParams(bool swap, float offset)
        {
            EyeWindow(0, swap, offset, out float lx, out _);
            EyeWindow(1, swap, offset, out float rx, out _);
            return new Vector4(lx, rx, swap ? 0.5f : 0f, swap ? 0f : 0.5f);
        }

        /// <summary>Width / height a panel must have to show one eye's picture undistorted, from the source texture's own width / height.</summary>
        public static float PanelAspect(StereoMode mode, float regionAspect)
        {
            float sourceAspect = regionAspect;                              // the SBS frame's own shape: the crop region of the source
            if (mode == StereoMode.SbsHalf) return sourceAspect;        // each eye is squeezed to half width; stretched back it is as wide as the whole source
            if (mode == StereoMode.SbsFull) return sourceAspect * 0.5f; // each eye is half the width at full scale
            return sourceAspect;
        }

        /// <summary>Source rectangle (x, y, w, h normalised, origin top-left) that pointer positions map onto: the left-eye picture inside the crop (or the right one when eyes are swapped), so a click on the panel lands where the same point of one eye's picture is.</summary>
        public static Rect InputRect(StereoMode mode, bool swap, Rect crop)
        {
            if (!IsStereo(mode)) return crop;
            return new Rect(crop.x + (swap ? 0.5f : 0f) * crop.width, crop.y, crop.width * 0.5f, crop.height);
        }
    }
}
