using UnityEngine;

namespace XrSpatial.Spatial
{
    /// <summary>One frame of a pointing device: a controller (or the simulated mouse pointer). Positions are in tracking space (the stage).</summary>
    public struct PointerState
    {
        public bool valid;
        /// <summary>The aim ray (where the controller points).</summary>
        public Ray ray;
        /// <summary>Orientation of the controller, used to rotate a grabbed panel.</summary>
        public Quaternion rotation;
        public bool triggerDown, triggerHeld, triggerUp;
        public bool gripDown, gripHeld, gripUp;
        public bool primaryDown, secondaryDown, menuDown;
        /// <summary>Thumbstick; Y pushes/pulls, X scales.</summary>
        public Vector2 stick;
        /// <summary>The other hand's menu button was pressed this frame (shows/hides the palette).</summary>
        public bool paletteToggle;
    }

    public interface IPointerSource
    {
        /// <summary>Polled once per frame by the tool.</summary>
        PointerState Poll();
        /// <summary>Anchor for the palette (the non-pointing hand's wrist), or null to float it in front of the head.</summary>
        Transform PaletteAnchor { get; }
        /// <summary>Camera/head transform in the stage.</summary>
        Transform Head { get; }
        /// <summary>Short haptic pulse on the pointing hand (no-op without a controller).</summary>
        void Haptic(float amplitude, float seconds);
    }
}
