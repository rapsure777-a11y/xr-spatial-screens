using System.Collections.Generic;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// The PC's mouse cursor drawn on one screen: a small flat sprite lying on the screen's own (logical, flat) plane at the spot the PC host says Windows has the cursor. It is
    /// the headset's picture of Windows' truth, not of the controller: it never reads the controller or its ray. It is created only when the PC cursor is switched on, and removed
    /// when it is switched off, so with the setting off a panel has no extra object and no per-frame work beyond one flag test.
    /// It is NOT displaced by Depth Lab: in 2.5D the picture's geometry moves in front of and behind the flat plane while the cursor stays on the plane where Windows takes input,
    /// which is what makes any mismatch with the picture (or the controller laser) visible. In 3D (SBS) modes it is one flat sprite shown identically to both eyes at the panel plane.
    /// </summary>
    public sealed class PanelCursor
    {
        static Texture2D s_Atlas;
        static readonly int MainTexId = Shader.PropertyToID("_MainTex"), CellId = Shader.PropertyToID("_Cell"), HighId = Shader.PropertyToID("_High"), ZTestId = Shader.PropertyToID("_ZTest");

        readonly PanelView m_Panel;
        readonly GameObject m_Go;
        readonly Mesh m_Mesh;
        readonly Material m_Material;
        readonly Vector3[] m_Verts = new Vector3[4];
        byte m_Shape = 255; PcCursorMode m_Mode = PcCursorMode.Off; bool m_ZAlways;

        /// <summary>What was drawn last (null while hidden), for the Labs readout.</summary>
        public bool Visible { get; private set; }
        public Vector2 SourcePos { get; private set; }
        public Vector2 PanelUv { get; private set; }
        public CursorSample Sample { get; private set; }

        static Texture2D Atlas
        {
            get
            {
                if (s_Atlas) return s_Atlas;
                s_Atlas = new Texture2D(CursorArt.Width, CursorArt.Height, TextureFormat.RGBA32, true, true) { name = "PcCursorAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave };
                s_Atlas.SetPixelData(CursorArt.Render(), 0);           // level 0 only; Apply(true) builds the smaller levels
                s_Atlas.Apply(true);
                return s_Atlas;
            }
        }

        public PanelCursor(PanelView panel)
        {
            m_Panel = panel;
            m_Go = new GameObject("PcCursor");
            m_Go.transform.SetParent(panel.transform, false);
            m_Mesh = new Mesh { name = "PcCursorQuad" };
            m_Mesh.MarkDynamic();
            m_Mesh.SetVertices(new List<Vector3> { Vector3.zero, Vector3.right, Vector3.one, Vector3.up });
            m_Mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            m_Mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);               // positioned every frame; never culled by a stale bound
            m_Go.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var mr = m_Go.AddComponent<MeshRenderer>();
            m_Material = new Material(Shader.Find("XrSpatial/Cursor")) { name = "PcCursor", hideFlags = HideFlags.DontSave };
            m_Material.SetTexture(MainTexId, Atlas);
            mr.sharedMaterial = m_Material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            m_Go.SetActive(false);
        }

        public void Destroy()
        {
            if (m_Go) Object.Destroy(m_Go);
            if (m_Mesh) Object.Destroy(m_Mesh);
            if (m_Material) Object.Destroy(m_Material);
        }

        public void Hide()
        {
            Visible = false;
            if (m_Go && m_Go.activeSelf) m_Go.SetActive(false);
        }

        /// <summary>Moves the cursor to where the PC says it is, or hides it when it should not be seen on this screen.</summary>
        public void Tick(PcCursorMode mode)
        {
            var panel = m_Panel;
            if (mode == PcCursorMode.Off || !panel.CursorStream(out ushort stream) || (panel.Source != null && panel.Source.Closed) || !panel.Def.visible || !RemoteHost.TryGetCursor(stream, out var c)) { Hide(); return; }
            Sample = c;
            if (!c.Inside || !c.Showing) { Hide(); return; }                          // not over this window (or covered), or the application hid the cursor
            var def = panel.Def;
            var shown = StereoMath.InputRect(def.stereo, def.swapEyes, def.crop);      // the part of the source this panel shows; the same rectangle the controller maps through
            SourcePos = new Vector2(c.u, c.v);
            if (!CursorMath.SourceToPanel(SourcePos, shown, out var uv)) { Hide(); return; }      // outside this crop (or the other eye's half): another panel's cursor
            PanelUv = uv;

            var q = def.corners;
            Vector3 point = QuadMath.UvToWorld(new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y)), q);
            Vector3 right = (q[1] - q[0]).normalized, up = (q[0] - q[3]).normalized;      // corners run TL, TR, BR, BL
            Vector3 normal = Vector3.Cross(q[1] - q[0], q[3] - q[0]).normalized;
            var cam = Camera.main;
            Vector3 eye = cam ? panel.transform.InverseTransformPoint(cam.transform.position) : point - normal * 2f;
            if (Vector3.Dot(normal, eye - point) < 0f) normal = -normal;                // lie on the side the viewer looks at
            float h = CursorMath.Height(Vector3.Distance(eye, point), mode);
            var hot = CursorArt.Hotspot(c.shape);
            Vector3 origin = point + normal * 0.002f - right * (hot.x * h) + up * (hot.y * h);      // the sprite's hotspot sits on the point
            m_Verts[0] = origin - up * h;                    // bottom-left
            m_Verts[1] = origin - up * h + right * h;        // bottom-right
            m_Verts[2] = origin + right * h;                 // top-right
            m_Verts[3] = origin;                             // top-left
            m_Mesh.SetVertices(m_Verts);
            m_Mesh.bounds = new Bounds(point, Vector3.one * (h * 4f + 0.1f));

            byte shape = (byte)CursorArt.CellOf(c.shape);
            if (shape != m_Shape) { m_Shape = shape; m_Material.SetVector(CellId, CursorArt.CellRect(shape)); }
            if (mode != m_Mode) { m_Mode = mode; m_Material.SetFloat(HighId, mode == PcCursorMode.High ? 1f : 0f); }
            bool always = panel.DepthActive;                                           // see the class comment: in 2.5D the cursor must not be hidden by displaced geometry
            if (always != m_ZAlways) { m_ZAlways = always; m_Material.SetFloat(ZTestId, always ? (float)UnityEngine.Rendering.CompareFunction.Always : (float)UnityEngine.Rendering.CompareFunction.LessEqual); }
            Visible = true;
            if (!m_Go.activeSelf) m_Go.SetActive(true);
        }
    }

    /// <summary>Text for the Labs cursor readout: where Windows says the cursor is, where it lands on the panel, and how far that is from where the controller last aimed.</summary>
    public static class CursorReadout
    {
        public static string Build(PanelView panel, SurfaceTool tool)
        {
            if (RemoteHost.PcCursor == PcCursorMode.Off) return "CURSOR  off. Turn PC cursor on (Input section) to see where Windows has the mouse.";
            if (panel == null) return "CURSOR  select a screen (point at it, press the trigger in edit mode).";
            if (!panel.CursorStream(out ushort stream)) return "CURSOR  this screen is not a PC window.";
            if (!RemoteHost.TryGetCursor(stream, out var c)) return "CURSOR  waiting for the PC (no report yet).";
            var sb = new System.Text.StringBuilder();
            sb.Append($"CURSOR  Windows px {c.screenX},{c.screenY}  shape {c.shape}  {(c.Showing ? "shown" : "hidden by the app")}\n");
            if (!c.Inside) { sb.Append("not over this window (or covered)"); return sb.ToString(); }
            var shown = StereoMath.InputRect(panel.Def.stereo, panel.Def.swapEyes, panel.Def.crop);
            var src = new Vector2(c.u, c.v);
            bool onPanel = CursorMath.SourceToPanel(src, shown, out var uv);
            sb.Append($"source {src.x:0.000},{src.y:0.000}  panel {(onPanel ? $"{uv.x:0.000},{uv.y:0.000}" : "outside this crop")}\n");
            if (RemoteHost.LastPointerStream == stream && RemoteHost.NowMs - RemoteHost.LastPointerMs < 4000)
            {
                var asked = RemoteHost.LastPointerSource;
                var d = asked - src;
                sb.Append($"controller asked {asked.x:0.000},{asked.y:0.000}  diff {d.x * panel.Source.Width:0},{d.y * panel.Source.Height:0} px ({d.magnitude * 100f:0.0}%)\n");
            }
            else sb.Append("controller: no recent aim at this window\n");
            sb.Append($"depth {(panel.DepthActive ? "ON " + Mathf.RoundToInt(DepthLab.Strength * 100f) + "%" : "off")}   mode {CursorMath.Name(RemoteHost.PcCursor)}");
            return sb.ToString();
        }

    }
}
