# Native Steam Frame client: architecture and plan (proposal, nothing built yet)

Goal: Spatial Crop as a real mixed-reality app: floating, clickable screens over the real room in colour (Arcturus Vision Camera), not over a virtual background.
Status 2026-10-06. Companion to `PASSTHROUGH.md`. Nothing here has been committed to code; this is for a go/no-go decision.

## The idea in one picture
```
PC (Windows)                                        Steam Frame (SteamOS, standalone)
-----------------------------                       -----------------------------------------
games / apps (any window)                           Spatial Crop client (Unity, OpenXR, ALPHA_BLEND)
  |  Windows.Graphics.Capture                         placement, crop, layouts, palette (existing code)
  v                                                   panels = video textures      <-- frames
Spatial Crop Host (.NET, extends XrssCapture) ---->   controller ray -> panel UV -> source position
  encode + send frames (LAN)                          |
  window list, window geometry                        v  small input messages (source x,y, buttons, wheel)
  SendInput (existing mapping)  <----------------------+
```
Headset-side composition does the colour passthrough: the client submits frames with alpha and selects the alpha-blend mode; the system compositor (with the Arcturus feed when plugged in) fills in the room. The app contains no camera code.

## The four things to verify, and what is known

| # | Question | Evidence so far | Verified here? |
|---|---|---|---|
| 1 | Do native OpenXR apps on Frame get Arcturus colour passthrough? | Arcturus developer page: passthrough is requested with `XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND` (alpha swapchain, alpha preserved), "works with apps running on Steam Frame". Press: the camera works "in the system compositor, beneath the application". Valve's Steamworks Unity/custom-engine/compatibility pages say nothing about passthrough | **Yes (Gate 1, 2026-10-07).** A native Android/OpenXR Unity app on the Frame requested alpha blend, the runtime offered modes 1 and 3, and the user saw the room in full colour with the Arcturus camera attached. See `GATE1_PASSTHROUGH.md` |
| 2 | Can captured Windows content be streamed into a native headset app? | Technically yes: Frame runs Android APKs (via Lepton), native Linux ARM64, and Windows x86 builds via Proton/FEX; it has a normal network stack (Steam Link itself streams to it). Nothing in the docs forbids sockets | **No.** Needs a transport proof (below) |
| 3 | How much of our code carries over? | See table below (measured from the repo) | **Yes** (code inspection) |
| 4 | Simplest path to real-room passthrough with interactive screens? | See "Options" and "Recommended path" | Partly |

## Reuse of the existing code (measured, `Assets/Scripts`)
| Part | Lines | On the headset client | Notes |
|---|---|---|---|
| `Core` (QuadMath, Layout) | 447 | reused unchanged | pure C#, no Windows or Unity-platform code; 29 edit-mode tests cover it |
| `Spatial` minus InputForwarder (PanelView, SurfaceTool, SpatialWorkspace, Pointer) | 845 | reused unchanged | placement, 4-corner editing, crop, grab/scale, saved layouts, palette hit-testing |
| `Spatial/InputForwarder.cs` | 285 | **split** | the state machine (press lock, drag clamp, release on tracking loss, dead zone, double-click snap) and the ray-to-source mapping stay on the client and emit messages instead of calling `SendInput`. The Win32 parts (window rectangle, activation check, `SendInput`) move to the PC host, where they are already tested |
| `Capture`: ScreenSource, ICaptureBackend, PatternBackend, CaptureCatalog | ~290 | reused | `ICaptureBackend` is the seam: add `NetworkBackend` next to `SidecarBackend` |
| `Capture/SidecarBackend` + `Tools/XrssCapture` (Windows.Graphics.Capture) | 206 + helper | **moves to the PC host** | the helper already produces the frames; it gains an encoder and a server |
| `App` (SpatialApp, palette, hand input, XR pointer) | 1187 | mostly reused | the window picker needs the window list over the network; the Frame controller profile needs Valve's OpenXR utilities package (or our profile ported); the desktop control panel is not needed |
| Shaders | 2 | reused, one change | output must keep alpha so the compositor can blend; background clear alpha 0 |

Rough split: about 80 percent of the existing C# is reused; the new parts are the host (encode, serve, input apply), the client network backend and video decode, and the Android/ARM64 build setup.

## Options
| Option | Real-room colour passthrough | Work | Main risk |
|---|---|---|---|
| A. Keep the PC OpenXR app | No (runtime offers opaque only; verified) | none | cannot be fixed by us |
| B. PC reads the camera via `IVRTrackedCamera` and draws it | maybe, monochrome or whatever SteamVR exposes | small to prototype | public report says it fails on Frame (`ValveSoftware/openvr #1926`); probe pending (headset off) |
| C. Native Frame client + PC host (this document) | yes, through the documented alpha-blend route | large (weeks of focused work for a first version) | passthrough on Frame not verified by us; video transport latency; I cannot see the headset, every test needs you wearing it |
| D. OpenVR overlay app (floating windows as SteamVR overlays, like OVR Toolkit) over SteamVR's room-view background | possibly, if the Frame lets overlays sit over its passthrough | medium (a separate overlay renderer; each 4-corner panel is pre-warped into a rectangular overlay texture with alpha) | the same issue 1926 says overlays cannot access or show passthrough; whether the *background* behind overlays can be passthrough while streaming is unknown |

D has one free experiment: you already own OVR Toolkit. If, with no game running, SteamVR's background can be set to passthrough/room view and OVR Toolkit windows float over it on your Frame, D becomes a cheaper route than C. If not, D is closed.

## Transport (question 2), simplest first
- **T1 JPEG frames over TCP (proof of concept).** Capture, JPEG-encode (hardware not needed), push changed frames; client decodes on a worker thread into a texture. 1080p at 30 fps is roughly 15 to 40 Mbit/s per panel on a LAN. Simple, debuggable, no native plugin. Weak for many large panels.
- **T2 H.264/H.265 with hardware encode (AMD AMF/Media Foundation on the PC) and hardware decode (Android MediaCodec on the headset).** Low bandwidth, low latency; needs a small native decode plugin or a package. This is the production path.
- **T3 WebRTC.** Unity has an official package for Android; the PC side needs a WebRTC sender library. More moving parts.
Input goes the other way as tiny messages: window id, source position (0..1), button/wheel events. Latency budget for a strategy game: about 60 to 120 ms click-to-visible, which is acceptable.

## Concrete blockers
1. **Unity Android module is not installed here** (only the Windows player). Installing it is a large Unity Hub download; needed for the recommended Android target (Steamworks recommends "targeting Android and Unity's built-in OpenXR plugin", IL2CPP, ARM64, Vulkan, Android API 29 to 30). An alternative to test first: our existing Windows build run on the headset through Proton/FEX, which needs no Android module but is slower and less supported.
2. **Passthrough on Frame is unproven for us.** Docs are silent; claims are second-hand. First test: a tiny alpha-blend app on the headset (gate 1 below).
3. **Deployment needs the headset in developer mode, Lepton Development running and `adb connect`** (Steamworks Unity page). I can drive adb from the PC once connected, but cannot see the headset.
4. **Video decode on the headset** (T2) needs native code or a package we have not chosen.
5. **PC GPU contention:** encoding screens while a game renders costs GPU time (fine for strategy and management games, a concern for heavy 3D).
6. **Discovery and pairing on the LAN** (address entry or a simple broadcast) and basic security (do not accept control from strangers on the network): needs a pairing code; input injection is powerful.
7. **Controller profile on the headset build:** use Valve's OpenXR utilities package; our custom profile is only proven in the PC build.
8. **Everything on the headset is invisible to me:** the diagnostics log (controller state, blend mode, frame rate, latency counters) is the main debugging tool.

## Recommended path (gated; stop at any failed gate)
0. **Now, no code:** when the headset is charged, run `Tools\CameraProbe\bin\Release\net8.0\CameraProbe.exe` (read-only), and try OVR Toolkit over SteamVR's passthrough background (option D experiment).
1. **Gate 1, passthrough proof:** minimal Unity app (a few coloured panels, alpha-blend feature, alpha swapchain) built for the headset (Android or Proton). Pass: the room is visible in colour behind the panels with the Arcturus camera.
2. **Gate 2, transport proof:** PC host sends one captured window as JPEG over TCP to the same app; the existing `PanelView` shows it. Pass: readable text at 30 fps, under about 150 ms.
3. **Gate 3, interaction:** controller click, drag and scroll through the host (reusing the tested mapping), palette window list over the network, saved layouts.
4. **Gate 4, quality:** hardware H.264 decode, several panels, reconnect, pairing code.
5. Only then fold both builds into one Unity project with two targets (PC dev client and headset client) and update the docs.

Gates 1 and 2 are each about a day of work plus a headset session; together they answer whether C is viable before any large investment.

## What I need from you before step 1
- Go-ahead to install Unity's Android build support (large download) or to try the Proton route first.
- The headset in developer mode with Lepton Development reachable for `adb connect`, when we get there.
- A short headset session after each gate.
