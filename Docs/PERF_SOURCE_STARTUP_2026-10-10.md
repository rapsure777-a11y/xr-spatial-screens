# Frame rate: only start sources a screen uses (2026-10-10)

Symptom: video looked jerky. Headset log (`[XrSpatial] frame rate`, every 5 s) showed 25 to 55 fps with slow frames of 42 to 72 ms, main thread at 84 percent CPU.

Finding (A/B over adb with the developer switches in `perf.txt`): controller outline and palette made no difference; the same app with no saved layout ran at 144 fps. The saved layout remembered **23 sources for 2 screens** (about a dozen generated test patterns from the Test pattern button, plus remembered windows: VLC, Explorer, a game, a torrent client, a monitor). Loading a layout started a capture and decode for every one of them, on the PC and on the headset.

Fix: `SpatialWorkspace.LoadLayout` starts only sources a screen uses, plus the active one; `GetSource` starts any other remembered source on demand; generated sources (pattern, depth test, 3D test) that no screen shows are dropped on load. Remembered window sources stay in the layout. Test: `SourceStartupTests`.

Result on the headset, same layout, same build otherwise: about 26 fps before, 137 to 144 fps after. Not yet judged by eye by the user at the time of writing.

Developer switches: words in `perf.txt` in the app data folder (`/sdcard/Android/data/com.gamebreaklabs.xrspatialscreens/files/`): `nooutline`, `nopalette`, `nolayout`; the app logs `perf flags:` when the file exists. Delete the file for normal behaviour.
