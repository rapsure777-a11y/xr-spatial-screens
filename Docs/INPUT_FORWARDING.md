# Input forwarding

Laser on a panel -> panel UV -> crop-aware source position (0..1, origin top-left) -> captured window's on-screen rectangle -> desktop pixel -> absolute `SendInput`.
Code: `Assets/Scripts/Spatial/InputForwarder.cs` (the only place that touches Win32 input; controller reading stays in `XrPointerSource`/`HandInput`, so another backend can replace it without touching the compositor). Turn it on with the palette's "Interact" toggle (desktop: `I`).

## Behaviour
| Input | VR control | Notes |
|---|---|---|
| Hover / move | laser | about 125 updates a second |
| Left click, double click, click-and-hold, drag | trigger | after a press the pixel stays put until the laser moves 6 px (shaky hands still click cleanly); a second press within 0.5 s and 14 px lands on the same pixel so Windows sees a double click |
| Right click | secondary button | a click (no right-drag yet) |
| Scroll | thumbstick up/down | |
| Keyboard | physical keyboard | goes to the window in front, which is the target after the first press. No VR keyboard yet |

Rules the forwarder enforces:
- **One destination.** The panel under the laser receives input. A press locks the destination to that panel until the button is released. While held, the laser may leave the panel: the pointer clamps to the window's edge (drag-and-drop out of the window edge works, nothing else gets the drag).
- **No wrong-window clicks.** Before a press the target window must be in front (it is activated if not, with the normal foreground rules, waiting up to 0.5 s) and the pixel must belong to that window or a window it owns (menus, tooltips, dialogs). Otherwise the press is dropped and the panel shows a message ("Windows would not bring that window to the front..." / "Another window is covering that spot..."). Nothing is clicked elsewhere.
- **No stuck buttons.** The held button is released when the controller loses tracking, interact mode is switched off, the panel or window goes away, or the app is disabled.
- **Gone windows are refused.** If the window has closed, the press is dropped with a message.
- **Moved and resized windows follow.** The rectangle is read live in the player (`DwmGetWindowAttribute` extended frame bounds), in the same pixel space as the injected input, so it does not depend on the capture helper's DPI awareness or on a stale frame.
- Test-pattern screens never click anything. Monitor sources click the monitor.

Four-corner geometry: the picture is drawn with a projective mapping and the ray is mapped back with the exact inverse, so tilted, trapezoid and edited quads are accurate. Cropped panels map back to the original window position. Several panels may show one window; each maps to its own crop.

## Evidence (all on this PC, real windows, real SendInput)
`Tools\test-input.ps1` starts `Tools\ClickTest` (a window that logs every mouse event it actually receives), runs the player with a scripted laser (`Assets/Scripts/App/SelfTest.cs`) and checks that log. Last full run: **11 of 11 passed**.

| Scenario | Result |
|---|---|
| Click on a full panel | within 1 px |
| Click through a cropped panel | exact |
| Click through a cropped panel tilted 40 degrees | exact in 6 of 7 runs; one run was 49 px off (cause not found, did not recur in later runs) |
| Right click, scroll wheel | received |
| Double click (second press 3 px away, 80 ms later) | Windows reported a double click |
| Drag (press, 96-275 mouse moves while held, release) | press and release on the expected pixels |
| Drag that leaves the panel (laser past the panel edge) | release at the window's edge, drag kept its target |
| Controller tracking lost while the button is held | button released in the target |
| Window moved before the click / resized before the click | click on the right pixel of the new rectangle |
| Window closed before the click | nothing sent, message shown |
| Real game, Slay the Spire (non-admin, windowed Java game), run twice: first with the earlier forwarder, then with this one | clicked "Statistics": the Statistics screen opened; clicked "Back": returned to the menu (screenshots of the game window) |
| Unit tests | EditMode 29/29 (incl. the 1920x1080 minimap example: crop centre -> 1700,850; negative desktop origin; dead zone), PlayMode 12/12 |

## Not tested
- **Display scaling other than 100%** (this PC is at 100%; 125/150/200% could not be set up). The rectangle is read inside the player, which removes the main risk, but it is unproven.
- **A second monitor / negative desktop coordinates in the live path.** The math is unit tested; there is one monitor here.
- **Refusing a press when Windows will not raise the window above a topmost window** (implemented, but the test setup was spoiled by an always-above fullscreen browser window on this PC, so it has no passing test).
- **Foreground-activation failure** in general (same reason).
- **A strategy game with a map and edge scrolling** (only Slay the Spire, a menu/card game, was driven; Civilization, Crusader Kings, Manor Lords etc. are not installed or were not tried).
- The VR controller bindings (trigger, secondary button, stick) and the in-headset feel.
- Physical keyboard into the target after a click (relies on Windows keyboard focus; not exercised).

## Cannot work (inherent)
- **Mouse-look / raw-input games** read relative motion or lock the cursor to the window; a pointing laser cannot steer them.
- **Elevated (administrator) windows**: Windows blocks input from a normal process into an elevated one. Run the app elevated too, or run the target without elevation. The app says so when Windows refuses.
- **Protected (DRM) content** captures black.
- **Anti-cheat or exclusive-fullscreen games** may ignore, reject or flag synthetic input, or fail to capture.
- **One real cursor.** Only one panel can have the mouse at a time, and the PC's visible cursor moves while you work in VR; the physical mouse competes with the laser. Moving the cursor can trigger hover effects, tooltips or edge scrolling in games when the laser merely crosses a panel in interact mode.

## Fixable, not done
- VR keyboard panel, middle/back/forward buttons, right-button drag, touch input.
- Per-panel "do not forward" toggle (the flag is stored, no palette control yet).
- Extra smoothing for tiny targets; configurable dead zone and double-click window in the palette.
- Verify scaling 125-200% and a second monitor, test more games.
