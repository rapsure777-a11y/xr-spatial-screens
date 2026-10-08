# Gate 2: streaming a captured Windows window into the native Frame app

Result (2026-10-07): **PASSED.** User on the headset: "Everything looks good ... it looks really good", dense small text readable, animated line found (line 25 of the test text), room still visible in colour around the panel. Controllers did nothing, as expected (no input in this gate).

## What was built
- `Tools/XrssStream` (.NET 8): reads the shared-memory frames that `XrssCapture` already publishes (FrameProtocol) and serves them as JPEG over TCP on **127.0.0.1:5600 only**. Per frame: 'XRSF', jpeg length, width, height, seq, JPEG bytes. The headset acks each shown frame (seq) so the PC measures the round trip.
- `Experiments/Gate1Passthrough/Assets/Scripts/Gate2Stream.cs`: background thread TCP client, latest-frame-wins, `Texture2D.LoadImage` per rendered frame, panel resized to the frame's aspect, stats to log and to the board.
- Transport: `adb reverse tcp:5600 tcp:5600` over the adb link; nothing is exposed on the LAN, no firewall change.
- `Experiments/Gate1Passthrough/gate2.ps1`: starts capture + sender + tunnel + app (`-Hwnd` to pick the window, `-Action stop|log`).

## Measurements (animated 1200x544 console window, sent 1202x576 JPEG q75)
| Metric | Result | Gate 2 target |
|---|---|---|
| Frame rate shown on the headset | ~21 fps (limited by the animation source, sender cap 30) | 30 fps |
| PC capture -> headset shows frame -> ack back (round trip) | avg ~21-22 ms, max 36 ms | < 150 ms |
| PC encode (JPEG) | ~2 ms/frame | |
| Headset decode (LoadImage) | ~5 ms avg, 7 ms max | |
| Bandwidth | ~25 Mbit/s for one 1200x576 window | |
| Dropped on headset | 1 in ~440 | |
A static window produces ~2 fps because Windows.Graphics.Capture only delivers on change; that is expected, not a fault.

## Caveats (not yet tested)
- The 30 fps target was not reached by the source in this test (21 fps animation); the pipeline had headroom (2 ms encode, 5 ms decode).
- Only one small window. A 3440x1440 window, several windows, a heavy game running at the same time, and Wi-Fi instead of the adb link at that bitrate are unmeasured. JPEG at ~25 Mbit/s per window will not scale to many large windows: hardware H.264 is the production path.
- The round trip is capture -> screen-present-minus-one-frame, measured on the PC clock; it is not a photon-to-photon figure.
- Transport here is loopback + adb. A real build needs LAN transport with pairing and encryption.
- Window text was a Windows console; photographic or high-motion content was not judged.
