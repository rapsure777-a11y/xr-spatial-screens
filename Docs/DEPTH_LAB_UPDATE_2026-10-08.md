# Depth Lab update, 2026-10-08

Branch `claude/depth-lab`. Depth Lab stays experimental: off by default, only reachable when `depthlab.flag` exists in the app data folder.

## What changed

| Commit | Change |
|---|---|
| `6a94ce8` | Optional Depth Anything 3 small model (`models\use_v3.flag` + `models\da3\model.onnx`), aspect-correct input. V2 stays the default. |
| `e4afec4` | V3 input edge 336 -> 504 px (more fine relief, more GPU time). New **Pop** slider: an S-curve on depth in the panel shader (`_DepthParams.w`), pulls mid depths apart so objects separate from the background. 0% = depth as the model gives it. |
| `c1a43ac` | Warble reduction. Host skips depth when the picture barely changed (48x27 sample grid, fewer than 6 points moved by more than 12 levels), PC deadband 4% -> 6% and follow rate 0.6 -> 0.4. New **Smoothing** slider (default 40%): headset-side ease rate 6 -> 1.5 per second and a deadband of up to 14/255. Labs page is 150 px taller; the "laser offset" note was removed to make room. |

## Findings (from the user's headset test)

- V3 looked smoother than V2 but did not pop out as much. Likely causes (read from the code, not measured): lower input resolution (336 vs 518), the `1/depth` inversion compressing the near end, and smoothing tuned for V2. Addressed by the 504 input and the Pop slider.
- Some warble is inherent to per-frame monocular depth. Skip-when-unchanged removes it for static content; Smoothing trades the rest for lag.

## Status

- Headset build succeeds (0 errors); APK installed on the Frame; host rebuilt and running.
- User confirmed the result is good after the Pop/resolution change and the smoothing change ("this is all good").
- Not verified: Labs page layout overlap after the height change (reported fine by the user's test, no overlap reported); EditMode/PlayMode tests were not re-run for these commits.

## Not done / next candidates

- Skip the `1/depth` inversion for V3 (`max - depth`) if it still looks flat at high Pop.
- Edge-aware (bilateral) smoothing guided by the screen picture.
- Temporal-consistency video-depth model if warble remains a problem.
