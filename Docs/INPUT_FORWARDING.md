# Input forwarding

Laser click on a panel -> panel UV -> crop-aware source UV -> captured window's on-screen rectangle -> absolute mouse position -> `SendInput`.
Code: `Assets/Scripts/Spatial/InputForwarder.cs`. Turn it on with the palette's "Interact" toggle (desktop: `I`).

## What it supports
| Input | VR control | Notes |
|---|---|---|
| Mouse move / hover | laser | continuous, about 125 updates per second, so hover effects and drags work |
| Left click, double click, drag | trigger | press and release are separate, so holding and dragging works |
| Right click | secondary button | |
| Scroll wheel | thumbstick up/down | |
| Focus | automatic on press | the captured window is brought to the front first (restores a minimised window) |
| Keyboard | your physical keyboard | goes to whichever window is in front, which is the target after the first click |

Works through full panels, cropped panels (the click lands where that part of the picture came from in the original window), tilted panels and several panels of one window. Pattern (test) screens never click anything. Monitor sources click the monitor.

The window rectangle is read inside the player (`DwmGetWindowAttribute`, extended frame bounds), so it is in the same pixel space as the injected coordinates whatever the helper's DPI awareness, and it follows a window that has been moved.

## What was tested (automatically, on this PC)
`Tools\test-click.ps1` starts `Tools\ClickTest` (a window that logs every click's screen position), runs the player with a scripted laser click and compares the expected pixel with the one the window received.

| Case | Result |
|---|---|
| Full panel, points at 4 places | exact (0 to 1 px) |
| Cropped panel (crop 0.5,0.4,0.5,0.6) | exact |
| Cropped and tilted 40 degrees | exact |
| Right click and wheel delivered | yes |
| Target window covered by the player's own window | focus step brings it to the front, click lands |
| Real game: Slay the Spire (non-admin, windowed, Java) | clicked "Statistics" on its main menu: the Statistics screen opened; clicked "Back": returned to the menu (verified by screenshots of the game window) |
| Unit tests (EditMode 24, PlayMode 12) | pass |

## What was not tested
- Display scaling other than 100% (this PC is at 100%) and mixed-DPI or multi-monitor setups. The code handles them in theory (virtual-desktop flag, in-process rectangle) but no scaled display was available.
- Real VR controller button bindings (needs the headset).
- Fullscreen-exclusive games (capture itself may be black or fail; borderless windowed is fine).

## Cannot work (inherent)
- **Mouse-look and raw-input games** (first-person cameras, many 3D games that hide and lock the cursor): they read relative mouse motion or lock the cursor to the window, so a pointing laser cannot steer them.
- **Windows running as administrator**: Windows blocks input from a normal process into an elevated one (UIPI). Run the app as administrator too, or run the game without elevation. The app shows a message when Windows refuses.
- **Protected content** (DRM video): captured as black.
- **Games using exclusive fullscreen or anti-cheat that rejects injected input**: some online games ignore or flag synthetic input.
- The real mouse cursor moves on the PC screen while you click in VR, and you cannot use the physical mouse for something else at the same time.

## Fixable / not done yet
- Keyboard from VR (a virtual keyboard panel), middle button, back/forward buttons, touch input.
- Right-button drag (right click is a quick press today).
- Per-panel "do not forward" is stored (`interactive`) but has no toggle in the palette yet.
- Higher click precision for tiny targets: the laser is as steady as your hand; a smoothing option could help.
- Verify at 125 to 200% display scaling and on a second monitor.
