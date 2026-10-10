using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using XrSpatial.App;
using XrSpatial.Capture;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.Tests
{
    /// <summary>The PC mouse cursor overlay on the real panel: placement from the host's report, crop and 3D handling, visibility rules, Off changing nothing, and the cursor shader.</summary>
    public class PcCursorTests
    {
        SpatialApp m_App;
        string m_Dir;
        PanelView m_Panel;
        const ushort Stream = 7;
        static readonly Vector3[] Corners = { new Vector3(-1, 1, 2), new Vector3(1, 1, 2), new Vector3(1, -1, 2), new Vector3(-1, -1, 2) };

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "xrss-cursor-" + System.Guid.NewGuid().ToString("N"));
            m_App = SpatialApp.Create(new SpatialApp.Options { forceDesktop = true, showControlPanel = false, autoLoadLast = false, parseCommandLine = false, layoutDirectory = m_Dir, background = BackgroundMode.Void });
            yield return null;
            m_App.Workspace.AutoSave = false;
            var src = m_App.Workspace.AddSource(new SourceDef { kind = "pattern", label = "p" });
            m_Panel = m_App.Workspace.AddSurface(src.id, (Vector3[])Corners.Clone());
            m_Panel.CursorStreamOverride = Stream;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Off);
            if (m_App) Object.Destroy(m_App.gameObject);
            yield return null;
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        static void Report(float u, float v, byte flags = CursorMath.FlagInside | CursorMath.FlagShowing, byte shape = CursorMath.ShapeArrow)
            => RemoteHost.SetCursorForTest(Stream, new CursorSample { flags = flags, shape = shape, u = u, v = v });

        Mesh CursorMesh() => m_Panel.transform.Find("PcCursor").GetComponent<MeshFilter>().sharedMesh;

        [UnityTest]
        public IEnumerator Off_ByDefault_NothingIsCreated_EvenWithAReportWaiting()
        {
            Assert.AreEqual(PcCursorMode.Off, RemoteHost.PcCursor);
            Report(0.5f, 0.5f);
            yield return null; yield return null;
            Assert.IsNull(m_Panel.Cursor);
            Assert.IsNull(m_Panel.transform.Find("PcCursor"), "no cursor object on a panel while the setting is off");
        }

        [UnityTest]
        public IEnumerator On_TheCursorSitsOnTheLogicalPlaneAtTheReportedPoint()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.25f, 0.5f);
            yield return null; yield return null;
            Assert.IsNotNull(m_Panel.Cursor);
            Assert.IsTrue(m_Panel.Cursor.Visible);
            Assert.IsTrue(m_Panel.transform.Find("PcCursor").gameObject.activeSelf);
            var expected = QuadMath.UvToWorld(new Vector2(0.25f, 0.5f), Corners);       // panel plane: a quarter from the left, halfway down
            var centre = CursorMesh().bounds.center;
            Assert.AreEqual(expected.x, centre.x, 1e-3f); Assert.AreEqual(expected.y, centre.y, 1e-3f);
            Assert.AreEqual(-0.5f, expected.x, 1e-3f);
            Assert.AreEqual(2f, expected.z, 1e-3f);
            // the arrow's tip (its hotspot) is on that point: the sprite's top-left vertex is just up-left of it
            var v = CursorMesh().vertices;
            Assert.Less(v[3].x, expected.x); Assert.Greater(v[3].y, expected.y);
        }

        [UnityTest]
        public IEnumerator Moving_FollowsEveryNewReport_WithoutWaitingForAVideoFrame()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.1f, 0.1f); yield return null; yield return null;
            float x1 = CursorMesh().bounds.center.x;
            Report(0.9f, 0.1f); yield return null;
            float x2 = CursorMesh().bounds.center.x;
            Assert.Greater(x2, x1 + 1f, "moved across the panel the very next frame");
        }

        [UnityTest]
        public IEnumerator Hidden_WhenNotOverTheWindow_WhenTheAppHidesTheCursor_AndWhenTheStreamIsGone()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.5f, 0.5f); yield return null; yield return null;
            Assert.IsTrue(m_Panel.Cursor.Visible);
            Report(0.5f, 0.5f, CursorMath.FlagShowing); yield return null;                 // the PC says the cursor is not over this window (or something covers it)
            Assert.IsFalse(m_Panel.Cursor.Visible);
            Report(0.5f, 0.5f, CursorMath.FlagInside); yield return null;                  // the application hid the cursor (a game with its own)
            Assert.IsFalse(m_Panel.Cursor.Visible);
            Report(0.5f, 0.5f); yield return null;
            Assert.IsTrue(m_Panel.Cursor.Visible);
            m_Panel.CursorStreamOverride = 9; yield return null;                           // a stream with no report
            Assert.IsFalse(m_Panel.Cursor.Visible);
        }

        [UnityTest]
        public IEnumerator Crop_ShowsTheCursorOnlyInsideTheCrop_AtTheCropRelativePosition()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            m_Panel.Def.crop = new Rect(0.5f, 0f, 0.5f, 1f); m_Panel.Rebuild();
            Report(0.25f, 0.5f); yield return null; yield return null;
            Assert.IsFalse(m_Panel.Cursor.Visible, "left of the crop: this panel shows no cursor");
            Report(0.75f, 0.5f); yield return null;
            Assert.IsTrue(m_Panel.Cursor.Visible);
            Assert.AreEqual(0f, CursorMesh().bounds.center.x, 1e-3f, "0.75 of the source is the middle of the right-half crop");
        }

        [UnityTest]
        public IEnumerator SbsPanel_OneFlatCursorForBothEyes_OnlyForTheEyePictureTheControllerMapsThrough()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            m_Panel.Def.stereo = StereoMode.SbsHalf;
            Report(0.25f, 0.5f); yield return null; yield return null;
            Assert.IsTrue(m_Panel.Cursor.Visible);
            Assert.AreEqual(0f, CursorMesh().bounds.center.x, 1e-3f, "0.25 of the SBS frame is the middle of the left-eye picture");
            Report(0.75f, 0.5f); yield return null;
            Assert.IsFalse(m_Panel.Cursor.Visible, "the right half is the other eye's picture: no second cursor");
            m_Panel.Def.swapEyes = true; yield return null;
            Assert.IsTrue(m_Panel.Cursor.Visible, "with swapped eyes the shown picture is the right half");
            Assert.AreEqual(1, m_Panel.transform.Find("PcCursor").GetComponents<MeshRenderer>().Length, "one renderer, not one per eye");
        }

        [UnityTest]
        public IEnumerator Resizing_AndMovingThePanel_KeepsTheCursorOnThePanelPoint()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.8f, 0.3f); yield return null; yield return null;
            var big = new[] { new Vector3(-2, 1.5f, 3), new Vector3(2, 1.5f, 3), new Vector3(2, -1.5f, 3), new Vector3(-2, -1.5f, 3) };           // moved back, bigger, other shape
            for (int i = 0; i < 4; i++) m_Panel.Def.corners[i] = big[i];
            m_Panel.Rebuild(); yield return null;
            var expected = QuadMath.UvToWorld(new Vector2(0.8f, 0.7f), big);
            var centre = CursorMesh().bounds.center;
            Assert.AreEqual(expected.x, centre.x, 1e-3f); Assert.AreEqual(expected.y, centre.y, 1e-3f); Assert.AreEqual(expected.z, centre.z, 1e-3f);
        }

        [UnityTest]
        public IEnumerator TiltedPanel_CursorStillLandsOnTheTiltedPlane()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            var tilted = new[] { new Vector3(-1, 1, 1.6f), new Vector3(1, 1, 1.6f), new Vector3(1, -1, 2.4f), new Vector3(-1, -1, 2.4f) };       // top edge nearer than the bottom edge
            for (int i = 0; i < 4; i++) m_Panel.Def.corners[i] = tilted[i];
            m_Panel.Rebuild();
            Report(0.5f, 0.5f); yield return null; yield return null;
            var expected = QuadMath.UvToWorld(new Vector2(0.5f, 0.5f), tilted);
            Assert.AreEqual(expected.z, CursorMesh().bounds.center.z, 1e-3f);
            Assert.AreEqual(2f, expected.z, 0.2f);
        }

        [UnityTest]
        public IEnumerator SettingOff_RemovesTheObject_AndHighIsALargerCursorThanNormal()
        {
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.5f, 0.5f); yield return null; yield return null;
            var mesh = CursorMesh();
            float normal = (mesh.vertices[3] - mesh.vertices[0]).magnitude;
            RemoteHost.SetPcCursor(PcCursorMode.High); yield return null;
            mesh = CursorMesh();
            float high = (mesh.vertices[3] - mesh.vertices[0]).magnitude;
            Assert.Greater(high, normal * 1.2f); Assert.Less(high, normal * 1.8f);
            RemoteHost.SetPcCursor(PcCursorMode.Off); yield return null; yield return null;
            Assert.IsNull(m_Panel.Cursor);
            Assert.IsNull(m_Panel.transform.Find("PcCursor"));
        }

        [UnityTest]
        public IEnumerator Palette_HasNoCursorButtonOffTheHeadset_AndTheReadoutExplainsItself()
        {
            yield return null;
            Assert.IsFalse(m_App.Palette.TryGetButtonWorldCenter("PC cursor: Off", out _), "the setting belongs to the headset build (the PC host is what reports the cursor)");
            Assert.That(CursorReadout.Build(m_Panel, m_App.Tool), Does.Contain("off"));
            RemoteHost.SetPcCursor(PcCursorMode.Normal);
            Report(0.5f, 0.5f);
            Assert.That(CursorReadout.Build(m_Panel, m_App.Tool), Does.Contain("source 0.500,0.500"));
        }
    }

    /// <summary>The cursor shader and sprite atlas, rendered from a plain camera (no app running, as in the SBS shader tests).</summary>
    public class PcCursorShaderTests
    {
        // ------------------------------------------------------------------ the shader

        static Color RenderCursorPixel(PcCursorMode mode, byte shape, Vector2 cellPoint, Color background)
        {
            var root = new GameObject("CursorShaderTest");
            try
            {
                var q = new[] { new Vector3(-1, 1, 3), new Vector3(1, 1, 3), new Vector3(1, -1, 3), new Vector3(-1, -1, 3) };
                var mesh = new Mesh();
                mesh.SetVertices(new List<Vector3> { q[3], q[2], q[1], q[0] });                      // BL BR TR TL, uv (0,0) (1,0) (1,1) (0,1)
                mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
                mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
                var atlas = new Texture2D(CursorArt.Width, CursorArt.Height, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                atlas.LoadRawTextureData(CursorArt.Render()); atlas.Apply();
                var mat = new Material(Shader.Find("XrSpatial/Cursor"));
                mat.SetTexture("_MainTex", atlas); mat.SetVector("_Cell", CursorArt.CellRect(shape)); mat.SetFloat("_High", mode == PcCursorMode.High ? 1f : 0f);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = mat;
                var camGo = new GameObject("cam"); camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = background;
                cam.fieldOfView = 53f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 20f;
                var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                cam.targetTexture = rt; cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                float half = 3f * Mathf.Tan(26.5f * Mathf.Deg2Rad);
                int x = Mathf.RoundToInt((0.5f + (cellPoint.x * 2f - 1f) / (2f * half)) * 255f);
                int y = Mathf.RoundToInt((0.5f + ((1f - cellPoint.y) * 2f - 1f) / (2f * half)) * 255f);      // cellPoint is y down
                var c = tex.GetPixel(x, y);
                cam.targetTexture = null; Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(mesh); Object.DestroyImmediate(mat); Object.DestroyImmediate(atlas);
                return c;
            }
            finally { Object.DestroyImmediate(root); }
        }

        static float Linear(float gamma) => Mathf.GammaToLinearSpace(gamma);                 // the camera's clear colour reaches the linear target converted

        [Test]
        public void Shader_ArrowBodyIsWhite_OutlineIsDark_FarAwayIsTheBackground()
        {
            var grey = new Color(0.5f, 0.5f, 0.5f, 1f);
            var body = RenderCursorPixel(PcCursorMode.Normal, CursorMath.ShapeArrow, new Vector2(0.13f, 0.33f), grey);
            Assert.Greater(body.r, 0.9f, "inside the arrow the cursor is white");
            var far = RenderCursorPixel(PcCursorMode.Normal, CursorMath.ShapeArrow, new Vector2(0.85f, 0.85f), grey);
            Assert.AreEqual(Linear(0.5f), far.r, 0.03f, "away from the shape the picture shows through");
            var edge = RenderCursorPixel(PcCursorMode.Normal, CursorMath.ShapeArrow, new Vector2(0.045f, 0.40f), grey);       // just left of the arrow's left edge: the dark outline
            Assert.Less(edge.r, 0.12f, "the outline is dark");
        }

        [Test]
        public void Shader_HighVisibilityAddsAWarmHaloAroundTheOutline_NormalDoesNot()
        {
            var grey = new Color(0.5f, 0.5f, 0.5f, 1f);
            var p = new Vector2(0.3324f, 0.275f);                                          // about 3.5 texels outside the arrow's upper-right edge
            var normal = RenderCursorPixel(PcCursorMode.Normal, CursorMath.ShapeArrow, p, grey);
            var high = RenderCursorPixel(PcCursorMode.High, CursorMath.ShapeArrow, p, grey);
            Assert.AreEqual(Linear(0.5f), normal.r, 0.05f);
            Assert.Greater(high.r - high.b, 0.15f, "the halo is warm (yellow), not grey");
        }
    }
}
