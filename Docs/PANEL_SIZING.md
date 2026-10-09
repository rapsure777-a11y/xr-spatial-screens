# Panel sizing: Lock Aspect and Snap Sharpness

Branch `claude/panel-sizing` (from `claude/integration-depth`). Headset verdict, 2026-10-09: "a really good feature". Keep both controls.

## Lock Aspect (palette, SCREEN section)
- Off by default. Off = the normal freeform corner drag, unchanged.
- On = a corner drag keeps the aspect of what the panel shows (source aspect x crop aspect). The opposite corner stays fixed; the larger of the horizontal and vertical pulls sets the size. Grip scaling was already proportional.
- Math: `PanelSizing.ResizeCornerLocked`. Not saved between launches.

## Snap Sharpness (palette, SCREEN section)
- Resizes the selected panel about its centre, same plane and orientation. Nothing else changes (no move, depth, streaming, quality or input change).
- Rule: panel width = distance x effective source pixels / (headset pixels per degree x 180/pi), so the source pixels per degree at the middle of the panel equal the headset's display pixels per degree (1:1, no enlarging). Height follows the crop's aspect. Width is limited to 0.10 to 6 m.
- Effective source pixels = crop size x the received picture size (not the desktop window size).
- Headset pixels per degree = projection scale x eye render width / 2, per degree, at the centre of view (`SpatialApp.HeadsetPixelsPerDegree`). Distance = head to panel centre.
- `PanelSizing.SourcePerDisplay` (1.0) is the one knob: raise it above 1 for a smaller, denser panel.

## Readout
Labs page, "SIZING": source and shown pixels, panel size and angles, source px/deg against the headset's, and the snap size.

## Not recorded
Side-by-side numbers for freeform vs locked vs snapped on The Ascent were not written down. Add them here if they matter.

## Findings from headset testing (2026-10-09)
- Sharpness depends on panel size: a 2880 px source is only sharp at about 1:1 (very large, around 130 degrees wide). Shrunk, it goes soft because the Frame shows about 22 px per degree.
- Snap Sharpness at 1:1 was too big to use, so Snap now has a width cap (Labs: none / 60 / 75 / 90 degrees). **Default 60 degrees.**
- Labs "Sharper downscale" (mip bias off / mild -0.5 / strong -1) recovers sharpness on small panels; user: it works. Default is still off (`ScreenSource.MipBias`).
- Labs "Stream width test" pins the stream width (auto / 1920 to 3840) to separate stream width from panel size.
- Crops stay the sharpest way to get a small panel: they cover fewer source pixels.
