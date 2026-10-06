# Architecture

## Capture pipeline
`XrssCapture.exe` (Tools/XrssCapture, .NET 8, `net8.0-windows10.0.19041`) uses Windows.Graphics.Capture on a window handle (works for games and covered windows; no desktop duplication limits). Each frame is copied GPU->staging->CPU into a double-buffered memory-mapped file `XrssFrame-<id>`; Unity polls the header, uploads the newest BGRA frame to a Texture2D (`LoadRawTextureData`) and blits it into a mip-mapped RenderTexture (anisotropic filtering for steeply angled screens). Per-frame header also carries the window's client rectangle in screen coordinates and foreground state (used for input forwarding). The helper exits when Unity's process dies.

Why a sidecar: Unity's Mono runtime cannot host the WinRT capture types cleanly and no C++ toolchain is required on the dev machine. The transport is a deliberate v1 (CPU copy, ~8 MB/frame at 1080p); the interface (`ICaptureBackend`) allows a later zero-copy shared D3D11 texture backend (Codex task).

MMF layout: see `Tools/XrssCapture/FrameProtocol.cs` and `Assets/Scripts/Capture/FrameProtocol.cs` (kept identical; tests assert the constants).

## Spatial model
A *surface* = 4 corners in tracking space (TL, TR, BR, BL) + a source id + a crop rectangle. Corners are kept planar (projected onto their best-fit plane) and convex. The picture is mapped with the projective map taking the corners to the unit square (per-vertex (uq, vq, q), divided per pixel), so any convex planar quad shows an undistorted picture from the placing viewpoint. 3D polygons with more than four points are not supported: the point-based UX is kept but the display surface is a quad.

## Interaction
- Right trigger: place the next point (1..4) in "Place" mode; confirm.
- Hover a panel corner + hold trigger: drag that corner (stays planar).
- Hold grip on a panel: move/rotate it rigidly with the hand; thumbstick Y pushes/pulls, X scales.
- Crop mode: drag a rectangle on a panel to spawn a new panel referencing the same source.
- Left wrist palette (world-space, pointed at with the ray): New Screen, Crop, Delete, Interact toggle, Save.
- Desktop control panel (IMGUI, mouse): choose the window to capture, list/reset surfaces.

## Input forwarding
Ray -> panel UV -> source UV (crop-aware) -> window client pixel -> screen coordinates (from the frame header) -> `SetCursorPos` + `SendInput` (trigger = left click, grip-click/B = right click, stick = wheel). Absolute pointer only: games that capture the mouse (FPS look) do not work with forwarded absolute input. This is a documented limitation.

## Passthrough / MR readiness
The camera background is controlled by `BackgroundMode` (Void, Grid, Passthrough). Passthrough is a stub that only logs; the render path uses a transparent clear colour and alpha-correct panel shader, so enabling an OpenXR environment blend mode later needs no change to panels.

## Persistence
`Application.persistentDataPath/layouts/<app-key>.layout.json` (JSON, atomic write). Sources are matched by process name + window-title substring, so a layout reattaches to a relaunched game. Coordinates are in tracking (stage) space.
