# XR Spatial Screens: UI inventory for design review (12 Oct 2026)

Two places have UI. Both are described here exactly as built, so a designer can see what exists before suggesting changes.

1. **The headset palette** (inside VR, a floating panel you point at with a laser and click with the trigger).
2. **The Windows host app** (a window and a tray menu on the PC).

What the product is: a spatial compositor. You pick PC windows, they float as screens in your room (over passthrough), and you click them with the controller. The palette is how you do everything else.

---

## 1. Headset palette

### How it behaves
- A world-space panel, 560 units wide = about 0.31 m, readable at arm's length. Height follows its content.
- Pointing: a controller laser. Click: the trigger. There is no touch screen, no keyboard on the palette itself.
- Buttons are rounded rectangles, 58 units tall (about 3.2 cm), two columns, 5 units between rows, 20 units margin, 20 units between columns. Utility has three columns.
- States a button can show: normal, hover (blue), pressed (short flash), **on** (teal fill plus a cyan bar down the left edge, so "on" does not rely on colour alone), dimmed (disabled, used for windows on another desktop).
- Sliders are full-width buttons with a fill; hold the trigger and drag.
- Text: top line = current mode (24 pt bold, cyan accent). Under it a two-line status message (24 pt). Section headers 22 pt bold dim with a hairline. Button text 26 pt centred. Notes 20 pt.
- Colours: panel #0C0F14 (93% opaque), button #212B38, hover #3375AD, on #145766, on-bar #66E6F2, pressed #17426B, text #EDF5FF, dim text #9EB8CC, accent #73D9F2.
- Some buttons only exist in some situations (marked below).

### Main page (top to bottom)

```
 MODE LINE (cyan, bold)            e.g. "EDIT"
 status message (2 lines)
 ── SOURCE ─────────────────────────
 [Capture window...] [Next source]
 [Test pattern]      [Clear unused]
 [Reconnect screen]                    (only while streaming from the PC)
 ── SCREEN ─────────────────────────
 [New screen]        [Delete screen]
 [Turn picture]      [Fit picture]
 [Lock aspect: off]  [Snap sharpness]
 ── 3D VIEW · side by side ─────────
 [3D: Flat]          [Swap eyes: off]
 [Depth -]           [Depth +]
 ── CROP ───────────────────────────
 [Crop]              [Reset crop]
 ── INPUT ──────────────────────────
 [Interact: off]     [Points: laser]
 [PC cursor: Off]                      (only while streaming from the PC)
 ── STREAM AND SOUND · volume 100% ─  (only while streaming from the PC)
 [PC sound: ON]      [Volume -]
 [Volume +]          [PC: Connected]
 ── UTILITY ────────────────────────
 [Save layout] [Keyboard] [Hide palette]
 ── LABS · experimental ───────────   (only if the lab flag file exists)
 [Depth Lab...]
```

Button labels and their states:

| Section | Button | Label states |
|---|---|---|
| Source | Capture window... | opens the window picker page |
| Source | Next source | cycles which source new screens use |
| Source | Test pattern | adds a test picture |
| Source | Clear unused | removes sources no screen uses |
| Source | Reconnect screen | restarts a frozen or blocky stream |
| Screen | New screen / Placing n/4 | "Placing 1/4" etc. while you place four corners |
| Screen | Delete screen | |
| Screen | Turn picture | rotates the picture inside the screen |
| Screen | Fit picture | fits the picture's aspect ratio |
| Screen | Lock aspect: ON / off | toggle |
| Screen | Snap sharpness | snaps the screen to a sharp size |
| 3D view | 3D: Flat / (side-by-side modes) | cycles the stereo mode; header adds "depth +n" when active |
| 3D view | Swap eyes: ON / off | toggle |
| 3D view | Depth - / Depth + | adjusts depth for side-by-side |
| Crop | Crop / Cropping... | drag a rectangle to make a new panel |
| Crop | Reset crop | |
| Input | Interact: ON / off | when on, clicks go through to the PC window |
| Input | Points: touch / laser | how corner points are placed |
| Input | PC cursor: Off / Normal / High | shows the PC's mouse pointer on the screen |
| Stream and sound | PC sound: ON / off | |
| Stream and sound | Volume - / Volume + | header shows the percentage (0 to 300) |
| Stream and sound | PC: Connected / Reconnecting / Not paired / Pairing / Pair again / Problem / Dev (adb) | opens the PC connection page |
| Utility | Save layout | |
| Utility | Keyboard | opens a virtual keyboard (separate floating panel) |
| Utility | Hide palette | hides the whole palette |
| Labs | Depth Lab: ON / Depth Lab... | opens the Labs page |

### Window picker page (opened by "Capture window...")

```
 Each row, full width:   "<program>: <window title>"        [size tag]
   size tag: "1920x1080", "min 1920x1080" (minimized), or "other desktop"
   "other desktop" rows are dimmed and explain why on tap
 up to 8 rows per page; if none: "No windows found (tap to retry)" or "Looking for windows..."
 bottom row, four buttons, 80 units tall:
 [Back] [Refresh] [Monitor] [Page 1/2]
```

### PC connection page (opened by the "PC: ..." button)
A title, optionally a big six-digit code (112 pt), a body paragraph, then a stack of full-width buttons (70 units tall), and **Back** at the bottom. Which buttons appear depends on the state:

| State (title) | Buttons |
|---|---|
| Pair with your PC | "Pair with <PC name> <address>" (one per PC found), Type address..., Forget this PC, Back to reconnecting, Use Dev connection |
| Compare the codes | Same code, Doesn't match |
| Confirm on your PC | Cancel |
| Pairing | Cancel |
| Paired | Done |
| Connected | Forget this PC, Use Dev connection |
| Reconnecting / Connecting / Can't reach your PC | Retry now, Pair again, Type address..., Forget this PC, Use Dev connection |
| This PC removed this headset | Pair again, Forget this PC and the same set |
| Dev (adb) | Use Paired PC |
| Type the PC's address | on-screen keypad: 1 2 3 / 4 5 6 / 7 8 9 / . 0 : then Delete, Pair here (or Use address), Cancel |

### Labs page (experimental depth and tuning; only visible with the lab flag)
Header "DEPTH LAB · 2.5D panels, off by default". Everything full width, with a dim note under most controls.

| Control | Type | Notes under it |
|---|---|---|
| Depth: ON (tap for flat) / off (flat, normal) | toggle | |
| Strength n% | slider | 0% flat, 15 to 30% subtle, 100% exaggerated |
| Focus n% | slider | which depth stays on the panel |
| Pop n% | slider | pulls near objects away from the background |
| Smoothing n% | slider | less warble, but slower |
| Edge snap n% | slider | keeps depth edges on picture edges |
| Add depth test / Remove test | two buttons | |
| Add 3D test / Remove 3D test | two buttons | |
| Real windows: Off / AI depth (from PC) / Curved / Dome / Tilt | cycle | |
| Depth model: V2 Small / V3 Small / V3 Base | cycle | label says "(turn depth on first)" until the PC reports |
| Depth for: screens you see / all screens | toggle | |
| Depth rate: 2 / 5 / 10 / 15 Hz / Auto | cycle | |
| (readout) | text | "Depth maps: 4.8 per second, 49 ms average, 71 ms slowest" |
| Stream width test: auto / 1920 / 2400 / 2880 / 3440 / 3840 | cycle | |
| Sharper downscale: (off / levels) | cycle | |
| Snap size cap: n deg wide / none | cycle | |
| (readouts) | text | SIZING and PC cursor diagnostics, small 18 pt text |
| Back | button | |

### Other things in the headset world (not on the palette)
- **Screens** are floating quads. When selected they show an outline and corner handles; edit mode shows edge bands you grab to tilt.
- **Controllers** are drawn as the real Steam Frame controllers (outlined) with a laser.
- **Closed window** screens go dark red and show "Window closed, waiting for it to reopen".
- **PC cursor** is drawn flat on the screen.
- **Virtual keyboard** (from the Keyboard button) is a separate floating panel.
- Controller actions (from the earlier design): trigger = click/select; grip = grab, move, resize a screen; palette is toggled from a controller button. Ask for the exact bindings if the designer needs them.

---

## 2. Windows host app

### Main window ("XR Spatial Screens Host", about 940 x 850, dark theme)

```
 XR Spatial Screens Host                 (title)
 Gamebreak Labs
 ┌─────────────────────────────────────────────┐
 │ ● Connected                                  │   big status dot + headline + detail line
 │   Steam Frame via Dongle                     │   (notice line in amber when something needs attention)
 └─────────────────────────────────────────────┘
 ┌─────────────────────────────────────────────┐
 │ Host        Running                          │
 │ Headset     Connected - Steam Frame          │
 │ Connection  Dongle - 10.35.78.22:5610        │
 └─────────────────────────────────────────────┘
 [ Pair Headset ] [ Stop Host ] [ Launch on Headset ] [ Install / Update on Headset ]
 result line (green on success, amber otherwise)
 (pairing card, only while pairing: code, countdown, Same code / Doesn't match / Cancel, then Done or Pair Again)
 Paired devices
 ┌ list: Headset | Paired | Last connected | Status ─────────┐
 │ Steam Frame   Oct 11, 2026   Today 17:25   Connected      │
 └──────────────────────[Remove] [Remove All]───────────────┘
 [x] Start XR Spatial Screens Host with Windows
 Show details            (link; opens a text box with [Open Log Folder] [Copy Diagnostics])
 Version 0.1.0 - Gamebreak Labs
```

Button names: **Pair Headset** (the primary, blue button), **Stop Host** (becomes **Start Host**), **Launch on Headset**, **Install / Update on Headset**, **Same code** (green), **Doesn't match**, **Cancel**, **Done**, **Pair Again**, **Remove**, **Remove All**, **Open Log Folder**, **Copy Diagnostics**. A checkbox: "Start XR Spatial Screens Host with Windows". A link: Show details / Hide details.

Status headlines: Host stopped, Connected, Pairing, Reconnecting, Waiting for headset (each with a one-line instruction below it, for example "Click Start Host to let your headset connect."). The dot colour follows the state (green good, amber warning, red problem, grey idle).

### Tray menu (right-click the tray icon; left-click opens the window)
Open XR Spatial Screens Host (bold), Pair Headset, Launch on Headset, Install / Update on Headset, Paired Devices, —, Stop Host / Start Host, Start with Windows (checkmark), —, Exit.
Balloon notifications: pairing finished, headset removed, launch and install results.

---

## 3. Things worth asking a designer about
1. **The palette is long.** About 28 buttons on the main page in two columns, plus separate pages. Which of these belong on a first-level surface, and which could live behind a second tab or a settings area?
2. **Mode and state at a glance.** The mode line is one small line of text. Would a clearer always-visible state (placing, editing, interacting) help?
3. **Toggle wording.** Many buttons show their current state in the label ("Lock aspect: off"). Is that clear, or should state be shown another way?
4. **Reading distance.** Text is 24 to 26 pt at about 0.31 m wide. Is that comfortable at arm's length and at a distance?
5. **Experimental controls.** The Labs page is a dense list of sliders and cycles for engineers. What should a consumer-facing depth setting look like (for example one Quality preset)?
6. **PC app.** Four buttons in one row, plus a result line. Is the hierarchy right (Pair first, developer buttons de-emphasised)? The launch and install buttons are developer and tester tools; the customer path may be a Steam install.
7. **First run.** Today the palette assumes you already know the flow. What is the right first-time experience (pair, pick a window, place a screen)?
