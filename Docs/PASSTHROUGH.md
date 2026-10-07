# Passthrough on Steam Frame (including the Arcturus Vision Camera)

Question: can Spatial Crop's floating screens sit in the user's real room, in colour, instead of a black void? Status 2026-10-06.

## Answer in one paragraph
Not today for the app as built (a Windows OpenXR app streamed through SteamVR). On this PC's runtime the OpenXR blend mode list is `OPAQUE` only and no passthrough extension is offered (checked in the player log). Valve's documented route, `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND`, is described as working for apps running **on the headset**, not for PC apps streamed over Steam Link. The Arcturus camera is reported to feed the headset's own compositor automatically, so it would come for free **if** the app ran on the headset. Whether it can be reached from the PC side (`IVRTrackedCamera`) is unresolved: one public report says it fails on Frame; this PC has not been able to test it yet because the headset was off. A probe is ready.

## What was verified on this PC
| Check | Result |
|---|---|
| OpenXR runtime blend modes (player log, SteamVR/OpenXR over Steam Link) | `XR_ENVIRONMENT_BLEND_MODE_OPAQUE` only |
| Runtime extensions | no passthrough or alpha-blend related extension (has `XR_VALVE_frame_controller_interaction`, `XR_EXT_user_presence`, hand tracking, etc.) |
| `camera.enableCamera` in `steamvr.vrsettings` | not set (SteamVR default: off) |
| `IVRTrackedCamera` from a PC program (`Tools/CameraProbe`), headset powered off | `HasCamera = 0`, `NotSupportedForThisDevice`. **Inconclusive**: no live headset |
| Unity here | 6000.3.9f1 and 6000.6.4f1 with the Windows player only (no Android or Linux build module) |

## What the sources say (not verified by me; read as claims)
- Arcturus Vision Camera: stereo colour camera for the Steam Frame front expansion port, 5K at up to 72 fps, depth-corrected passthrough, HDR, "certified Steam Frame Compatible", $149. The site gives no SDK or API details. ([arcturus.vision](https://arcturus.vision/))
- Arcturus developer page: apps get passthrough through `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND` (Unity: OpenXR plug-in plus an environment-blend feature; the app must use an alpha-capable swapchain and keep alpha). It states: "At this time, passthrough blending works with apps running on Steam Frame, not PC apps using Streamed VR." No raw camera API exists for developers. ([arcturus.vision/developers](https://arcturus.vision/developers))
- Press coverage says the camera works in the system compositor, "automatically across all apps that use passthrough", and that Valve's own developer pages do not mention passthrough. ([vr.org](https://vr.org/articles/steam-frame-color-passthrough-arcturus-vision-149-mixed-reality-developers-2026))
- OpenVR issue 1926 (open, no replies): `IVRTrackedCamera` reports a camera on Steam Frame but frame access fails even with the camera enabled; overlays streamed over Steam Link have no way to ask for passthrough. ([ValveSoftware/openvr #1926](https://github.com/ValveSoftware/openvr/issues/1926))
- SteamVR's OpenXR runtime has never supported OpenXR passthrough; the community layer `openxr-steamvr-passthrough` fills the gap by reading camera frames through OpenVR, so it depends on the same camera access that issue 1926 reports as failing on Frame. ([Rectus/openxr-steamvr-passthrough](https://github.com/Rectus/openxr-steamvr-passthrough))
- Valve's Steam Frame page lists execution models: streamed PC titles, native ARM64 and Android APKs. ([Steamworks: Steam Frame](https://partner.steamgames.com/doc/steamframe))

## Options
| Path | Official? | Works for the current streamed app? | Notes |
|---|---|---|---|
| A. Request `ALPHA_BLEND` from the SteamVR/OpenXR runtime | yes | **No**: runtime offers opaque only (verified) | The app already clears to a transparent colour and keeps panels opaque, so it would need only a blend-mode request if a runtime ever offered it |
| B. Read the camera from the PC with `IVRTrackedCamera`, draw it behind the screens (or use the Rectus layer) | no (private OpenVR interface) | **Unknown**: reportedly fails on Frame; not tested here | Cheap to test: run `Tools\CameraProbe\bin\Release\net8.0\CameraProbe.exe` with the headset on and connected. Even if it works it would be monochrome/raw or the Arcturus feed at whatever SteamVR exposes, with extra latency and a custom reprojection problem |
| C. Run Spatial Crop **on the headset** (Android APK or ARM64 build) with `ALPHA_BLEND`, and stream the captured windows to it from the PC | yes (documented route) | Yes, by changing the architecture | Colour Arcturus passthrough should then come from the compositor with no camera code of ours. Needs: an Android (or Linux ARM64) Unity build module, an on-headset OpenXR build with the Frame controller profile, an alpha-blend feature, H.264/H.265 video capture and encode on the PC, network transport, hardware decode on the headset, and input events sent back to the PC helper for `SendInput`. Significant work; untested on Frame; the Android module is not installed here |
| D. SteamVR "Room View" system passthrough | yes (SteamVR feature, not app controlled) | Not a background for the app | Shows the room instead of the app; no way to composite screens over it |

## Recommendation
1. When the headset is charged and connected, run the probe (read-only, changes no settings). If frames arrive, path B becomes worth prototyping; if they fail the same way as the public report, path B is closed without buying or changing anything.
2. If B is closed, path C is the only route to floating screens in the real room in colour. It is a re-architecture (a standalone on-headset client plus a PC streaming server), so it needs an explicit go-ahead. The existing placement, crop, layout and input-mapping code (`Core`, `Spatial`) is reusable on the headset; the capture transport and the input transport are the new parts.
3. Nothing in the current app needs to change for passthrough readiness. A virtual background (grid or void) stays the fallback everywhere passthrough is unavailable.

## Open questions only the headset can answer
- Does `Tools\CameraProbe` get `HasCamera = 1` and a frame with the Arcturus camera plugged in?
- Does SteamVR's Room View work with the Arcturus camera (the Rectus project says if Room View does not work, the HMD camera will not be accessible)?
- Does a native alpha-blend test app on the headset show the Arcturus colour feed without any code to select the camera?

## Update 2026-10-07: probe result, OVR Toolkit test, Frametop

- **CameraProbe with the headset connected** (SteamVR running, Frame driver loaded): `HasCamera = 0`, every call `NotSupportedForThisDevice`. A normal PC program cannot read camera frames through SteamVR on this setup (`camera.enableCamera` was unset; not changed). Path B is closed in practice.
- **OVR Toolkit over SteamVR's passthrough background** (user test): no passthrough shown. Path D (SteamVR overlays from the PC) is closed.
- **Frametop** (`DeeJanuz/frametop`, MIT, Python/C++/QML, 249 stars, active). It runs SteamVR **on the headset** (SteamOS) and publishes its screens as **SteamVR overlays** (OpenVR `IVRIPCResourceManagerClient.ImportDmabuf` + `SetOverlayTransformAbsolute`), driven by its own Wayland compositor. Its docs and source never mention passthrough, alpha blend or the colour camera. Where its panels sit over the real room, that is SteamVR's own environment on the Frame ("the Frame's home environment isn't a scene app", `GetCurrentSceneProcessId()` is 0 when no game runs), not something Frametop produces. It pauses itself when a VR game starts, so it is not a passthrough-over-a-game solution. Its only use of the cameras is hand tracking (`ft-camd`, borrowing XRService buffers via `pidfd_getfd`); it notes the Arcturus colour cameras are unreliable there. It depends on undocumented SteamVR/SteamOS internals and says so (a SteamOS update can break it).
- **What that means for Spatial Crop:** overlays on the headset's own SteamVR are a second possible mechanism: if the Frame's SteamVR home shows the room, a headset-side overlay app could sit over it. That is not a verified fact about our case. Gate 1 tests the documented OpenXR alpha-blend route; a follow-up (Gate 1b) can test an overlay on the headset only if Gate 1 fails.
