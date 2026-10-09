# Depth Lab: architecture note (Gate D0)

Branch `claude/depth-lab`, from `claude/palette-polish` (the polished, headset-approved build). `main` and `develop` untouched. Experiment only; every step is optional and defaults to OFF.

## What exists today (inspected)

| Piece | How it works now | Relevance to depth |
|---|---|---|
| `ScreenSource` | Owns the backend, an upload texture and one mip-mapped, anisotropic `View` RenderTexture (BGRA sRGB). All panels and crops of a source sample this one texture. | The depth stage only needs to read `View` (and, later, a second texture beside it). It never changes how `View` is filled. |
| `PanelView` | One mesh per panel: 4 vertices, two triangles. Vertex attribute `TEXCOORD0 = (u*q, v*q, q)` (projective mapping, `QuadMath.CornerUvq`). Material `XrSpatial/Panel`. Corner handles, outline, closed label are children. | The flat mesh and material stay exactly as they are. |
| `XrSpatialPanel.shader` | Unlit, per-pixel projective map, `_Crop` rectangle in source UV, tint, edge highlight. Stereo macros present (`UNITY_VERTEX_OUTPUT_STEREO`), `Cull Off`, `ZWrite On`. | Crops are a UV rectangle in this shader, so a depth shader that uses the same UV formula gets crops for free. |
| Input | `InputForwarder`/`RemoteInputForwarder` raycast the flat quad (`QuadMath.RayToUv`) and map to source UV with the crop. | Untouched. Depth is purely visual; pointer maths stay on the flat plane. |
| Stereo | OpenXR, the shader uses the standard stereo macros, so one object is drawn once per eye by the XR system. | A vertex displacement in the shader is automatically seen from two slightly different eye positions: true parallax, no baked side-by-side image. |
| GPU | Adreno-class mobile GPU, Vulkan/GLES3: vertex texture fetch (`SAMPLE_TEXTURE2D_LOD` in a vertex shader) is supported. Scene is tiny (a few panels). | A panel grid of about 15,000 vertices sampled once per vertex per eye is negligible next to the 1080p+ fragment work already done. |

## Smallest clean insertion point

**A second renderer path inside `PanelView`, chosen per frame.**

```
ScreenSource.View (unchanged, same as today)
   |-- depth OFF or no depth map:  flat mesh + XrSpatial/Panel      (the existing path, untouched)
   `-- depth ON and a depth map:   grid mesh + XrSpatial/PanelDepth (new, optional)
```

* New shader `XrSpatial/PanelDepth` = the flat shader plus a vertex stage that moves each grid vertex along the panel's front normal by `(depth - focus) * relief`, clamped. The fragment stage is the same colour code, using the same undisplaced UV, so the picture stays glued to the surface and the displacement is seen as true stereo and head parallax.
* New grid mesh (about 128 x 72 quads), built only when depth is first switched on. Vertex positions follow the quad's four corners (bilinear); the per-vertex `(u*q, v*q, q)` is the exact affine function of the in-plane position, so non-parallelogram (perspective) quads still map correctly.
* The renderer swaps `sharedMesh` and `sharedMaterial` between the two sets. The flat mesh, flat material, `Rebuild()` and the highlight/tint/closed code are not edited. The depth material copies the flat material's properties every frame while depth is on (`CopyPropertiesFromMaterial`), so crop, tint, edge highlight and "window closed" dimming keep working in depth mode.
* The depth map belongs to the source (`ScreenSource.DepthMap`, from an optional `IDepthProvider` on the backend). No depth map, or `DepthLab.Enabled == false`: nothing changes, nothing is allocated, nothing extra is drawn.

## Depth source for D1

A prepared depth map, no inference. `DepthTestBackend` (new source kind `depthtest`) generates one clear, strategy-game-like frame (terrain, hills, river, buildings, units, trees, HUD with real text) and a matching grayscale depth map. If `depth_source.png` and `depth_map.png` exist in the app's data folder they are used instead, so a real screenshot with an offline-generated depth map can be tried without a rebuild.

## Controls (Depth Lab)

A LABS section on the palette, only visible when the file `depthlab.flag` exists in the app data folder (otherwise the approved palette is byte-for-byte the same layout). It opens a Depth Lab page: Depth on/off (default OFF each launch), Strength slider (default 20%, 0 = flat), Focus slider (which depth value stays on the panel plane), add and remove the test screen.

## Risk control

* Depth OFF: the renderer is the flat one; no depth material is created, no mesh built, no texture read.
* Clamp: maximum displacement is limited to 35 cm and scales with panel width, so nothing comes close to the face.
* Input: unchanged. At strong settings the visible picture can look offset from the pointer by up to the displacement; to be documented after the headset test, not fixed here.
* Nothing in streaming, decoding, capture, audio, placement or saved layouts is edited. The test source is a normal pattern-style source; a "Remove depth test" button deletes it.

## Open questions this experiment answers

Does depth-displaced flat imagery look better in the Frame, is text still readable, is head parallax comfortable, is it worth continuing to D2 (live source) and D3 (dynamic depth).
