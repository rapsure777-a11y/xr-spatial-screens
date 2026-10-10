# SBS stereo (optional 3D viewing mode) — branch `claude/sbs-stereo`

Built 2026-10-10 from `claude/controller-polish` (head 9f6a3a5, whose controller outline and edge tilt were confirmed in the headset the same day). Status: **code and automated tests done; headset test pending.**

## What it is
A per-panel viewing mode layered onto the existing flat panel. Capture, streaming, audio, passthrough, placement, sizing, controller interaction and Depth Lab are untouched. Modes: **Flat** (default, exactly the old path), **SBS Half**, **SBS Full**, plus **Swap eyes** and a small **3D depth** (convergence) shift.

## Rendering method
`Assets/Resources/Shaders/XrSpatialPanel.shader` gained a `_SBS` keyword (`multi_compile_local`, off by default). With it off the shader is the original variant. With it on, the fragment shader picks the left or right half of the **same source texture** by `unity_StereoEyeIndex` (0 = left eye, 1 = right eye; single-pass instanced/multiview on the headset) and samples the full height. One panel, one mesh, one draw: no second panel and no extra texture. `PanelView.ApplyStereo` sets the keyword and the `_Stereo` vector, only when the panel's settings change.

## Half and Full SBS
Both sample identically (left half to the left eye, right half to the right eye). They differ in the **shape the panel must have**: Half-SBS squeezes each eye to half width, so the eye picture's width/height is the source's width/height (1920x1080 half-SBS = one 16:9 picture per eye); Full-SBS stores each eye at full width, so it is half the source's width/height (3840x1080 = one 16:9 picture per eye). Panel shape is never changed automatically: **Fit picture** and **Lock aspect** use the eye shape in a 3D mode, so you press Fit picture once after choosing the mode.

## Eye swap
`SurfaceDef.swapEyes`: the left eye reads the right half and the other way round. One palette button. Eye order is never guessed.

## 3D depth (convergence)
`SurfaceDef.stereoOffset`, a fraction of one eye picture's width, **limited to +-1 percent** (`StereoMath.MaxOffset`), moved in 0.25 percent steps by the two Depth buttons. Positive brings the picture nearer (left picture moves right, right picture moves left), negative pushes it farther. Default 0 = the source's own stereo. A 1 percent shift on a 2 m panel is 2 cm per eye, 4 cm total: the limit is deliberately small. The sampling window is clamped inside each eye's own half so a shift never pulls in the other eye's picture. No vertical shift, no scaling difference between the eyes.

## Interaction and crop limits
- **Input** is unchanged in kind: pointer position maps onto the logical panel. In a 3D mode it maps onto one eye's picture (the left half, or the right half when swapped), through `PanelView.UvToSource`.
- **Crop is off while 3D is on** (the Crop button and a crop drag refuse with a message). Reason: a crop is a rectangle of the whole source, but in SBS each eye needs the same sub-rectangle of its own half; mapping an arbitrary crop cleanly is ambiguous (what a crop of an SBS frame should mean), so for this version a 3D panel ignores its crop. The saved crop is kept and applies again when you go back to Flat.
- **Depth Lab is bypassed** on a 3D panel (mutually exclusive); going back to Flat lets it act again.
- Saved with the layout (`stereo`, `swapEyes`, `stereoOffset`); layouts saved before this load as Flat.

## UI
Palette section **3D VIEW**: `3D: Flat / SBS Half / SBS Full` (cycles), `Swap eyes`, `Depth -`, `Depth +` (the section header shows the current depth). They act on the selected screen. Labs (when `depthlab.flag` exists) has **Add 3D test** / **Remove 3D test**: a generated Half-SBS 1920x1080 picture (red box in front, gold on the screen plane, green behind, yellow ball nearest, grid and white frame on the plane, a cyan one-eye marker top-left of the left picture and an orange one top-right of the right picture). It starts in **Flat**, so you first see two squeezed pictures.

## Automated tests (all pass)
EditMode `StereoMathTests`: eye windows, swap, offset sign and clamp, step, aspect for Half/Full, input rect, mode cycle, shader params, old layout files load as Flat and settings survive save/load. PlayMode `StereoPanelTests`: the real panel shader rendered from a camera (left eye): Flat shows the whole source, SBS shows the left half across the whole panel, Swap shows the right half, Flat again equals the first result, the largest shift never shows the other eye's colour; the real panel (keyword on/off, depth bypassed, crop and corners unchanged, Flat restores everything), pointer mapping, crop refused in 3D, palette buttons (no selection changes nothing), test picture has the intended per-eye disparity.

**Not testable on the PC: the right eye** (a desktop camera is always eye 0). That path is the same code with `unity_StereoEyeIndex` = 1 and is verified only by the headset checklist.

## Headset checklist
1. Labs, Add 3D test. You should see two narrow side-by-side pictures (Flat).
2. Select the screen (edit mode, point and trigger), press 3D: Flat once (SBS Half), then Fit picture. One picture, with the red box floating in front, the green box behind, the grid on the frame plane, and proportions as a normal 16:9 screen (not tall and narrow).
3. Swap eyes: depth should reverse (red behind, green in front). Swap again to restore.
4. Depth - and Depth +: a slight push farther or nearer, never uncomfortable. Return to 0.
5. 3D: Flat until Flat: the original picture returns, panel unchanged. Crop and Depth Lab work again.
6. Repeat with a real half-SBS and full-SBS video in a player window if available (full-SBS: choose SBS Full).
