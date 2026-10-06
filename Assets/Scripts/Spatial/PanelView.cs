using System.Collections.Generic;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    public enum PanelHighlight { None, Hover, Selected }

    /// <summary>
    /// One screen in space: renders a <see cref="SurfaceDef"/> as a quad textured with (a crop of) its source, plus corner handles and an outline for editing.
    /// The object sits at the origin of the stage root, so its vertices are in tracking space directly.
    /// </summary>
    public sealed class PanelView : MonoBehaviour
    {
        public SurfaceDef Def { get; private set; }
        public ScreenSource Source { get; private set; }
        public PanelHighlight Highlight { get; private set; }

        MeshFilter m_Filter;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;
        Material m_Material;
        Transform[] m_Handles = new Transform[4];
        MeshRenderer[] m_HandleRenderers = new MeshRenderer[4];
        LineRenderer m_Outline;
        Material m_HandleMat, m_LineMat;
        int m_HoverCorner = -1;
        static readonly int MainTex = Shader.PropertyToID("_MainTex"), Crop = Shader.PropertyToID("_Crop"), Opacity = Shader.PropertyToID("_Opacity"),
            ShowEdge = Shader.PropertyToID("_ShowEdge"), Tint = Shader.PropertyToID("_Tint");

        public static PanelView Create(Transform parent, SurfaceDef def, ScreenSource source)
        {
            var go = new GameObject("Panel " + def.id);
            go.transform.SetParent(parent, false);
            var pv = go.AddComponent<PanelView>();
            pv.Build(def, source);
            return pv;
        }

        void Build(SurfaceDef def, ScreenSource source)
        {
            Def = def; Source = source;
            m_Filter = gameObject.AddComponent<MeshFilter>();
            m_Renderer = gameObject.AddComponent<MeshRenderer>();
            m_Mesh = new Mesh { name = "PanelMesh" };
            m_Mesh.MarkDynamic();
            m_Filter.sharedMesh = m_Mesh;
            m_Material = new Material(Materials.Panel) { name = "Panel-" + def.id };
            m_Renderer.sharedMaterial = m_Material;
            m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;
            m_Renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            m_Renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            m_HandleMat = Materials.NewUnlit(new Color(1f, 1f, 1f, 0.95f), true);
            for (int i = 0; i < 4; i++)
            {
                var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                h.name = "Handle" + i;
                Destroy(h.GetComponent<Collider>());
                h.transform.SetParent(transform, false);
                m_Handles[i] = h.transform;
                m_HandleRenderers[i] = h.GetComponent<MeshRenderer>();
                m_HandleRenderers[i].sharedMaterial = m_HandleMat;
                m_HandleRenderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                h.SetActive(false);
            }
            m_LineMat = Materials.NewUnlit(new Color(0.35f, 0.85f, 1f, 0.95f), false);
            var lg = new GameObject("Outline");
            lg.transform.SetParent(transform, false);
            m_Outline = lg.AddComponent<LineRenderer>();
            m_Outline.sharedMaterial = m_LineMat;
            m_Outline.loop = true; m_Outline.positionCount = 4; m_Outline.useWorldSpace = false; m_Outline.widthMultiplier = 0.004f;
            m_Outline.numCornerVertices = 2; m_Outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Outline.enabled = false;
            Rebuild();
        }

        /// <summary>Re-reads the corners/crop from <see cref="Def"/> (call after changing them).</summary>
        public void Rebuild()
        {
            var q = Def.corners;
            var uvq = QuadMath.CornerUvq(q);
            m_Mesh.Clear();
            m_Mesh.SetVertices(new List<Vector3> { q[0], q[1], q[2], q[3] });
            m_Mesh.SetUVs(0, new List<Vector3> { uvq[0], uvq[1], uvq[2], uvq[3] });
            m_Mesh.SetTriangles(QuadMath.Triangles, 0);
            m_Mesh.RecalculateBounds();
            m_Mesh.bounds = new Bounds(m_Mesh.bounds.center, m_Mesh.bounds.size + Vector3.one * 0.1f);
            m_Material.SetVector(Crop, new Vector4(Def.crop.x, Def.crop.y, Def.crop.width, Def.crop.height));
            m_Material.SetFloat(Opacity, Def.opacity);
            m_Renderer.enabled = Def.visible;
            for (int i = 0; i < 4; i++) m_Outline.SetPosition(i, q[i]);
            LayoutHandles();
        }

        /// <summary>Points this panel at a new live source (after the source was replaced).</summary>
        public void Rebind(ScreenSource source) { Source = source; }

        void LateUpdate()
        {
            if (Source != null && Source.View) m_Material.SetTexture(MainTex, Source.View);
            else m_Material.SetTexture(MainTex, Texture2D.blackTexture);
        }

        public void SetHighlight(PanelHighlight h, int hoverCorner = -1)
        {
            Highlight = h; m_HoverCorner = hoverCorner;
            m_Material.SetFloat(ShowEdge, h == PanelHighlight.None ? 0f : 1f);
            m_Outline.enabled = h != PanelHighlight.None && Def.visible;
            LayoutHandles();
        }

        void LayoutHandles()
        {
            bool show = Highlight != PanelHighlight.None && Def.visible;
            float size = Mathf.Clamp(QuadMath.Size(Def.corners).magnitude * 0.018f, 0.018f, 0.05f);
            for (int i = 0; i < 4; i++)
            {
                m_Handles[i].gameObject.SetActive(show);
                if (!show) continue;
                m_Handles[i].localPosition = Def.corners[i];
                bool hot = i == m_HoverCorner;
                m_Handles[i].localScale = Vector3.one * size * (hot ? 1.8f : 1f);
                m_HandleRenderers[i].sharedMaterial = hot ? Materials.HandleHot : (i == 0 ? Materials.HandleFirst : m_HandleMat);
            }
        }

        public bool Raycast(Ray ray, out Vector2 uv, out float distance)
        {
            uv = default; distance = 0f;
            return Def.visible && QuadMath.RayToUv(ray, Def.corners, out uv, out distance);
        }

        /// <summary>Panel UV -> position in the source image (0..1, origin top-left), taking the crop into account.</summary>
        public Vector2 UvToSource(Vector2 uv) => QuadMath.PanelUvToSource(uv, Def.crop);

        void OnDestroy()
        {
            if (m_Mesh) Destroy(m_Mesh);
            if (m_Material) Destroy(m_Material);
            if (m_HandleMat) Destroy(m_HandleMat);
            if (m_LineMat) Destroy(m_LineMat);
        }
    }

    /// <summary>Shared materials (loaded from Resources/Shaders so they survive shader stripping in player builds).</summary>
    public static class Materials
    {
        static Material s_Panel, s_HandleHot, s_HandleFirst;
        public static Shader PanelShader => Shader.Find("XrSpatial/Panel");
        public static Shader UnlitShader => Shader.Find("XrSpatial/Unlit");
        public static Material Panel => s_Panel ? s_Panel : s_Panel = new Material(PanelShader) { name = "PanelBase", hideFlags = HideFlags.DontSave };
        public static Material HandleHot => s_HandleHot ? s_HandleHot : s_HandleHot = NewUnlit(new Color(1f, 0.85f, 0.2f, 1f), true);
        public static Material HandleFirst => s_HandleFirst ? s_HandleFirst : s_HandleFirst = NewUnlit(new Color(0.4f, 1f, 0.5f, 0.95f), true);

        public static Material NewUnlit(Color c, bool seeThrough)
        {
            var m = new Material(UnlitShader) { name = "Unlit", hideFlags = HideFlags.DontSave };
            m.SetColor("_Color", c);
            m.SetFloat("_ZTest", seeThrough ? (float)UnityEngine.Rendering.CompareFunction.Always : (float)UnityEngine.Rendering.CompareFunction.LessEqual);
            m.renderQueue = seeThrough ? 4000 : 3000;
            return m;
        }
    }
}
