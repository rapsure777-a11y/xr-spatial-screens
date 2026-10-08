using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>
    /// How many pixels of a streamed window a screen in the room needs. Pure geometry from the head position and the screen's corners (no camera projection, which is unreliable in
    /// VR and made the first version swing between 640 and 3840 px). The screen's width is the horizontal angle between its extreme corners as seen from the head; the eye image
    /// has about <see cref="EyePixelsPerFov"/> / <see cref="HorizontalFovDegrees"/> pixels per degree.
    /// </summary>
    public static class StreamMath
    {
        public const float HorizontalFovDegrees = 105f;
        /// <summary>A screen counts as in view up to this many degrees beyond the edge of the view, so a fast head turn finds it already running.</summary>
        public const float InViewMarginDegrees = 60f;

        /// <summary>The source width in pixels this screen needs (before rounding and clamping) and whether it is in or near the view.</summary>
        /// <param name="head">Head position, stage space.</param>
        /// <param name="forward">Direction the head faces.</param>
        /// <param name="corners">The screen's corners in the same space.</param>
        /// <param name="cropWidth">Fraction of the source's width the screen shows (1 = all of it).</param>
        /// <param name="eyeWidth">Width of one eye image in pixels.</param>
        /// <param name="headroom">Source pixels per eye pixel (1.5 = 50 percent more than the screen can show, for sharp text that does not shimmer).</param>
        public static float RequiredSourceWidth(Vector3 head, Vector3 forward, Vector3[] corners, float cropWidth, int eyeWidth, float headroom, out bool inView)
        {
            inView = false;
            if (corners == null || corners.Length == 0) return 0f;
            var flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward;       // looking straight up or down
            flatForward.Normalize();

            var centre = Vector3.zero;
            foreach (var c in corners) centre += c;
            centre /= corners.Length;

            // Corner angles are measured from the direction of the screen's own centre, so a screen behind the head does not wrap around +-180 degrees and read as huge.
            var flatCentre = Vector3.ProjectOnPlane(centre - head, Vector3.up);
            if (flatCentre.sqrMagnitude < 1e-6f) flatCentre = flatForward;            // the screen is directly above or below the head
            flatCentre.Normalize();
            float minAz = float.MaxValue, maxAz = float.MinValue;
            foreach (var c in corners)
            {
                var flat = Vector3.ProjectOnPlane(c - head, Vector3.up);
                if (flat.sqrMagnitude < 1e-6f) continue;
                float az = Vector3.SignedAngle(flatCentre, flat, Vector3.up);
                if (az < minAz) minAz = az;
                if (az > maxAz) maxAz = az;
            }

            float widthDeg = maxAz >= minAz ? Mathf.Min(maxAz - minAz, 180f) : 0f;
            float off = Vector3.Angle(forward, centre - head);
            inView = off - widthDeg * 0.5f < HorizontalFovDegrees * 0.5f + InViewMarginDegrees;

            float pixelsPerDegree = eyeWidth / HorizontalFovDegrees;
            return widthDeg * pixelsPerDegree / Mathf.Max(0.05f, cropWidth) * headroom;
        }

        /// <summary>Round up to a multiple of <paramref name="step"/> and clamp, so small changes in distance do not change the request.</summary>
        public static int Quantize(float width, int step, int min, int max) => Mathf.Clamp(Mathf.CeilToInt(width / step) * step, min, max);
    }
}
