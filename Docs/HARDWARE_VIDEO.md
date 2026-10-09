# Hardware video (HEVC) – first milestone report

Branch `claude/hardware-video` (from `claude/gate3b-placement` + the host work of `claude/gate3-interaction`). `main` and `develop` untouched. JPEG is still the default and the fallback for every stream.

## 1. What was built

PC window capture → scale (as before) → **ffmpeg `hevc_amf` (AMD hardware encoder)** → RTP over the loopback (frame boundaries from the RTP marker bit) → Annex-B access units → `XRSV` message over the existing connection → headset `HevcDecoder` (Android `MediaCodec`, `OMX.google.hevc.decoder`) → three 8-bit planes → GPU YUV→RGB blit into the **same shared source texture** the panels already sample.

* Panels, crops, placement, input, layout, keyboard, audio, passthrough: unchanged. They get the same `ScreenSource.View` texture, so several crops of one window share one decoded picture (one stream per window, not per crop).
* Selectable per session: palette button **Video: JPEG / HEVC** (saved; reopens every screen's stream). Test hook: `codec.txt` containing `hevc`/`jpeg` in the app's files folder overrides it.
* Fallback: if the PC has no ffmpeg it stays on JPEG; if the headset's decoder throws, that screen goes back to JPEG by itself.
* Quality tiers on the PC (never arbitrary sizes, so the encoder restarts rarely): 1280, 1920, 2400, 2880, 3440 wide; the existing smart-quality request (`R`) picks the tier. Bitrate about 0.19 bit per pixel at 60 fps (≈40 Mbit/s at 2880×1200), VBR with a 2× peak.

## 2. What the machines actually offer

| | Finding |
|---|---|
| PC encoder | RX 7900 XT, AMF: H.264, HEVC and AV1 encode 2880×1200 at 280 fps (H.264) / 5–7× real time (HEVC, AV1). All hardware. |
| Frame decoder | **No hardware video decoder is exposed to apps.** `MediaCodecList` has only Google software codecs (`OMX.google.*`: H.264, HEVC, VP8, VP9, up to 4096²). No AV1. No `/dev/video*` nodes; only the GPU (`kgsl`/`dri`). The Android layer ("Lepton") runs as a container. |
| Decode speed (benchmark, 2880×1200, 600 frames) | HEVC: 157 fps flat out; paced at 60 fps: **6 ms** input→output (max 27 ms), 0.65 CPU core. H.264: also keeps up but the decoder holds ~2 pictures (42 ms) and costs more latency. |
| In the app | HEVC decode costs 0.2–0.3 ms per picture for the windows tested (small deltas), the main-thread plane upload is the cost to watch for full-motion 2880 streams. |

So "hardware decode" is not available: it is **hardware encode on the PC, software decode on the Frame's CPU**. HEVC was chosen over H.264 (reorder delay) and AV1 (no decoder).

## 3. Measurements (same windows, same session, host-side statistics)

| | JPEG q90 4:4:4 | HEVC (ffmpeg pipe) |
|---|---|---|
| Terminal window 1032×950, bandwidth | 15–17 Mbit/s | **0.6 Mbit/s** |
| Terminal window, capture→shown round trip | **≈23 ms** | ≈76 ms first build, ≈45 ms after the nudge tuning |
| Browser window 1920×802, round trip | **≈22–30 ms** | ≈106–134 ms first build |
| Moving 30 fps test window 1920×830 | 39 Mbit/s, 16 ms encode | 11–19 Mbit/s, 9 ms encode, round trip ≈27 ms after tuning |
| Colour check (decoded frame vs source) | – | correct (BT.709 limited, tagged) |

HEVC is 4:2:0; JPEG here is 4:4:4. Coloured text edges are the place to look for a quality difference.

## 4. Latency finding (the important one)

ffmpeg's command-line pipeline holds the **last two pictures** until more input arrives (tested with `hevc_amf`, `hevc_mf`, every async/low-delay option, single decoder thread: always 2). A window that stops changing would never show its final frame. Workaround in the host: when a stream goes quiet, the last picture is submitted again (twice) so the real one is released; the first nudge waits 1.5× the recent picture gap (6–30 ms), the second 6 ms. The nudged copies are skip frames (a few bytes) and are not acknowledged. In steady 60 fps motion the hold still costs about two frame intervals (≈33 ms).

Removing the hold needs a **native AMF (or Media Foundation) encoder** instead of ffmpeg's CLI: submit a frame, query the output with a timeout. That is the next engineering step if HEVC wins on quality.

## 5. Bugs found on the way (all fixed)

* `low-latency` decoder flag: the Frame's OMX decoder rejects it and Android's own ACodec then crashes the whole app on the failed configure. Removed; do not set it.
* RTCP reports from ffmpeg land on the neighbouring port, i.e. another stream's receiver; they looked like lost video packets. The receiver now accepts RTP payload type 96 only.
* Large keyframes in small RTP packets overran the loopback; now 60 KB packets.
* The decode benchmark started itself from a leftover clip folder and fought the real decoders; it is opt-in (`bench/run.flag`).
* The PC tools' `.csproj` files were git-ignored by the Unity rule, so a fresh clone could not build the host; now tracked.

## 6. Not done / limits

* Quality in the headset is **not yet judged** (needs eyes): text, fine UI, motion, head-turn blockiness, enlarged crops.
* HV2 (shared-source crops) is true by construction (one decoded texture per window) but not yet checked in the headset.
* HV3 (2–3 windows): three streams ran at once without trouble in the first run; no measurements yet.
* ffmpeg must be on PATH (or `--ffmpeg`, `XRSS_FFMPEG`) on the PC; it is not bundled.
* Software decode uses CPU: 0.65 core for one 2880×1200 60 fps stream in the benchmark.

## 7. Recommendation (so far)

Keep both, JPEG default. HEVC clearly wins on bandwidth (≈25×); it loses on latency while it goes through ffmpeg. Decide default after the headset quality look; if HEVC looks clearly better, build the native encoder to remove the 2-frame hold.

## 8. Test procedure (Steam Frame)

1. Wake the Frame, start Lepton Development. The PC host and the app are already installed and running (JPEG).
2. Open the palette. Note how the game/browser screen looks in JPEG (text sharpness, motion).
3. Press **Video: HEVC**. Every screen reopens (≈1 s). Look at the same things again; click and drag; open the floating minimap crop.
4. Press **Video: JPEG** to go back at any time.
