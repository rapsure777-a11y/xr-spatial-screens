# PC cursor overlay (branch `claude/pc-cursor`)

Built from `claude/integration-depth` (head ea26393 at the time). Status 2026-10-10: **PC side and automated tests done and verified; headset testing (Wild West Pioneers, Flat and Depth) NOT done yet** because the Frame was not reachable over adb when this was built. The APK is built (`Builds/Headset/XrSpatialScreens.apk`, 14:19) and waiting to be installed.

## What it is
The real Windows mouse cursor, shown inside the spatial screen. The PC host reads where Windows has the cursor and tells the headset; the headset draws a small sprite on the screen's plane. The controller is not involved at all: the sprite shows where Windows really put the mouse, whether a physical mouse or the controller laser (through the existing input forwarding) moved it. A physical mouse needs nothing from this app and nothing is injected for it.

Setting: **PC cursor: Off / Normal / High** (palette, Input section, headset build only). Default Off, saved. With Off there is no cursor traffic, no extra objects and no per-frame work beyond one flag test; the app behaves like the current build.

## Where the position comes from
`GetCursorInfo` in `Tools/XrssStream/CursorReporter.cs` (the real system cursor: position, "is a cursor showing", and which cursor). Read-only: it never moves the cursor and never sends input.

## How it is sent
One new small message on the existing connection, no second network system:
- headset to PC: `'C' on u8` (1 = report the cursor, 0 = stop). Sent when the setting changes and again after every reconnect.
- PC to headset: `'XRSC'` (24 bytes): stream u16, flags u8 (1 = the cursor is over this stream's window with nothing covering the spot, 2 = Windows is showing a cursor), shape u8, u f32, v f32, screen X i32, screen Y i32.

It is not tied to the JPEG frame rate. Three host threads: a poll (about every 4 ms, high-resolution timer, power throttling off for these threads because Windows 11 stretches short sleeps of background programs), a sender that always writes the NEWEST position per stream (an older queued one is replaced, so a long video frame in front of it can never make the cursor lag more than that one write), and a cover check (WindowFromPoint can wait on a busy window, so it runs on its own thread every 25 ms).

## How it maps
- **PC side** (`CursorMath.PixelToWindowUv`, mirrored in the host): the cursor pixel inside the window's extended frame bounds (DWMWA_EXTENDED_FRAME_BOUNDS, the same rectangle the input injector uses) gives u,v in 0..1, top-left origin. This is the exact inverse of the injector's `left + round(u*w)`, so the drawn cursor is where a click from the headset would land. Unit-tested against the injector's formula.
- **Headset** (`CursorMath.SourceToPanel`): the source position is turned into a panel UV with the same rectangle the controller input uses (`StereoMath.InputRect`: the crop, or one eye's picture in a 3D mode). Outside that rectangle the panel shows no cursor, so a cropped panel only shows the cursor when it is inside the crop, at the crop-relative position. Then `QuadMath.UvToWorld` puts it on the panel's four corners, so resizing, Lock Aspect, Snap Sharpness, any freeform shape and tilt all follow because the position is computed from the live corners every frame.
- **Monitor capture:** the headset streams windows only (monitor capture exists only in the editor/sidecar path), so there is no monitor mapping yet.

## Rendering
A separate flat quad (shader `XrSpatial/Cursor`, atlas drawn by code in `Core/CursorArt.cs`), child of the panel, 2 mm in front of the panel plane, alpha blended. It is not baked into the video. The size is a fixed visual angle (Normal 1.7 degrees, about 3 cm at 1 m; High 2.5 degrees) so it reads the same on a small near screen and a big far one. Created only while the setting is on.

- **Depth mode:** the cursor stays on the **flat logical plane** and is never displaced by the depth map. While a panel is in 2.5D its depth test is set to Always, so displaced picture geometry that comes out in front of the plane cannot hide it. That is deliberate: it is the reference point for the controller mismatch.
- **SBS/3D panels:** one flat sprite at the panel plane, the same for both eyes (zero disparity), so no conflicting left/right cursors. It is only shown for the part of the source the controller maps through (the left-eye half, or the right when eyes are swapped); the other half does not get a second cursor.

## Modes
- **Off:** nothing.
- **Normal:** white arrow with a dark outline.
- **High:** 1.5 times larger plus a soft warm halo outside the outline.

## Shapes
The host recognises Windows' standard cursors by handle: arrow, text (I-beam), hand, busy/working, cross, move, the four resize arrows, not-allowed. The headset draws each as a small procedural glyph (the hand is a simplified shape). **Any other cursor, such as a game's own custom cursor, is drawn as the arrow.** If an application hides the system cursor (many games that draw their own), Windows reports "not showing" and the overlay hides too. If a game draws its own software cursor into the picture and also leaves the system cursor visible, both would show; switch the setting to Off for that game.

## Visibility rules
Hidden when: the setting is Off; the cursor is not over this window or another window covers that spot; the application hid the cursor; the source is closed or the stream ended; the panel is hidden; the PC connection dropped (stored cursor states are cleared on disconnect); the position is outside this panel's crop.

## Labs readout
In Labs (needs `depthlab.flag`), below the sizing readout: the selected screen's Windows pixel, shape, source and panel position, the last position the controller aimed at on the same window, and the difference in pixels and percent, plus the current Depth state. Use it to read the controller-versus-Windows error.

## Verified so far
- EditMode 72/72, PlayMode 35 passed, 0 failed, 1 skipped (pre-existing). New: `CursorMathTests` (mapping, crop, SBS rectangle, round trip with the controller mapping, exact inverse of the injector, size, sprite atlas), `PcCursorTests` (off creates nothing, position on the logical plane, follows every report, hidden cases, crop, SBS, resize/move, tilt, High larger, Off removes the object, readout text) and the cursor shader rendered from a camera (white body, dark outline, background shows through, High adds the halo).
- Host, real windows (`Tools` scratch client against the live host with the Windows Terminal window as the stream): centre, corner pixels, quarter point, just outside, far away all matched the expected u,v exactly and the screen pixel; resize cursors reported at the window border; turning reporting off stops the messages. A 100-move sweep delivered 100 reports. The poll costs 0.02 ms per pass (worst 1.4 ms). First report after a move: about 2.5 to 7.5 ms.

## NOT verified (needs the headset)
Wild West Pioneers in Flat mode, physical mouse move/click/drag/scroll feel, no-duplicate-input check, every regression item in the list (they are unchanged by design: no existing file's behaviour was altered with the setting Off; the code paths added are behind one flag test), Depth mode, the controller-versus-cursor mismatch measurement, headset frame rate with the cursor on. The right-eye SBS path is the same flat sprite and cannot be told apart from the left on a PC.

## Headset test plan
1. Install the APK, start the host, open Wild West Pioneers through the app. Palette, Input: PC cursor Normal.
2. Flat: move the physical mouse; the arrow should track. Click a few UI targets, drag a selection, scroll. Confirm one click is one click.
3. Controller: Interact on, aim and click; the cursor should jump to where the click landed.
4. Turn Depth on (Labs). At Strength 0%, 15%, 30%, 60%, 100%: aim the laser at the same button, read the Labs readout (difference px) and note how far the apparent button is from the cursor.
5. Prediction to check (not a measurement): the apparent shift of a displaced feature against the flat plane is roughly displacement x (lateral offset / distance). With a 2 m wide screen at 2 m, relief is 12 cm at 20% (a feature at most 6 cm out of plane, about 3 cm off at the screen edge), 30 cm at 50% (about 8 cm at the edge), and the maximum 35 cm per side at 100% (about 18 cm at the edge). Near the centre line it should be near zero. If the readings follow this, the mismatch grows linearly with Strength and with distance from the centre.

## Files
New: `Assets/Scripts/Core/CursorMath.cs`, `Core/CursorArt.cs`, `Assets/Scripts/Spatial/PanelCursor.cs` (also `CursorReadout`), `Assets/Resources/Shaders/XrSpatialCursor.shader`, `Tools/XrssStream/CursorReporter.cs`, tests. Changed (small, additive): `Capture/RemoteHost.cs` (XRSC parse, setting, last controller aim), `Spatial/PanelView.cs` (owns the cursor, `CursorStream`), `App/PalettePanel.cs` (button, Labs readout, panel height), `Tools/XrssStream/Program.cs` ('C' message, start/stop). Untouched: capture, JPEG transport, controller ray input, crop maths, depth estimation, SBS, placement, audio, keyboard, source selection, sharpness snap, edge tilt, controller models.
