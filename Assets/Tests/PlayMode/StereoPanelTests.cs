using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.App;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>SBS stereo on the real panel shader (rendered from a mono camera, which is the left eye) and on the real panel, palette and tool.</summary>
    public class StereoPanelTests
    {
        // ------------------------------------------------------------------ the shader itself

        static Color32 RenderPixel(Material mat, Vector2 uvInPicture)
        {
            var root = new GameObject("StereoShaderTest");
            try
            {
                var q = new[] { new Vector3(-1, 1, 3), new Vector3(1, 1, 3), new Vector3(1, -1, 3), new Vector3(-1, -1, 3) };
                var mesh = new Mesh();
                mesh.SetVertices(new List<Vector3>(q));
                var uvq = QuadMath.CornerUvq(q);
                mesh.SetUVs(0, new List<Vector3> { uvq[0], uvq[1], uvq[2], uvq[3] });
                mesh.SetTriangles(QuadMath.Triangles, 0);
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = mat;
                var camGo = new GameObject("cam");
                camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.fieldOfView = 53f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 20f;
                var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                // the quad spans +-1 at distance 3 with a 53 degree vertical fov: it covers about 0.67 of the view, centred
                float half = 3f * Mathf.Tan(26.5f * Mathf.Deg2Rad);
                int x = Mathf.RoundToInt((0.5f + (uvInPicture.x * 2f - 1f) / (2f * half)) * 255f);
                int y = Mathf.RoundToInt((0.5f + (uvInPicture.y * 2f - 1f) / (2f * half)) * 255f);
                var c = tex.GetPixel(x, y);
                cam.targetTexture = null; Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(mesh);
                return c;
            }
            finally { Object.DestroyImmediate(root); }
        }

        static Texture2D RedLeftBlueRight()
        {
            var t = new Texture2D(64, 8, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 64; x++) t.SetPixel(x, y, x < 32 ? Color.red : Color.blue);
            t.Apply();
            return t;
        }

        static Material PanelMat(Texture2D tex)
        {
            var m = new Material(Shader.Find("XrSpatial/Panel"));
            m.SetTexture("_MainTex", tex);
            return m;
        }

        static void AssertColour(Color32 c, Color expect, string what)
        {
            Assert.AreEqual(expect.r * 255f, c.r, 12f, what + " red"); Assert.AreEqual(expect.g * 255f, c.g, 12f, what + " green"); Assert.AreEqual(expect.b * 255f, c.b, 12f, what + " blue");
        }

        [Test]
        public void Shader_FlatShowsTheWholeSource_Sbs_ShowsOnlyTheLeftEyeHalfAcrossTheWholePanel_AndSwapShowsTheRightHalf()
        {
            Assume.That(Shader.Find("XrSpatial/Panel") != null);
            var tex = RedLeftBlueRight();
            var m = PanelMat(tex);
            // Flat (keyword off): the panel's left quarter is the red half and the right quarter the blue half
            AssertColour(RenderPixel(m, new Vector2(0.25f, 0.5f)), Color.red, "flat left quarter");
            AssertColour(RenderPixel(m, new Vector2(0.75f, 0.5f)), Color.blue, "flat right quarter");
            // SBS: a mono camera is the left eye, so BOTH quarters show the left (red) picture stretched over the panel
            m.EnableKeyword("_SBS"); m.SetVector("_Stereo", StereoMath.ShaderParams(false, 0f));
            AssertColour(RenderPixel(m, new Vector2(0.25f, 0.5f)), Color.red, "sbs left quarter");
            AssertColour(RenderPixel(m, new Vector2(0.75f, 0.5f)), Color.red, "sbs right quarter");
            // Swap eyes: the left eye now reads the right half
            m.SetVector("_Stereo", StereoMath.ShaderParams(true, 0f));
            AssertColour(RenderPixel(m, new Vector2(0.25f, 0.5f)), Color.blue, "swapped left quarter");
            AssertColour(RenderPixel(m, new Vector2(0.75f, 0.5f)), Color.blue, "swapped right quarter");
            // back to Flat: identical to the first result
            m.DisableKeyword("_SBS");
            AssertColour(RenderPixel(m, new Vector2(0.25f, 0.5f)), Color.red, "flat again, left quarter");
            AssertColour(RenderPixel(m, new Vector2(0.75f, 0.5f)), Color.blue, "flat again, right quarter");
            Object.DestroyImmediate(m); Object.DestroyImmediate(tex);
        }

        [Test]
        public void Shader_CropSelectsTheSbsFrame_SoBlackBarsAtTheSidesDoNotMisalignTheEyes()
        {
            Assume.That(Shader.Find("XrSpatial/Panel") != null);
            // a pillar-boxed capture, like a 16:9 SBS video fullscreen on an ultrawide: [bar 0.2][left picture 0.2..0.5][right picture 0.5..0.8][bar 0.2]
            var tex = new Texture2D(100, 8, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 100; x++) tex.SetPixel(x, y, x < 20 || x >= 80 ? Color.black : x < 50 ? Color.red : Color.blue);
            tex.Apply();
            var m = PanelMat(tex);
            m.EnableKeyword("_SBS"); m.SetVector("_Stereo", StereoMath.ShaderParams(false, 0f));
            // without a crop the left eye gets a black bar plus the left picture: this is the misalignment
            Assert.Less(RenderPixel(m, new Vector2(0.1f, 0.5f)).r, 40, "uncropped: the left eye picture starts with the black bar");
            // cropping the video area (x 0.2..0.8) puts the eyes back in line: the left eye is red across the whole panel
            m.SetVector("_Crop", new Vector4(0.2f, 0f, 0.6f, 1f));
            for (float u = 0.05f; u < 1f; u += 0.15f) AssertColour(RenderPixel(m, new Vector2(u, 0.5f)), Color.red, $"cropped left eye at u {u:0.00}");
            m.SetVector("_Stereo", StereoMath.ShaderParams(true, 0f));
            for (float u = 0.05f; u < 1f; u += 0.15f) AssertColour(RenderPixel(m, new Vector2(u, 0.5f)), Color.blue, $"cropped, swapped, at u {u:0.00}");
            Object.DestroyImmediate(m); Object.DestroyImmediate(tex);
        }

        [Test]
        public void Shader_TheLargestConvergenceShift_NeverPullsInTheOtherEyesPicture()
        {
            Assume.That(Shader.Find("XrSpatial/Panel") != null);
            var tex = RedLeftBlueRight();
            var m = PanelMat(tex);
            m.EnableKeyword("_SBS");
            foreach (float off in new[] { StereoMath.MaxOffset, -StereoMath.MaxOffset })
            {
                m.SetVector("_Stereo", StereoMath.ShaderParams(false, off));
                for (float u = 0.02f; u < 1f; u += 0.12f) AssertColour(RenderPixel(m, new Vector2(u, 0.5f)), Color.red, $"offset {off} at u {u:0.00}");
            }
            Object.DestroyImmediate(m); Object.DestroyImmediate(tex);
        }

        // ------------------------------------------------------------------ the real panel, tool and palette

        SpatialApp m_App;
        ScriptedPointer m_Ptr;
        string m_Dir;
        static readonly Vector3 Head = new Vector3(0, 1.6f, 0);

        static Vector3[] FourPoints() => new[]
        {
            Head + new Vector3(-0.8f, 0.45f, 2f), Head + new Vector3(0.8f, 0.45f, 2f), Head + new Vector3(0.8f, -0.45f, 2f), Head + new Vector3(-0.8f, -0.45f, 2f),
        };

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "xrss-stereo-" + System.Guid.NewGuid().ToString("N"));
            m_App = SpatialApp.Create(new SpatialApp.Options { forceDesktop = true, showControlPanel = false, autoLoadLast = false, parseCommandLine = false, layoutDirectory = m_Dir, background = BackgroundMode.Void });
            yield return null;
            m_Ptr = new ScriptedPointer { HeadTransform = m_App.Cam.transform };
            m_App.Tool.Pointer = m_Ptr;
            m_App.Workspace.AutoSave = false;
            m_App.Tool.AutoFitAspect = false;
            m_App.AddPattern();
            yield return null; yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (m_App) Object.Destroy(m_App.gameObject);
            yield return null;
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        [UnityTest]
        public IEnumerator Panel_Sbs_TurnsTheKeywordOn_BypassesDepth_KeepsTheCrop_AndFlatRestoresEverything()
        {
            var panel = m_App.Tool.CreateFromPoints(FourPoints(), Head);
            Assert.NotNull(panel);
            panel.Def.crop = new Rect(0.1f, 0.2f, 0.5f, 0.5f);
            m_App.Workspace.Touch(panel);
            var mat = panel.GetComponent<MeshRenderer>().sharedMaterial;
            var cropBefore = mat.GetVector("_Crop");
            var cornersBefore = (Vector3[])panel.Def.corners.Clone();
            yield return null;
            Assert.IsFalse(mat.IsKeywordEnabled("_SBS"), "a Flat panel must not have the SBS keyword");

            m_App.Tool.Select(panel);
            m_App.Tool.CycleStereo();
            yield return null;
            Assert.AreEqual(StereoMode.SbsHalf, panel.Def.stereo);
            Assert.IsTrue(mat.IsKeywordEnabled("_SBS"));
            Assert.IsFalse(panel.DepthActive, "depth is bypassed on a 3D screen");
            Assert.AreEqual(cropBefore, mat.GetVector("_Crop"), "the crop stays: in 3D it selects the SBS frame inside the capture");
            Assert.AreEqual(cornersBefore, panel.Def.corners, "turning 3D on never moves or resizes the panel");

            m_App.Tool.ToggleSwapEyes();
            m_App.Tool.NudgeStereoOffset(1); m_App.Tool.NudgeStereoOffset(1);
            yield return null;
            Assert.IsTrue(panel.Def.swapEyes);
            Assert.AreEqual(2 * StereoMath.OffsetStep, panel.Def.stereoOffset, 1e-5f);
            Assert.AreEqual(StereoMath.ShaderParams(true, panel.Def.stereoOffset), mat.GetVector("_Stereo"));

            m_App.Tool.CycleStereo(); m_App.Tool.CycleStereo();            // Full, then Flat
            yield return null;
            Assert.AreEqual(StereoMode.Flat, panel.Def.stereo);
            Assert.IsFalse(mat.IsKeywordEnabled("_SBS"), "back to Flat: the original shader variant");
            Assert.AreEqual(cropBefore, mat.GetVector("_Crop"));
            Assert.AreEqual(cornersBefore, panel.Def.corners);
        }

        [UnityTest]
        public IEnumerator Panel_Sbs_PointerInputMapsOntoOneEyesPicture_AndFlatMappingIsUnchanged()
        {
            var panel = m_App.Tool.CreateFromPoints(FourPoints(), Head);
            var flat = panel.UvToSource(new Vector2(1f, 0.5f));
            Assert.AreEqual(1f, flat.x, 1e-5f);
            panel.Def.stereo = StereoMode.SbsHalf;
            var s = panel.UvToSource(new Vector2(1f, 0.5f));
            Assert.AreEqual(0.5f, s.x, 1e-5f, "the right edge of the panel is the right edge of the left-eye picture");
            panel.Def.crop = new Rect(0.2f, 0f, 0.6f, 1f);
            Assert.AreEqual(0.5f, panel.UvToSource(new Vector2(1f, 0.5f)).x, 1e-5f, "inside a crop 0.2..0.8 the left-eye picture ends at 0.5");
            Assert.AreEqual(0.2f, panel.UvToSource(new Vector2(0f, 0.5f)).x, 1e-5f);
            panel.Def.crop = new Rect(0, 0, 1, 1);
            Assert.AreEqual(0.5f, s.y, 1e-5f);
            panel.Def.swapEyes = true;
            Assert.AreEqual(1f, panel.UvToSource(new Vector2(1f, 0.5f)).x, 1e-5f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Tool_CropIsRefusedOnA3dScreen_AndFitPictureUsesTheEyePictureShape()
        {
            var panel = m_App.Tool.CreateFromPoints(FourPoints(), Head);
            m_App.Tool.Select(panel);
            panel.Def.stereo = StereoMode.SbsHalf;
            m_App.Tool.BeginCrop();
            Assert.AreNotEqual(ToolMode.Crop, m_App.Tool.Mode, "crop must not start on a 3D screen");
            panel.Def.stereo = StereoMode.Flat;
            m_App.Tool.BeginCrop();
            Assert.AreEqual(ToolMode.Crop, m_App.Tool.Mode, "crop still works on a Flat screen");
            m_App.Tool.SetIdle();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_ThreeDButton_CyclesTheSelectedPanel_SwapEyesToggles_WithoutASelectionNothingChanges()
        {
            var panel = m_App.Tool.CreateFromPoints(FourPoints(), Head);
            m_App.Tool.Select(null);
            yield return null;
            Assert.IsTrue(m_App.Palette.TryGetButtonWorldCenter("3D: Flat", out var at), "3D button not found");
            yield return PressAt(at);
            Assert.AreEqual(StereoMode.Flat, panel.Def.stereo, "no selection: nothing changes");
            m_App.Tool.Select(panel);
            yield return null;
            Assert.IsTrue(m_App.Palette.TryGetButtonWorldCenter("3D: Flat", out at));
            yield return PressAt(at);
            Assert.AreEqual(StereoMode.SbsHalf, panel.Def.stereo);
            yield return null;
            Assert.IsTrue(m_App.Palette.TryGetButtonWorldCenter("Swap eyes: off", out at), "swap button not found");
            yield return PressAt(at);
            Assert.IsTrue(panel.Def.swapEyes);
        }

        [UnityTest]
        public IEnumerator StereoTest_AddsAFlatPanel_WithADifferentPicturePerEye_AndRemoves()
        {
            m_App.AddStereoTest();
            yield return null; yield return null;
            var panel = m_App.Tool.Selected;
            Assert.NotNull(panel);
            Assert.AreEqual(StereoMode.Flat, panel.Def.stereo, "the test starts in Flat so both eye pictures are visible");
            Assert.AreEqual(1920, panel.Source.Width); Assert.AreEqual(1080, panel.Source.Height);
            Assert.IsTrue(panel.PictureAspect(out float flatAspect)); Assert.AreEqual(1920f / 1080f, flatAspect, 1e-3f);
            panel.Def.stereo = StereoMode.SbsHalf;
            Assert.IsTrue(panel.PictureAspect(out float sbsAspect)); Assert.AreEqual(16f / 9f, sbsAspect, 1e-3f, "half-SBS 1920x1080 is one 16:9 picture per eye");
            panel.Def.stereo = StereoMode.SbsFull;
            Assert.IsTrue(panel.PictureAspect(out float fullAspect)); Assert.AreEqual(8f / 9f, fullAspect, 1e-3f, "this same file read as full-SBS would be a tall 8:9 eye picture, which is why the mode matters");

            var b = new XrSpatial.Capture.StereoTestBackend();
            Texture2D t = null;
            Assert.IsTrue(b.PollInto(ref t));
            Color Top(int x, int yFromTop) => t.GetPixel(x, yFromTop);   // frames are stored top row first, so texture row 0 is the picture top
            Assert.Greater(Top(20, 100).b, 0.8f); Assert.Less(Top(20, 100).r, 0.3f);              // left picture: cyan marker, top-left
            Assert.Greater(Top(1870, 100).r, 0.8f); Assert.Less(Top(1870, 100).b, 0.4f);          // right picture: orange marker, top-right
            // the red near box is shifted right in the left picture and left in the right picture (crossed disparity = in front)
            bool Red(Color c) => c.r > 0.7f && c.g < 0.3f;
            Assert.IsFalse(Red(Top(620, 500)), "left eye: nothing red this far left"); Assert.IsTrue(Red(Top(640, 500)));
            Assert.IsTrue(Red(Top(960 + 612, 500)), "right eye: the box starts further left (inside its light frame)");
            Object.Destroy(t);

            m_App.RemoveStereoTest();
            yield return null;
            Assert.IsFalse(m_App.Workspace.Layout.sources.Exists(x => x.kind == "sbstest"));
        }

        IEnumerator PressAt(Vector3 target)
        {
            m_Ptr.Aim(m_App.Cam.transform.position, target);
            yield return null;
            m_Ptr.State.triggerDown = true; m_Ptr.State.triggerHeld = true;
            yield return null;
            m_Ptr.State.triggerHeld = false; m_Ptr.State.triggerUp = true;
            yield return null;
        }
    }
}
