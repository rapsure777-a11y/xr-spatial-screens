using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>Whether the headset draws the PC's mouse cursor on a screen. Off is the default and means no cursor traffic at all.</summary>
    public enum PcCursorMode { Off = 0, Normal = 1, High = 2 }

    /// <summary>
    /// Pure maths for the PC cursor overlay. The PC host reports where Windows has the cursor as a position inside the streamed window (u, v in 0..1, top-left origin, the same
    /// space the pointer events use); the headset turns that into a position on a panel (crop and 3D aware) and a size that reads the same at any distance.
    /// </summary>
    public static class CursorMath
    {
        // cursor shapes sent by the host (Windows standard cursors); anything else (a game's own cursor) is sent as Other and drawn as an arrow
        public const byte ShapeArrow = 0, ShapeIBeam = 1, ShapeHand = 2, ShapeBusy = 3, ShapeCross = 4, ShapeSizeAll = 5, ShapeSizeWE = 6, ShapeSizeNS = 7, ShapeSizeNWSE = 8, ShapeSizeNESW = 9, ShapeNo = 10, ShapeOther = 255;

        public const byte FlagInside = 1, FlagShowing = 2;

        public static PcCursorMode Next(PcCursorMode m) => m == PcCursorMode.Off ? PcCursorMode.Normal : m == PcCursorMode.Normal ? PcCursorMode.High : PcCursorMode.Off;
        public static string Name(PcCursorMode m) => m == PcCursorMode.Normal ? "Normal" : m == PcCursorMode.High ? "High" : "Off";

        /// <summary>Where on a panel a source position lands. False when the position is outside the rectangle the panel shows (a crop, or one eye's picture in a 3D mode): such a panel shows no cursor.
        /// <paramref name="shown"/> is the rectangle the panel shows (<see cref="StereoMath.InputRect"/>, the same one the controller input maps through). The panel UV has v up, like <see cref="QuadMath.UvToWorld"/>.</summary>
        public static bool SourceToPanel(Vector2 src, Rect shown, out Vector2 panelUv)
        {
            panelUv = QuadMath.SourceToPanelUv(src, shown);
            const float eps = 1e-4f;
            return panelUv.x >= -eps && panelUv.x <= 1f + eps && panelUv.y >= -eps && panelUv.y <= 1f + eps;
        }

        /// <summary>Visual height of the cursor in metres at a distance: a fixed angle, so it reads the same on a small near screen and a big far one. Normal 1.7 degrees (about 3 cm at 1 m), High 2.5 degrees.</summary>
        public static float Height(float distance, PcCursorMode mode)
        {
            float degrees = mode == PcCursorMode.High ? 2.5f : 1.7f;
            return Mathf.Clamp(distance, 0.3f, 12f) * Mathf.Tan(degrees * Mathf.Deg2Rad);
        }

        /// <summary>The host's wire message for a cursor position: pixel (px, py) of the desktop inside the window rectangle (left, top, w, h). False when the pixel is outside it.</summary>
        public static bool PixelToWindowUv(int px, int py, int left, int top, int w, int h, out float u, out float v)
        {
            u = v = 0f;
            if (w <= 0 || h <= 0 || px < left || py < top || px >= left + w || py >= top + h) return false;
            u = (px - left) / (float)w; v = (py - top) / (float)h;
            return true;
        }
    }
}
