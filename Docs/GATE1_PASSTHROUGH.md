# Gate 1: native Steam Frame passthrough test

Question: **do you see your real room in full colour through the Arcturus camera behind the panels?**
Build: `Experiments/Gate1Passthrough` (separate Unity project, Unity 6000.3.9f1, Android ARM64 IL2CPP, Vulkan, API 29-30, OpenXR 1.18.0). APK: `Experiments/Gate1Passthrough/Builds/Gate1Passthrough.apk` (22.7 MB, package `com.gamebreaklabs.gate1passthrough`; builds are not committed).

## What the app does (and only this)
- Requests `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND` through Unity's public `OpenXRFeature.SetEnvironmentBlendMode` (`PassthroughFeature`).
- Camera clears to transparent black (alpha 0), no skybox, no HDR/MSAA; six opaque unlit panels in a half circle 2 m away (red, orange, yellow, green, cyan, magenta).
- Logs (`[Gate1]` tag, also on a text board in front of you): runtime name/version, enabled and available OpenXR extensions, the blend modes the runtime offers, blend mode before/after the request, and every 3 s `displayOpaque`.
- No streaming, cropping, input forwarding or layouts.

## Steam Frame steps (from Valve's Steamworks pages: setup, adb_lepton, loadgames, debugging)
Developer mode (once):
1. On the Frame: **Steam Settings > System > Enable Developer Mode**.
2. Scroll to **Developer** and choose **Set User Password**.

Route A, adb (fastest, uses the helper):
1. Start **Lepton Development** from the Steam Library on the Frame (an Android home screen appears and stays running).
2. On the PC: `powershell -File Experiments\Gate1Passthrough\gate1.ps1` (connects with `adb connect frame`, installs the APK, launches it, saves `Logs\gate1-device.log`). Use `-Hostname <ip>` if `frame` does not resolve. Over USB instead: `adb forward tcp:5555 tcp:5555` then `adb connect localhost:5555`.
3. Put the headset on, find the app **Gate1 Passthrough** in Lepton's launcher if it did not start by itself.

Route B, SteamOS Devkit Client (Valve's documented route, use if A does not give an immersive app):
1. **Settings > Developer > Pair new host** on the Frame; open SteamOS Devkit Client on the PC, tab Devkits, **Register**, confirm on the headset.
2. **Title Upload**: Name `Gate1`, Local Folder `Experiments\Gate1Passthrough\Builds`, Start Command `Gate1Passthrough.apk`, Runtime **Android (APK)**. Upload, then Start. It appears under Library > Non-Steam > Devkit Game.

## What to report
- Do you see the room behind the panels? In colour? (yes / no / black / grey / monochrome)
- Does the app run as an immersive app at all (head-tracked panels), or as a flat window?
- Then run `gate1.ps1 -Action log` (or `adb logcat -d`) and I read `Logs\gate1-device.log`.

## How the result is classified
| Observation | Conclusion |
|---|---|
| Room visible in colour | Gate 1 passed |
| Panels visible, black background, log blend modes list contains 3 (alpha_blend) and request accepted | app bug (alpha lost in the swapchain/camera path) |
| Blend modes list has only 1 (opaque) or request rejected | OpenXR/runtime limitation on this Frame software |
| Blend mode accepted, room appears grey/monochrome or absent only with Arcturus unplugged/plugged | Arcturus/Frame limitation (camera feed), compare with and without the accessory |
| App never starts immersive | launch route problem (try Route B); not a passthrough result |

## Known risks in this build
- The manifest has no Frame-specific VR category or Valve OpenXR package yet (Steamworks recommends Valve's OpenXR utilities package for Unity; its page could not be read from here). If the app shows as a flat window, that is the first thing to add.
- Controllers are not used.

## Run 1 (2026-10-07): GATE 1 PASSED
Route that worked: Lepton Development started on the Frame, then adb over **IPv4** (`adb connect 10.0.0.171:5555`; the `frame` hostname resolved to an IPv6 link-local address and was refused). `adb install -r` succeeded; `monkey -c LAUNCHER` did not start it, `adb shell am start -n com.gamebreaklabs.gate1passthrough/com.unity3d.player.UnityPlayerGameActivity` did.

Evidence from `adb logcat` (full log saved locally under `Logs/`, not committed):
- Runtime: `SteamVR/OpenXR 2.17.10` on the headset (the Frame's own OpenXR runtime, not the PC one).
- **Runtime blend modes on the headset: 1, 3 (opaque and alpha_blend).** The PC runtime offered opaque only.
- Request accepted: `blend mode changed: AlphaBlend`; Unity reports `displayOpaque=False`.
- Camera clear SolidColor, alpha 0; head pose tracked live.
- Extensions enabled by Unity: XR_EXT_local_floor, XR_EXT_user_presence, XR_KHR_android_create_instance, XR_KHR_composition_layer_depth, XR_KHR_visibility_mask, XR_KHR_vulkan_enable2, XR_META_performance_metrics, XR_FB_foveation (+configuration, swapchain_update_state).
- Early `xrCreateInstance failed` lines appeared twice before the session came up; it then initialised normally.

**User report (headset, Arcturus Vision Camera attached):** the six coloured panels are visible, the real room is visible behind them, in full colour. The text board showed the full extension list, so it overflowed (cosmetic; trim it before reuse).

**Conclusion: Gate 1 passed.** A native Android/OpenXR Unity app on the Steam Frame gets colour passthrough from the system compositor just by requesting `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND` (Unity public API, no camera code, no Valve package, no extra manifest entries) with a transparent-black clear and opaque panels. The Arcturus feed is handled by the Frame, not by the app. Not yet known: behaviour with the camera unplugged (the Frame's own cameras may give a different picture), frame rate/latency under load, and whether a streamed window texture keeps alpha correctly.

Classification: not an app bug, not an OpenXR/runtime limitation (the Frame runtime offers blend mode 3), not an Arcturus limitation.
