using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;

namespace XrSpatial.Spatial
{
    /// <summary>
    /// The set of sources and screens in the room: owns the live <see cref="ScreenSource"/>s and <see cref="PanelView"/>s, mirrors them into a serialisable
    /// <see cref="Core.Layout"/>, and saves/restores that layout per application. All edits go through here so persistence is always up to date.
    /// </summary>
    public sealed class SpatialWorkspace : MonoBehaviour
    {
        public Transform Stage { get; private set; }
        public Layout Layout { get; private set; } = new Layout();
        public IReadOnlyList<PanelView> Panels => m_Panels;
        public string ActiveSourceId { get; set; }
        public bool AutoSave = true;
        public string LayoutDirectory { get; set; }
        public event Action Changed;
        public event Action<PanelView> PanelAdded, PanelRemoved;

        readonly List<PanelView> m_Panels = new List<PanelView>();
        readonly Dictionary<string, ScreenSource> m_Sources = new Dictionary<string, ScreenSource>();
        float m_SaveAt = -1f;

        public static SpatialWorkspace Create(Transform parent)
        {
            var go = new GameObject("Workspace");
            go.transform.SetParent(parent, false);
            var w = go.AddComponent<SpatialWorkspace>();
            w.Stage = go.transform;
            w.LayoutDirectory = Path.Combine(Application.persistentDataPath, "layouts");
            return w;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>Loads the saved layout for an application (creating an empty one), rebuilding every source and panel.</summary>
        public void LoadLayout(string appKey)
        {
            Clear();
            var l = Layout.Load(LayoutDirectory, appKey) ?? new Layout { appKey = appKey };
            Layout = l;
            foreach (var s in l.sources) EnsureSource(s);
            foreach (var d in l.surfaces) CreatePanel(d);
            ActiveSourceId = l.sources.Count > 0 ? l.sources[0].id : null;
            Changed?.Invoke();
        }

        public void Clear()
        {
            foreach (var p in m_Panels) if (p) Destroy(p.gameObject);
            m_Panels.Clear();
            foreach (var s in m_Sources.Values) s.Dispose();
            m_Sources.Clear();
        }

        public void SaveNow()
        {
            m_SaveAt = -1f;
            try { Layout.Save(LayoutDirectory); }
            catch (Exception e) { Debug.LogWarning("[XrSpatial] could not save layout: " + e.Message); }
        }

        public void MarkDirty()
        {
            if (AutoSave) m_SaveAt = Time.unscaledTime + 1.0f;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ sources

        public ScreenSource GetSource(string id) => id != null && m_Sources.TryGetValue(id, out var s) ? s : null;
        public IEnumerable<SourceDef> Sources => Layout.sources;

        /// <summary>Adds (or replaces, by id) a source definition and starts capturing it.</summary>
        public SourceDef AddSource(SourceDef def)
        {
            if (string.IsNullOrEmpty(def.id)) def.id = Layout.NewId("src");
            var existing = Layout.FindSource(def.id);
            if (existing != null) { Layout.sources.Remove(existing); if (m_Sources.TryGetValue(def.id, out var old)) { old.Dispose(); m_Sources.Remove(def.id); } }
            Layout.sources.Add(def);
            EnsureSource(def);
            ActiveSourceId = def.id;
            // Existing panels of a replaced source pick up the new live source.
            foreach (var p in m_Panels) if (p.Def.sourceId == def.id) p.Rebind(m_Sources[def.id]);
            MarkDirty();
            return def;
        }

        public void RemoveSource(string id)
        {
            foreach (var p in new List<PanelView>(m_Panels)) if (p.Def.sourceId == id) RemoveSurface(p);
            Layout.sources.RemoveAll(s => s.id == id);
            if (m_Sources.TryGetValue(id, out var src)) { src.Dispose(); m_Sources.Remove(id); }
            if (ActiveSourceId == id) ActiveSourceId = Layout.sources.Count > 0 ? Layout.sources[0].id : null;
            MarkDirty();
        }

        ScreenSource EnsureSource(SourceDef def)
        {
            if (m_Sources.TryGetValue(def.id, out var s)) return s;
            s = new ScreenSource(def, () => RequestFor(def));
            m_Sources[def.id] = s;
            return s;
        }

        static CaptureRequest RequestFor(SourceDef def)
        {
            if (def.kind == "monitor") return new CaptureRequest { monitorIndex = Mathf.Max(0, def.monitorIndex) };
            if (string.IsNullOrEmpty(def.processName) && string.IsNullOrEmpty(def.titleContains)) return null;
            return new CaptureRequest { processName = def.processName, titleContains = def.titleContains };
        }

        // ------------------------------------------------------------------ surfaces

        public PanelView AddSurface(string sourceId, Vector3[] corners, Rect? crop = null, string label = null)
        {
            var d = new SurfaceDef { id = Layout.NewId("scr"), sourceId = sourceId, corners = (Vector3[])corners.Clone(), crop = crop ?? new Rect(0, 0, 1, 1), label = label };
            Layout.surfaces.Add(d);
            var p = CreatePanel(d);
            MarkDirty();
            return p;
        }

        PanelView CreatePanel(SurfaceDef d)
        {
            m_Sources.TryGetValue(d.sourceId ?? "", out var src);
            var p = PanelView.Create(Stage, d, src);
            m_Panels.Add(p);
            PanelAdded?.Invoke(p);
            return p;
        }

        public void RemoveSurface(PanelView p)
        {
            if (!p) return;
            Layout.surfaces.Remove(p.Def);
            m_Panels.Remove(p);
            PanelRemoved?.Invoke(p);
            Destroy(p.gameObject);
            MarkDirty();
        }

        /// <summary>Call after changing a panel's corners or crop.</summary>
        public void Touch(PanelView p) { p.Rebuild(); MarkDirty(); }

        public PanelView Find(string surfaceId) => m_Panels.Find(p => p.Def.id == surfaceId);

        // ------------------------------------------------------------------ frame loop

        void Update()
        {
            foreach (var s in m_Sources.Values) s.Tick();
            if (m_SaveAt > 0f && Time.unscaledTime >= m_SaveAt) SaveNow();
        }

        void OnApplicationQuit() { if (AutoSave && Layout != null && Layout.surfaces.Count + Layout.sources.Count > 0) SaveNow(); }

        void OnDestroy() { foreach (var s in m_Sources.Values) s.Dispose(); m_Sources.Clear(); }
    }
}
