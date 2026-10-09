using System;
using UnityEngine;

namespace XrSpatial.Capture
{
    /// <summary>Where the captured window sits on the desktop (client area, screen pixels), for forwarding pointer input back to it.</summary>
    public struct WindowGeometry
    {
        public int x, y, width, height;
        /// <summary>Window handle (low 32 bits are enough on Windows) so input can be directed at it; 0 for non-window sources.</summary>
        public long hwnd;
        public bool foreground, minimized, alive;
        public bool IsValid => width > 0 && height > 0;
    }

    /// <summary>
    /// A producer of frames of a flat PC image. Implementations: <see cref="SidecarBackend"/> (Windows.Graphics.Capture via the XrssCapture helper) and
    /// <see cref="PatternBackend"/> (a generated test image). A future zero-copy shared-texture backend plugs in here.
    /// </summary>
    public interface ICaptureBackend : IDisposable
    {
        /// <summary>One-line human status ("capturing 1920x1080 @ 60 fps", "waiting for the helper", an error message).</summary>
        string Status { get; }
        bool IsRunning { get; }
        /// <summary>The source has permanently ended (window closed, helper exited); the owner may restart it.</summary>
        bool HasEnded { get; }
        int Width { get; }
        int Height { get; }
        float CaptureFps { get; }
        WindowGeometry Window { get; }
        /// <summary>If a new frame is available, uploads it into <paramref name="target"/> (created or resized as needed, BGRA32) and returns true. Main thread only.</summary>
        bool PollInto(ref Texture2D target);
    }
}

namespace XrSpatial.Capture
{
    /// <summary>Optional: a backend that can also supply a depth map for the picture it produces (0 = far, 1 = near, same aspect as the picture). Used only by the Depth Lab.</summary>
    public interface IDepthProvider
    {
        Texture2D Depth { get; }
    }
}
