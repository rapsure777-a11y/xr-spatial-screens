# Codex task briefs (ready to paste)

Rules for every task: branch `codex/<task>` from the latest `develop`; push to GitHub; do NOT merge into `develop` (Claude reviews, builds, tests and merges); do NOT touch `Assets/Scripts/Spatial` or `Assets/Scripts/App` unless the task says so; keep the existing tests green and add tests; finish with a handoff note (what changed, what was actually tested, what is unverified). Read `Docs/ARCHITECTURE.md` and `Docs/CURRENT_STATE.md` first.

Build/test commands (PowerShell, from the repo root): `powershell -File Tools\build-helper.ps1` (capture helper), `powershell -File Tools\unity-run.ps1 -Tests EditMode` / `-Tests PlayMode` (Unity 6000.3.9f1), `powershell -File Tools\unity-run.ps1 -Method XrSpatial.Editor.Automation.BuildPlayer` then `powershell -File Tools\run-player.ps1 -Shot Screenshots\x.png -ExtraArgs "--xrss-pattern --xrss-quickscreen"`.

---

## 1. Zero-copy capture transport  (`codex/zero-copy-capture`)
Today `Tools/XrssCapture` copies each frame GPU -> staging -> CPU -> shared memory, and Unity uploads it again (`SidecarBackend`). Replace that with a shared D3D11 texture: the helper creates a texture with `D3D11_RESOURCE_MISC_SHARED_NTHANDLE | SHARED_KEYEDMUTEX`, copies the WGC frame into it on the GPU, and publishes the NT handle / name; Unity opens it on its own device and wraps it with `Texture2D.CreateExternalTexture` (or copies it GPU-side into the existing `ScreenSource.View`). Keep `ICaptureBackend` as the seam: add `SharedTextureBackend` beside `SidecarBackend`, select it automatically when the OS/GPU supports it, fall back otherwise. Window geometry/flags stay in the shared-memory header (`FrameProtocol`). Acceptance: 4K window at 60 fps with < 1 ms main-thread cost; no tearing (keyed mutex); resize handled; the CPU path remains as a fallback; both pass the existing tests. Files: `Tools/XrssCapture/*`, `Assets/Scripts/Capture/*`. Unity can only reach D3D11 device pointers from native code or via `Texture2D.GetNativeTexturePtr` + COM vtable calls; if that proves infeasible without a C++ plugin, document why and propose the smallest native plugin (no C++ toolchain is installed on the dev PC; it can be installed).

## 2. Capture helper hardening  (`codex/capture-helper-hardening`)
Window resize while capturing, DPI-scaled windows, minimised/restored windows, protected (DRM) content (black frames -> report "protected content"), multi-monitor and negative monitor origins, the window moving between monitors, UWP/packaged apps, helper restart after a crash, `--list` speed. Add a console test mode that captures N windows for T seconds and prints achieved fps, dropped frames and per-frame CPU cost. Files: `Tools/XrssCapture/*` only (+ docs). Acceptance: a written test matrix with results.

## 3. Input-coordinate mapping tests and live forwarding verification  (`codex/input-forwarding`)
`InputForwarder` maps laser hit -> panel UV -> crop-aware source UV -> window rectangle -> `SendInput`. The pure math has unit tests (`CaptureProtocolTests`); the live path is unverified. Write an automated live test (a test window with known click targets, e.g. a tiny WinForms/WPF app in `Tools/`) that proves clicks land on the right pixel for: full panels, cropped panels, tilted panels, windows on a second monitor, DPI 100/150/200 %. Fix mapping bugs you find (limit edits to `InputForwarder.cs` + `Capture` geometry fields). Document the focus/foreground behaviour and what fails (games using raw input).

## 4. QuadMath property tests and numeric robustness  (`codex/quad-math-hardening`)
Fuzz `Assets/Scripts/Core/QuadMath.cs`: near-degenerate quads, very large/small screens, extreme perspective (one edge 10x longer than the opposite), coordinates far from the origin (float precision), planarisation stability, ray/plane grazing angles. Compare the projective map against an independent homography solver. Report and fix defects. Files: `Assets/Scripts/Core`, `Assets/Tests/EditMode`.

## 5. Layout versioning and migration  (`codex/layout-migration`)
`Layout`/`SurfaceDef` are JSON via `JsonUtility`. Add explicit versioning, forward-compatible loading (unknown fields), a migration chain with tests, corruption recovery (keep a `.bak`, never lose a layout), and an import/export command. Files: `Assets/Scripts/Core/Layout.cs`, tests.

## 6. Performance profiling  (`codex/perf-profile`)
Measure frame time with 1, 4 and 8 panels (some cropped) of 1080p / 1440p / 4K sources in the flat player and, if a headset is available, in VR: main-thread cost of `PollInto` + `Graphics.Blit` (mip generation), GPU cost of the panel shader with anisotropic filtering, memory. Propose and implement safe wins (e.g. update mips only when the frame changed, skip hidden panels, downscale far panels). Files: `Assets/Scripts/Capture/ScreenSource.cs` and tools; report numbers in `Docs/perf/`.
