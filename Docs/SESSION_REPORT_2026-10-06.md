# Session report: XR Spatial Screens, session 1 (2026-10-06)

**Repo:** `rapsure777-a11y/xr-spatial-screens` (private). **Branch:** `develop`. **Latest commit:** `57f43acf8ff61fd59e5d0c573e912b332a7df421` (plus the commit that adds this report). `main` is intentionally still the scaffold commit `cdc83b4`: it moves only after a headset test.
**Unity:** 6000.3.9f1 primary, 6000.6.4f1 checked. **Author:** Claude (integration owner). **Codex branches:** none exist; Codex cannot be launched from this session, so its work is specified in `Docs/CODEX_TASKS.md`.

## 1. Bottom line

The application exists end to end in a flat-window form: it captures a real Windows window, shows it in 3D on a screen the user defines by placing four points, supports corner editing, moving, scaling and cropping into independent panels, saves layouts per application, and can forward pointer clicks. All of that is covered by automated tests that pass on both Unity versions, and the live-capture path was seen working on a real window.

**It has never been run in a headset.** The VR controller code is copied from the proven Worlds of Mini Golf project but has not been exercised here. Live click injection also could not be verified in this environment (details in section 5). Treat the VR experience as untested until you try it.

## 2. What was built

| Area | Where | What it does |
|---|---|---|
| Spatial math | `Assets/Scripts/Core/QuadMath.cs` | Plane fitting, convexity/validity checks, winding so a picture is never mirrored for the viewer, projective (perspective-correct) picture mapping for any planar quad, ray to picture coordinate and inverse, crop math, aspect fitting |
| Layout data | `Assets/Scripts/Core/Layout.cs` | Sources, surfaces (4 corners + crop rectangle), JSON save/load, atomic write, per-application key |
| Capture helper | `Tools/XrssCapture` (.NET 8) | Windows.Graphics.Capture of a window or monitor into a shared-memory frame buffer; window list; self-contained publish of about 41 MB |
| Capture client | `Assets/Scripts/Capture` | Starts the helper hidden, maps its memory with Win32 `OpenFileMapping`/`MapViewOfFile`, uploads the newest frame, builds a mip-mapped anisotropic view texture; test-pattern source; restart if the window reappears |
| Renderer | `Assets/Resources/Shaders/XrSpatialPanel.shader`, `PanelView.cs` | Per-pixel projective mapping with crop, corner handles, edge highlight |
| Tools | `Assets/Scripts/Spatial/SurfaceTool.cs` | Place 4 points, drag one corner (stays planar), grip-grab move/turn/scale, crop drag, delete, turn picture, fit aspect, interact mode |
| Input forwarding | `Spatial/InputForwarder.cs` | Laser to picture to captured-window rectangle to `SendInput` (left/right click, wheel), with a focus step |
| UI | `Assets/Scripts/App` | World-space wrist palette (two pages, including an in-VR window picker), desktop IMGUI control panel, simulated desktop pointer, floor grid, demo layout |
| XR input | `XrPointerSource.cs`, `HandInput.cs`, `SteamFrameControllerProfile.cs` | Head and two hands via the Input System, bound by usage (any OpenXR profile), floor tracking origin |
| Build/automation | `Assets/Scripts/Editor/Automation.cs`, `Tools/*.ps1` | Project configuration (OpenXR single-pass instanced, D3D11, linear), scene, player build, test and screenshot runners |

Design decisions worth knowing:

- **Four-point screens only.** The point-based UX is kept, but the display surface is a convex planar quad. Arbitrary polygons make the picture mapping ambiguous. Points are projected onto their best-fit plane; bad shapes (bow-tie, reflex corner, tiny) are rejected with a message.
- **Projective mapping, not affine.** A trapezoid or any tilted quad shows an undistorted picture (diagonal-intersection method, divided per pixel in the shader), which is what makes freely angled screens look right.
- **A separate capture process.** WinRT capture does not sit well in Unity's Mono runtime, and no C++ toolchain is installed on this PC. The transport (CPU copy through shared memory) is a deliberate first version behind the `ICaptureBackend` interface; a zero-copy shared-texture backend is the first Codex task.
- **No scene file dependencies.** The whole application is built at runtime by `SpatialApp`; a one-object scene is only needed for the build.
- **Passthrough readiness.** The background mode is an enum with a passthrough stub, and the shader and clear colour need no change to enable passthrough later. Nothing depends on Steam Frame specific APIs.

## 3. What was actually tested

| Check | Result |
|---|---|
| Unity 6000.3.9f1 EditMode | 24/24 pass (math, layout, protocol constants vs the helper copy, window-list parsing, input mapping, aspect fitting) |
| Unity 6000.3.9f1 PlayMode | 12/12 pass: scripted-pointer tests for placing 4 points, anticlockwise order fix, bad-shape rejection, corner drag stays planar, grip move and scale, crop panel creation, auto-fit, layout save/reload with crops, shared source texture; plus real-window capture through the helper (size, orientation, window position) and a clear error when no window matches |
| Unity 6000.6.4f1 (on a copy) | EditMode 24/24; PlayMode 11 passed, 0 failed, 1 skipped (the live-capture test needs the helper/fixture built in that copy) |
| Windows player build | Builds with 0 errors (about 140 MB player folder, helper about 41 MB self-contained) |
| Flat player, visual | Live Notepad window captured and shown in 3D (title bar, tabs, text, correct aspect and orientation); a demo of six freely angled screens plus a cropped minimap, picture upright and not mirrored |
| Capture helper | `--list`, `--snap` on two real windows, `--run` against Notepad and a test window |

Bugs found and fixed along the way (all caught by looking at the output or by tests): a left-handed winding error in the "is this quad clockwise for the viewer" test; red/blue swapped in the test pattern (BGRA order); the picture window being the full frame (title bar included) rather than the client area, which matters for click mapping; Unity's Mono memory-map API not reliably opening the helper's named map (replaced with direct Win32 calls); a quick-screen created before the first frame had the wrong aspect; the helper's last error line being lost when it exited immediately; a focus step missing before forwarded clicks; and the Mini Golf package manifest breaking Unity 6.6 (`com.unity.modules.vr` was removed there), now trimmed to what the project uses.

## 4. Git and process

- Branches: `main` (scaffold only), `develop` (integrated, pushed), `claude/spatial-compositor-core` (the work branch, pushed). I committed once to the wrong branch (`main`) before noticing; it was local only, and I moved it onto the work branch and restored `main` before anything was pushed.
- Docs: `Docs/CURRENT_STATE.md` (current architecture, what works, what is broken, files in flux, Codex boundaries, next tasks), `Docs/ARCHITECTURE.md`, `Docs/CODEX_TASKS.md`.
- A memory entry for this project was added so a later session can pick it up.

## 5. Known issues and honest limits

1. **Never run in a headset.** Unverified: aim pose and trigger/grip/stick/menu bindings on the Steam Frame controllers, palette toggle on the off hand, controller stand-in size and placement, floor origin behaviour in SteamVR, comfortable sizes and distances.
2. **Click forwarding:** superseded. It was later verified end to end on the desktop (see `Docs/INPUT_FORWARDING.md`); an earlier claim here that the sandbox swallowed injected input was wrong. Still unverified in the headset.
3. **Limits by design:** absolute pointer only (mouse-look games will not respond); frames above 3840x2160 are rejected; Windows only delivers capture frames when a window changes, so a static window reports "static"; forwarded clicks cannot reach an app running as administrator (the app shows a message if Windows refuses).
4. **Performance is unmeasured in VR.** The upload path is one memory copy per frame (about 8 MB at 1080p) plus a GPU blit for mip generation; fine for a handful of panels on this PC in the flat player, unknown in a headset.
5. Not done by request: screen thickness, glow, shadow, curvature. Not done for lack of a way to test: passthrough.

## 6. What Codex should and should not do

Do not touch `Assets/Scripts/Spatial` or `Assets/Scripts/App` yet (the interaction design will change after the first headset test). Good isolated tasks, with acceptance criteria, are in `Docs/CODEX_TASKS.md`: zero-copy capture, helper hardening, input-forwarding verification, math fuzzing, layout migration, performance profiling.

## 7. Recommended next steps

1. Headset test: do the four points land where you point, can you edit and grab them comfortably, is a captured game readable at distance, does the palette behave.
2. Verify a real forwarded click on a normal desktop.
3. Start Codex on zero-copy capture and helper hardening in parallel.
4. After the headset test, decide on layout niceties (snap, duplicate, recentre) and the later visual pass.

## 8. How to run it

```
powershell -File Tools\unity-run.ps1 -Tests EditMode          # also PlayMode, -Version 6000.6.4f1 -Project <copy>
powershell -File Tools\unity-run.ps1 -Method XrSpatial.Editor.Automation.BuildPlayer
powershell -File Tools\run-player.ps1 -Shot Screenshots\a.png -ExtraArgs "--xrss-demo"      # flat-window demo
Builds\Windows\XrSpatialScreens.exe                                                          # in a headset: start SteamVR first
```
