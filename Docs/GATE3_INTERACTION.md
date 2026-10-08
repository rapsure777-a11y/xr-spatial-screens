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
