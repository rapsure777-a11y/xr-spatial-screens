using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace XrSpatial.Core
{
    /// <summary>How a captured source is found again next session (a window is identified by its process and title, not by a handle).</summary>
    [Serializable]
    public class SourceDef
    {
        public string id;
        /// <summary>"window", "monitor" or "pattern" (a generated test image).</summary>
        public string kind = "window";
        public string processName;
        public string titleContains;
        public int monitorIndex;
        public string label;
    }

    /// <summary>
    /// One screen in space: four corners (tracking-space metres, order TL TR BR BL) showing a rectangle of a source. A crop panel is simply another
    /// surface that points at the same source with a smaller <see cref="crop"/>.
    /// </summary>
    [Serializable]
    public class SurfaceDef
    {
        public string id;
        public string sourceId;
        public string label;
        public Vector3[] corners = new Vector3[4];
        /// <summary>Normalised region of the source (origin top-left, y down): x, y, width, height. The whole source is (0, 0, 1, 1).</summary>
        public Rect crop = new Rect(0f, 0f, 1f, 1f);
        public bool visible = true;
        /// <summary>Forward pointer clicks on this panel to the source application.</summary>
        public bool interactive = true;
        [Range(0.05f, 1f)] public float opacity = 1f;

        public SurfaceDef Clone() => new SurfaceDef
        {
            id = id, sourceId = sourceId, label = label, corners = (Vector3[])corners.Clone(), crop = crop,
            visible = visible, interactive = interactive, opacity = opacity,
        };
    }

    /// <summary>Everything needed to restore a workspace for one application: its sources and its surfaces.</summary>
    [Serializable]
    public class Layout
    {
        public const int CurrentVersion = 1;
        public int version = CurrentVersion;
        public string appKey = "default";
        public List<SourceDef> sources = new List<SourceDef>();
        public List<SurfaceDef> surfaces = new List<SurfaceDef>();

        public SourceDef FindSource(string id) => sources.Find(s => s.id == id);
        public SurfaceDef FindSurface(string id) => surfaces.Find(s => s.id == id);

        public static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // ------------------------------------------------------------------ persistence

        /// <summary>File-name-safe key for an application ("Hades.exe" or a window title).</summary>
        public static string SafeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "default";
            var sb = new System.Text.StringBuilder();
            foreach (char c in key.Trim().ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            string s = sb.ToString();
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        public static string PathFor(string directory, string appKey) => Path.Combine(directory, SafeKey(appKey) + ".layout.json");

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static Layout FromJson(string json)
        {
            var l = JsonUtility.FromJson<Layout>(json);
            if (l == null) return null;
            l.sources ??= new List<SourceDef>();
            l.surfaces ??= new List<SurfaceDef>();
            l.surfaces.RemoveAll(s => s == null || s.corners == null || s.corners.Length != 4 || !QuadMath.IsValidQuad(s.corners, 0.005f));
            return l;
        }

        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, appKey);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());          // write beside, then swap, so a crash never leaves a half-written layout
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static Layout Load(string directory, string appKey)
        {
            string path = PathFor(directory, appKey);
            if (!File.Exists(path)) return null;
            try { var l = FromJson(File.ReadAllText(path)); if (l != null) l.appKey = appKey; return l; }
            catch (Exception e) { Debug.LogWarning($"[XrSpatial] Could not read layout {path}: {e.Message}"); return null; }
        }
    }
}
