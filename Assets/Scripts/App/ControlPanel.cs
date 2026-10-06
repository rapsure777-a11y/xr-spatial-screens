using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using XrSpatial.Capture;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// The desktop-side control panel (IMGUI, shown in the app's flat window / the headset mirror): pick the window to capture, manage sources and screens, save and
    /// load layouts. Selecting a window here is the one thing that is awkward inside VR, so it lives on the desktop.
    /// </summary>
    public sealed class ControlPanel : MonoBehaviour
    {
        public SpatialApp App;
        public bool Visible = true;
        Rect m_Rect = new Rect(12, 12, 460, 640);
        Vector2 m_WinScroll, m_ListScroll;
        List<CapturableWindow> m_Windows = new List<CapturableWindow>();
        bool m_Refreshing;
        string m_Filter = "";
        string m_Note = "";

        void Start() => Refresh();

        public bool ContainsScreenPoint(Vector2 p) => m_Rect.Contains(new Vector2(p.x, Screen.height - p.y));

        public void Refresh()
        {
            if (m_Refreshing) return;
            m_Refreshing = true;
            CaptureCatalog.ListAsync().ContinueWith(t => { m_Windows = t.Result; m_Refreshing = false; });
        }

        void OnGUI()
        {
            if (!Visible || App == null || App.Workspace == null) return;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F1) { Visible = false; return; }
            m_Rect = GUI.Window(7311, m_Rect, DrawWindow, "XR Spatial Screens");
        }

        void DrawWindow(int id)
        {
            var ws = App.Workspace; var tool = App.Tool;
            GUILayout.Label($"Layout: {ws.Layout.appKey}    ({(App.XrActive ? "headset active" : "no headset: desktop pointer")})");
            GUILayout.Label("Status: " + (string.IsNullOrEmpty(tool.Message) ? tool.Mode.ToString() : tool.Message));

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("New screen (4 points)")) tool.BeginPlace();
            if (GUILayout.Button("Quick screen")) App.AddQuickScreen();
            if (GUILayout.Button("Crop")) tool.BeginCrop();
            if (GUILayout.Button("Delete")) tool.DeleteSelected();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(tool.InteractMode, "Interact (forward clicks)") != tool.InteractMode) tool.ToggleInteract();
            if (GUILayout.Toggle(tool.TouchPlacement, "Touch placement") != tool.TouchPlacement) tool.ToggleTouch();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Capture a window");
            GUILayout.BeginHorizontal();
            m_Filter = GUILayout.TextField(m_Filter, GUILayout.Width(220));
            if (GUILayout.Button(m_Refreshing ? "..." : "Refresh list")) Refresh();
            if (GUILayout.Button("Test pattern")) App.AddPattern();
            if (GUILayout.Button("Monitor 0")) App.AddMonitor(0);
            GUILayout.EndHorizontal();
            m_WinScroll = GUILayout.BeginScrollView(m_WinScroll, GUILayout.Height(190));
            foreach (var w in m_Windows.Where(w => m_Filter.Length == 0 || w.title.IndexOf(m_Filter, System.StringComparison.OrdinalIgnoreCase) >= 0 || w.process.IndexOf(m_Filter, System.StringComparison.OrdinalIgnoreCase) >= 0))
                if (GUILayout.Button($"{w.process}  |  {Short(w.title, 44)}  |  {w.w}x{w.h}", GUI.skin.label)) { App.UseWindow(w); }
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            GUILayout.Label($"Sources ({ws.Layout.sources.Count})");
            foreach (var s in ws.Layout.sources.ToList())
            {
                var live = ws.GetSource(s.id);
                GUILayout.BeginHorizontal();
                bool active = s.id == ws.ActiveSourceId;
                if (GUILayout.Toggle(active, $"{(string.IsNullOrEmpty(s.label) ? s.id : s.label)}: {live?.Status}") && !active) ws.ActiveSourceId = s.id;
                if (GUILayout.Button("x", GUILayout.Width(26))) ws.RemoveSource(s.id);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label($"Screens ({ws.Panels.Count})");
            m_ListScroll = GUILayout.BeginScrollView(m_ListScroll, GUILayout.Height(110));
            foreach (var p in ws.Panels.ToList())
            {
                GUILayout.BeginHorizontal();
                bool sel = tool.Selected == p;
                string desc = $"{p.Def.id}  {(p.Def.crop.width < 0.999f ? "crop " : "")}{p.Def.label}";
                if (GUILayout.Toggle(sel, desc) && !sel) tool.Select(p);
                p.Def.visible = GUILayout.Toggle(p.Def.visible, "show", GUILayout.Width(52));
                if (GUILayout.Button("x", GUILayout.Width(26))) { ws.RemoveSurface(p); }
                GUILayout.EndHorizontal();
                p.Rebuild();
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save layout")) { ws.SaveNow(); m_Note = "saved " + Layout.PathFor(ws.LayoutDirectory, ws.Layout.appKey); }
            if (GUILayout.Button("Reload layout")) { ws.LoadLayout(ws.Layout.appKey); m_Note = "reloaded"; }
            if (GUILayout.Button("Clear all")) { ws.Clear(); ws.Layout.sources.Clear(); ws.Layout.surfaces.Clear(); ws.MarkDirty(); }
            GUILayout.EndHorizontal();
            GUILayout.Label(m_Note, GUI.skin.label);
            GUILayout.Label("Desktop pointer: mouse = laser, left = trigger, middle/G = grip, arrows = stick, Tab = palette, hold right mouse + WASD to fly. F1 hides this panel.");
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
