# Gate 3: interaction in the native Frame app

Status 2026-10-08: **3a and the first slice of 3b passed on the headset.** User: "Actually works great. I put a screen right on top of my nightstand."

## 3a (Experiments/Gate1Passthrough, test app)
Laser from the right controller onto the streamed panel; trigger = left button (hold to drag), A = right click, sticks = wheel, grip = carry the panel, right stick while carrying = resize. Verified from the PC side with the click-test window: 25 left presses with drags, right clicks, 16 wheel events, 2 double clicks. Panel carrying was reported "super silky smooth".
Bugs found and fixed on the way: the PC refused clicks because z-order lags the focus change (short settle wait added, clearer refusal message); the sender did not notice a vanished client while the window was static, which blocked reconnecting.
Findings: the Frame's runtime presents the Steam controllers as Oculus Touch controllers (`OculusTouchControllerOpenXR`); `XR_VALVE_frame_controller_interaction` is not offered on the headset, so the custom Steam Frame profile is not needed there.

## 3b first slice: the real app on the headset (this branch)
`claude/gate3b-placement`. The same Unity project builds for Android (ARM64, IL2CPP, Vulkan, URP Mobile asset). New code:
- `Capture/RemoteHost.cs` TCP client to the PC host (127.0.0.1:5600 via `adb reverse`), `Capture/NetworkBackend.cs` (an `ICaptureBackend`: JPEG frames decoded per rendered frame, ack back), `ScreenSource` uses it when the host connection is active.
- `Spatial/RemoteInputForwarder.cs`: crop-aware source position + button events sent to the host; the Windows `InputForwarder` stays for the PC build.
- `XR/PassthroughFeature.cs` requests alpha blend (Android only); camera clears to transparent on Android.
- `Editor/HeadsetBuild.cs`: `Setup` then `Build` in batch mode (separate launches). Android tools are expected in `C:\Users\fence\UnityAndroid` (override with `XRSS_ANDROID_TOOLS`).
- Host side unchanged from Gate 2 (`Tools/XrssStream` + `XrssCapture`, started with `Experiments/Gate1Passthrough/gate2.ps1 -Hwnd <hwnd> -Pkg com.gamebreaklabs.xrspatialscreens`).

Headset result: palette, 4-corner placement, and use in the room worked; the user placed a screen on a nightstand over the live room.

## Not done / not verified
- One streamed window only (the palette offers "Streamed PC window"); no window list from the PC, no per-window streams, so per-app layouts key on a placeholder.
- Saved-layout round trip, crop, corner editing and click accuracy after placement were not individually checked by the user in this slice (palette/placement worked).
- Transport is loopback + adb. Needs LAN transport with a pairing code and encryption before real use; JPEG does not scale to several large windows (hardware H.264 planned).
- Mapping/dead-zone/double-click rules live in the PC host (`Tools/XrssStream/InputInjector.cs`), duplicated from `InputForwarder.cs`; the two should be unified later.
- Controller button names on the headset differ from the PC (Touch layout): right click is the secondary (B) button in the real app.

## Real browser and game windows (2026-10-08)
Tested with an Edge window (Wikipedia article) and Slay the Spire (3440x1431 window) streamed to the real app. User: game "extremely playable", no ghosting or tearing, clicks and drags work; text "not blurry" after the fixes; sharpness limited mostly by the headset display.

Bugs and causes found:
- Upside-down image: the capture helper writes rows top-first (what the panel shader expects); a decoded JPEG is bottom-first. Fixed with a GPU flip in `ScreenSource` for `NetworkBackend`.
- Blur: the stock URP Mobile asset renders at 0.8 scale; the headset build now sets 1.0 and the eye texture scale to 1.25. JPEG quality 90, 4:4:4.
- Click not working: the palette's Interact toggle must be ON (trigger edits screens otherwise); and the capture helper had died because the game first shows a start-up window whose handle is replaced a few seconds later. Wait for the game window handle to stay stable before capturing.
- Lag: three causes. (1) JPEG encode ~40 ms on one thread capped the stream near 21 fps: now libjpeg-turbo (SkiaSharp) on 3 parallel workers, ~30 fps. (2) Router Wi-Fi to the Frame had 24 ms average ping and spikes to 149 ms. (3) The Frame is also reachable through its Valve Wi-Fi dongle on the PC (`Realtek 8832CU ... For Valve`, 2.4 Gbps link, Frame address 10.35.78.1): 1 ms average, 6 ms worst. Use `adb connect 10.35.78.1:5555` (not the router address) and the stream runs at ~32 fps with a 35 ms average and 62 ms worst round trip (2400x1016, ~80 Mbit/s).

Not measured: decode time on the headset main thread; full-width (3440) streaming; several windows at once; fast action games; fullscreen exclusive games; the Frame behaviour on battery over a long session.

## Several windows at once (3c first slice, 2026-10-08)
Host protocol v2 (`Tools/XrssStream/Program.cs`): one TCP connection, many streams: window list ('W'/'XRSL'), open/close a stream per window handle ('O'/'X'), per-stream acks and pointer events, stream status messages. One `XrssCapture` helper per open window, shared 4-way JPEG encode pool; idle windows cost almost nothing (a static page streams 1 to 5 fps). Headset: `RemoteHost` (streams, window list, reconnect re-opens wanted streams), `NetworkBackend(hwnd)` per source, `ScreenSource` finds a saved source's window by process and title among the PC's windows and retries every 2 s while it is not open, the palette window picker lists the PC's windows, `RemoteInputForwarder` sends each press to its panel's stream.

User verification on the headset: window list works, "next source" (picking another window for another screen) works, clicks go only to the pointed-at window, a closed PC window leaves its screen showing the last frame and not interactive (expected; planned: dim it and label it, reconnect when reopened).
Measured with two streams: terminal window 1033x951 at 20 to 34 ms round trip, browser 1586x993 at 40 to 48 ms.

Next: dimmed "window closed" state; a typing path (headset keyboard to the PC window); pairing code and one-click host app; auto stream width; hardware video if several large windows are active at once.

## Typing (2026-10-08)
User verified on the headset: "Everything works".
- Host: protocol message 'K' (stream, kind, code): kind 0 types one Unicode character (SendInput KEYEVENTF_UNICODE, so layout and shift state do not matter), kind 1/2 press and release a Windows virtual key (extended-key flag set for arrows, Home/End, Insert/Delete, right Ctrl/Alt, Win). The target window is brought to the front first; a held key is released when the stream closes or the headset disconnects. Verified with the ClickTest fixture: characters incl. a non-ASCII one, Ctrl+C as virtual keys, Enter, held Shift released on disconnect.
- Headset: `KeyboardPanel` (US layout, laser-poked, latching Shift/Ctrl/Alt, Ctrl/Alt + letter/digit sent as real combinations, Move and Close keys, header shows the target window), `UiLayers` (keyboard in front of the palette), `HeadsetKeyboardForwarder` (a Bluetooth keyboard paired to the Frame: text as characters, specials/modifiers/function keys as keys with auto-repeat, letters and digits as keys while Ctrl/Alt held). Keystrokes go to the window last clicked (`RemoteHost.FocusStream`). The PC's own keyboard already types into that window because the host brings it to the front on click.
- Fixed on the way: the palette's last row was below its panel (unreachable "Hide palette"); `SpatialApp.Keyboard` property name collided with Unity's `Keyboard` class (now `VirtualKeyboard`).
- Not verified: the Bluetooth keyboard path (needs a keyboard paired to the Frame; unknown whether the Frame passes its keys to the app); non-US layouts on the virtual keyboard; IME/dead keys; keys on exclusive-fullscreen games.

## Window list states and PC sound (2026-10-08)
- Window list: the capture helper now lists minimized windows (state "minimized", size = restored size) and windows on other virtual desktops (state "otherDesktop", shown but not selectable). When a minimized window is opened the host restores it without taking focus (SW_SHOWNOACTIVATE) and minimizes it again when the stream closes. Verified list contents from the PC: Media Player, ChatGPT, Comet, Notepad, File Explorer, uTorrent, Steam streaming, VR Vault appear as minimized.
- PC sound: host `AudioSender` (NAudio WASAPI loopback of the default output device, 16-bit stereo, ~60-90 ms chunks) sends 'XRSA' packets after the headset sends 'S 1'; headset `RemoteAudio` jitter buffer (prime 70 ms, target 90 ms, max 220 ms, linear resampling) feeds `PcAudioPlayer` (AudioSource + OnAudioFilterRead). Palette button "PC sound: ON/off". Measured on the headset: ~16 packets/s, 75 to 133 ms buffered, about one underrun per 30 s. User: video lags slightly behind, so sound and picture nearly match.
- Not done: per-window sound (Windows process loopback), muting the PC's own speakers, headset microphone to the PC, A/V sync control.
- Finding: with three large windows the headset shows only 11 to 13 fps per stream while the PC sends 24: every JPEG is decoded on the main thread (`Texture2D.LoadImage`). Planned fixes: request a stream width from the headset based on how large the screen appears (angular size), pause streams that are out of view, then hardware video decoding.

## Media Player check (2026-10-08)
Reproduced on the PC with a plain H.264 test video in the Store version of Media Player: the capture works in the normal window state, and after minimize and the host's restore (SW_SHOWNOACTIVATE) the video was still playing (clock 23 s to 48.9 s while minimized) and captured correctly (second frame 2.7 s later: 51.6 s). So the minimize/restore path is not the cause of the user's report that their movie only shows when the player is in its full-screen mode on the monitor. Open question: whether their movie is protected or hardware-overlay video (captures black outside its full-screen mode) or a different app/state.

## Smart quality and off-main-thread decoding (2026-10-08)
- Measured before: `Texture2D.LoadImage` on the main thread took 17 to 29 ms per frame, 30 to 40 frames/s across three streams = 740 to 830 ms of every second (about 80% of the thread). Shown rates were 11 to 13 fps per stream while the PC sent 24.
- Smart quality (`StreamQuality`, protocol 'R' stream, maxWidth, fps): every 0.5 s each stream is asked for the width its screens need in the eye image (corrected for crop, 15% headroom, rounded up to 160 px, 640 to 3840), a slow 2 fps when none of its screens is in view, with a 4 s hold before lowering (the first version flapped 960/1440/3360/3840 as the head moved). Measured with it alone: stream 4 12.3 -> 17.6 fps, stream 5 13.6 -> 18.2 fps shown.
- Off-main-thread decode (`AndroidJpegDecoder`, `NetworkBackend`): the JPEG is decoded by android.graphics.BitmapFactory on a worker thread into a direct byte buffer; the main thread copies the pixels into the texture (`LoadRawTextureData` + `Apply`). Pixels are top-row-first, so no flip is needed on this path (`NeedsFlip` false); if the Android decoder throws, the backend falls back to `LoadImage` (flip on). Measured on the headset with one 1033x951 stream: main thread 0.6 ms per frame, 9 ms per second.
- Pitfall found: passing `(sbyte[])(Array)bytes` still hands Unity a `byte[]` and logs a JNI warning with a stack trace on every call; an actual `sbyte[]` copy is needed.
- Not yet measured: three large windows at once with both changes; picture orientation on the new path was only checked via logs (user to confirm); CPU load on the worker threads.

## Clicks that did not register, Comet (2026-10-08)
Cause found in the host log: mouse-up landed 20 to 40 px from mouse-down (laser wobble) because the dead-zone rule was applied to moves but not to the release, so web pages saw drags, not clicks. Fixed in Tools/XrssStream/InputInjector.cs: a release inside the dead zone is a click at the press point; dead zone 20 px, double-click snap 24 px; input log lines carry the window title. Separately the Comet browser (Suno) was no longer running on the PC when the user reported a frozen window (no crash record in the Windows log); after reopening it, the helper captured Comet normally (restored from minimized: its window comes back maximized, 3440x1440, although the list reports the unmaximized size). A frozen screen is the last frame of an ended stream: a visible 'window closed' state is still to do.

## Confirmed working on the headset (2026-10-08, late)
User: Suno in the Comet browser works (clicks, library, desktop layout, sound) after: the release-inside-dead-zone fix (clicks), the real restored size for minimized windows (a maximized window restores maximized, not at its small normal size, which had given Suno's mobile layout), sizes shown in the window picker, and 8 windows per page sorted normal / minimized / other desktop. Also in this stretch: smart per-stream quality, off-main-thread JPEG decoding (main thread 9 ms/s), steady 144 fps app frame rate, PC sound, minimized-window restore, typing.
Known gaps: no "window closed" look (a dead stream shows its last frame), the layout lives only on the headset and is lost when Lepton is reset (reinstall needed too), pairing/security, one-click host app, per-window sound, hardware video, palette follows the wrist with a slight flutter when moved fast, head-turn blockiness (mitigated by more headroom and a wider in-view margin; user to re-check).

## Host crash fix, volume control (2026-10-08, night)
- The host (`XrssStream`) crashed twice (22:52, 23:54) with an AccessViolation inside `SKPixmap.Encode(SKJpegEncoderOptions)` on a pool thread (YouTube/fullscreen resizing publishes odd-shaped frames; an unvalidated frame reached the encoder). Fixes: frames are only dispatched with 16 <= w,h <= 8192, stride >= w*4, stride*h <= slot capacity; output width/height >= 16; the scale result and the allocated bitmap are checked; a crashed capture helper is restarted while its window exists (max 4 per minute); `host-supervisor.ps1` restarts the host after any exit (tested: killed, new process within 8 s, headset reconnected and reopened its windows). Restart record: `Logs/host.out.restarts.log`.
- Volume (user is hard of hearing, see memory): palette Volume -/+ in steps 0, 25, 50, 75, 100, 125, 150, 200, 250, 300 percent, soft knee limiter above 0.8 (tanh), remembered in PlayerPrefs `xrss.volume`. User: "Sound seems fine".
- Ideas for the sound: clarity (treble) boost, Windows Live Captions streamed as its own screen, per-window sound.

## Autonomous work while the user rested (2026-10-08, after midnight)
User reports: still some clicking issues; on Comet "everything turns super blocky" and a refresh is needed after a while. Findings and changes (not yet verified on the headset; the headset was asleep so the new build is built but not installed):
- Blockiness, cause 1 (found in the logs): `StreamQuality` swung a screen's requested width between 640 and 3840 px while "in view". It measured the screen with `Camera.WorldToViewportPoint`, which is unreliable on an XR camera. Replaced by `Core/StreamMath.RequiredSourceWidth` (pure geometry from head position and corners; angle measured from the screen's own centre direction so a screen behind the head cannot wrap and read as huge), minimum width 1280, a clean hold (higher need at once, lower after 4 s, out of view keeps the width). 7 tests in `StreamMathTests` (found and fixed a wrap-around bug on the way).
- Blockiness, cause 2 (likely, not reproduced): Chromium browsers throttle and discard detail in a window Windows considers fully covered ("native window occlusion"), which is common for a streamed window; it persists until a refresh. `Experiments/Gate1Passthrough/start-comet-streamable.ps1` starts Comet with `--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling` (only effective when Comet is started with them, after closing all its windows).
- New palette button "Reconnect screen": closes the selected screen's stream and opens a fresh one (a stuck or blocky screen without refreshing the browser).
- Clicks: of 105 logged clicks, 81 released more than 20 px and 34 more than 60 px from where they pressed (the trigger pull moves the laser). Added `Core/PressAim` (press at where the laser rested 70 ms earlier if that is within 2% of the source size of where it is now; a larger difference counts as a deliberate move). 4 tests in `PressAimTests`. The earlier release-in-dead-zone fix (20 px) is on the host.
- Layout backup on the PC (Lepton resets wipe the app and its layouts): `Tools/XrssStream/LayoutStore.cs` keeps `%LOCALAPPDATA%\XrSpatialScreens\layout-backup`; protocol 'Y' (upload name+data) and 'Z' (send me everything) / 'XRSY'; names restricted to letters, digits . _ - (tested: a path-escape name was refused, nothing written outside the folder). Headset `LayoutBackup` uploads every saved layout plus `_lastkey.txt`, re-uploads on each reconnect, and restores missing layouts (the headset's own copy wins), reloading the last layout if the app came up empty. Only the PC side is tested against a scripted client; the headset side is compiled and unit-test-clean only.
- Tests: 40 edit-mode pass; play-mode 12 pass, 2 skipped only because the capture helper is not built inside the second workspace.
