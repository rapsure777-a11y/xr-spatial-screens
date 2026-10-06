# CURRENT STATE (XR Spatial Screens)

Updated: 2026-10-06 (session 1, in progress). Owner/integrator: Claude. Specialists: Codex (branches `codex/<task>`, never merged directly).

- **Branch:** `develop` (integration). `main` = stable only.
- **Unity:** 6000.3.9f1 (primary), 6000.6.4f1 (compile/test check). OpenXR 1.18, URP 17.3, Input System 1.20, D3D11, Mono.
- **Build/test:** EditMode 16/16 (math + layout). Capture helper, runtime, UI: not built yet (see "Working / Broken").

## Product
A spatial compositor: capture a Windows window/game, then *draw* where its screen exists by placing 4 corner points with a VR controller. The image is mapped projectively onto that quad (correct under any tilt). Corners can be grabbed and edited. Rectangles of the source can be cropped into independent panels. Layouts are saved per application. See `Docs/ARCHITECTURE.md`.

## Architecture (short)
- `XrSpatial.Core` (pure math/data, no scene deps): `QuadMath` (plane fit, ordering, projective UV, ray->UV, crop math), `Layout`/`SurfaceDef`/`SourceDef` (+JSON).
- `XrSpatial.Capture`: `ICaptureBackend`; `WgcSidecarBackend` talks to the external `XrssCapture.exe` (.NET 8, Windows.Graphics.Capture) over a memory-mapped file; `PatternBackend` (generated test image).
- `XrSpatial.Spatial`: panel mesh + shader, controller tools (place points, edit corners, move/scale, crop), input forwarding, persistence.
- `XrSpatial.App`: bootstrap that builds the whole scene at runtime (no scene dependencies), desktop control panel (IMGUI), simulated hands for non-VR testing.
- `XrSpatial.XR`: Steam Frame controller profile (copied from Worlds of Mini Golf).
- `Tools/XrssCapture`: the capture helper (separate process: keeps WinRT/D3D capture out of Unity's Mono runtime).

## Working
- Core math + layout persistence (EditMode tests 16/16).

## Known broken / not built yet
- Everything beyond Core (this file is updated as milestones land).

## Files being modified right now
- Tools/XrssCapture (new), Assets/Scripts/Capture (new).

## Areas Codex should not touch (yet)
- `Assets/Scripts/Spatial`, `Assets/Scripts/App` (architecture in flux). Good Codex tasks right now: `Tools/XrssCapture` backend hardening and zero-copy shared-texture transport; input-coordinate mapping tests; serialization migrations; perf profiling.

## Next highest-value tasks
1. Capture helper + Unity capture client; verify a real window shows in Unity.
2. Panel renderer (projective shader, mip/aniso), desktop test scene with panels at odd angles.
3. VR controller tools (place 4 points, edit corners, grab/scale, crop).
4. Layout save/restore per app; input forwarding.
