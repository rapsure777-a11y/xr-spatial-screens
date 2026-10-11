# Steam page prep (draft, 2026-10-11)

Working title: **XR Spatial Screens** (Gamebreak Labs, version 0.1.0). Draft copy only; nothing here is published. Claims below are limited to what has been verified in the headset (see SPEC_2026-10-10.md section 3). Items marked TODO are not built or not verified.

## Release blockers (decide before the page goes live)
1. **Pairing / one-click host app.** Today the link is adb over the dongle address and needs Lepton Development on the Frame. A Steam customer cannot be asked to do that. Pairing + LAN transport (design exists only on `claude/hardware-video`) and a tray host app are required. `main` waits for this.
2. **Installer and distribution of the Android app.** How the APK reaches the Frame (Steam depot, sideload helper, or store) is undecided.
3. **Steam Frame availability and Steamworks rules** for a headset-native companion app: confirm with Steamworks docs before committing to the category (Software vs Game) and the VR tags.
4. Unverified features not to advertise yet: Full SBS with a real file, Clear unused on headset, Depth Lab re-check.

## Short description (under 300 characters)
Put your PC's windows in your room. XR Spatial Screens streams any window or monitor to your Steam Frame as a resizable, tiltable screen over passthrough, with controller clicking, typing and PC sound.

## Long description (draft)
**Your desktop, anywhere in the room.**
XR Spatial Screens turns the windows on your PC into floating screens in your real room. Draw four corners with your controller and the screen sits exactly where you want it: over your desk, on the wall, above your bed.

**Features**
- **Any window or monitor.** Pick from a list of your open PC windows, including minimized ones. Run several screens at once.
- **Passthrough.** See your real room behind your screens.
- **Place it your way.** Draw, grab, move, turn, tilt and resize. Lock the aspect ratio, or snap a screen to the sharpest size for its distance.
- **Crop.** Split one window into independent panels.
- **Click, drag, scroll and type** into PC windows with the controller laser, the virtual keyboard, or a Bluetooth keyboard.
- **PC sound in your headset**, with a volume control up to 300%.
- **See your mouse.** An optional PC cursor overlay shows where Windows really has the pointer.
- **Layouts remember each app.** Your screens come back where you left them.
- **3D video.** Side-by-side Half/Full 3D per screen, swap eyes.
- **Real controller models** drawn as a clean outline so you can see where you are pointing.
- **Experimental 2.5D depth** (Labs) for flat pictures.

## Requirements (draft, confirm before publishing)
- Steam Frame headset
- Windows 11 PC (Windows.Graphics.Capture), .NET 8 runtime bundled
- Valve dongle connection (about 1 ms link). Wi-Fi performance not yet measured.
- Measured on the dev PC: about 32 fps for a game window, 35 ms round trip, text readable at 2880 px width.
- Limits to state honestly: windows over 3840x2160 are not supported; absolute pointer only (no raw mouse for FPS-style games); one PC.

## Suggested tags
VR, Utility, Productivity, Software, Passthrough (confirm what Steam offers for Frame).

## Accessibility note (real selling point)
Volume up to 300% with a limiter, large readable text via sharpness snap. Captions/clarity boost for hard-of-hearing users are planned, not built: do not advertise yet.

## Art and media checklist (Steam requirements; verify exact sizes in Steamworks)
| Asset | Size (verify) | Status |
|---|---|---|
| Header capsule | 920x430 | TODO |
| Small capsule | 462x174 | TODO |
| Main capsule | 1232x706 | TODO |
| Vertical capsule | 748x896 | TODO |
| Library hero / logo | 3840x1240 / 1280x720 | TODO |
| Screenshots (min 5, 1920x1080) | | TODO: headset captures needed (existing `Screenshots/` are depth-lab test shots only) |
| Trailer | | TODO: headset capture of placing a screen over a real room |
| Icon, community assets | | TODO |

Suggested screenshots: (1) a screen on a nightstand over passthrough, (2) several screens around a desk, (3) a game window (Slay the Spire) being clicked, (4) cropped panels, (5) 3D side-by-side video, (6) palette and layout. NEVER include personal photos or real private windows/notes in captures; use a dedicated test window.

## Other store-page items
Privacy statement (the app streams PC windows over a local link; nothing leaves the LAN/dongle), support contact, age rating questionnaire, pricing, release date, EULA, third-party licences (Depth Anything models, libjpeg-turbo, Unity): TODO, needs the owner's decisions.
