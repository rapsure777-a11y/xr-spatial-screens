# CURRENT STATE (XR Spatial Screens)

Updated: 2026-10-06 (session 1). Owner/integrator: Claude. Specialists: Codex (branches `codex/<task>`, reviewed before merging; Codex cannot be launched from this session, so no Codex branches exist yet).

- **Branch:** `develop` (integration). `main` = stable only (still the scaffold; promote after the first headset test).
- **Unity:** 6000.3.9f1 primary. 6000.6.4f1 compile/test checked (see Build/test). OpenXR 1.18, URP 17.3, Input System 1.20, D3D11, Mono, Windows x64.
- **Build/test:** EditMode 21/21, PlayMode 8/8 (6000.3.9f1). Windows player builds (98 MB + helper). Not yet run in a headset.

## Product
A spatial compositor: capture a Windows window/monitor, then *draw* where its screen exists by placing 4 corner points with a VR controller. The image is mapped projectively onto that quad (correct under any tilt). Corners can be grabbed and edited, the whole screen can be moved/turned/scaled, rectangles of the source can be cropped into independent panels, layouts are saved per application. See `Docs/ARCHITECTURE.md`.

## Working (verified)
- **Capture:** `Tools/XrssCapture` (Windows.Graphics.Capture, .NET 8) captures a real window (verified live with Notepad: title bar, text, tabs) into a shared memory map; Unity maps it with Win32 `OpenFileMapping`/`MapViewOfFile` and uploads BGRA frames; mip-mapped, anisotropic view texture. Self-contained single-file publish works (`Tools\build-helper.ps1 -Publish`, 42 MB).
- **Rendering:** `XrSpatial/Panel` shader with per-pixel projective mapping + crop; arbitrary planar convex quads incl. trapezoids; verified in screenshots (pattern not mirrored, upright, aspect from the source).
- **Interaction (scripted-pointer PlayMode tests):** place 4 points -> screen (order fixed for the viewer, planarised, bad shapes rejected), drag a corner (stays planar), grip-grab move/turn/scale, crop drag -> new panel on the same source, save/reload layout (crops included).
- **UI:** world-space palette (UGUI, laser hit-test), desktop IMGUI control panel (window picker, sources, screens, save/load), simulated desktop pointer for no-headset work.
- **Persistence:** JSON per app key in `persistentDataPath/layouts`, atomic write, sources re-found by process + title.
- **Input forwarding:** implemented (laser -> panel UV -> crop-aware source UV -> window rect -> SendInput); the math is unit-tested; the live click path has NOT been verified end to end.

## Known broken / untested / limits
- **Never run in a headset.** VR controller bindings (`XrPointerSource`, `HandInput`) are copied from the proven Worlds of Mini Golf project but unverified here (aim pose, grip/trigger/stick/menu, palette toggle on the off hand's menu/primary).
- Capture transport is a CPU copy (one memcpy per frame, ~8 MB at 1080p); windows larger than 3840x2160 are rejected. WGC delivers frames only when the window changes (a static window reports "static").
- Absolute-pointer forwarding only (no raw mouse for FPS-style games); focus handling = SetForegroundWindow on click.
- Only quad (4-point) screens; no curvature/thickness/glow yet (by request).
- Passthrough/MR: stub only (`BackgroundMode.Passthrough` logs). Steam Frame specifics not required.
- Player build is not yet launched on a headset; `main` not yet promoted.

## Files being modified right now
- Packages/manifest.json (trimmed for 6.6 compatibility), Docs, tests.

## Areas Codex should not touch (architecture in flux)
- `Assets/Scripts/Spatial`, `Assets/Scripts/App`. Good Codex tasks now (isolated): zero-copy shared-D3D11-texture capture backend (`ICaptureBackend`), helper hardening (window resize, DPI, protected content, multi-monitor), more `QuadMath` property tests, layout migration, perf profiling of the upload path, real-window input-forwarding verification.

## Next highest-value tasks
1. Headset test (Steam Frame via SteamVR): confirm controller bindings, palette, 4-point placement, corner editing, readability of a captured game at distance; tune sizes.
2. Verify live input forwarding against a real window; improve focus behaviour.
3. Zero-copy capture transport (Codex).
4. Layout niceties: per-surface aspect lock, snap/grid, copy/duplicate, hide/show from palette, recenter.
5. Later visual pass (thickness, glow, shadow, curvature) only after judging flat quality in the headset.
