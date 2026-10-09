using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using XrSpatial.Capture;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// The tool palette: a small world-space panel held over the non-pointing wrist (or floating beside the head without controllers). The pointing hand's laser
    /// highlights a button and the trigger presses it. UGUI is used for layout/text only; hit-testing is done by intersecting the laser with the canvas plane, so no
    /// EventSystem or physics raycaster is needed (and nothing in the scene can steal the click). Two pages: the tools (in labelled sections), and a window picker
    /// (so a window can be chosen to capture without leaving VR). All colours and sizes live in <see cref="Style"/>.
    /// </summary>
    public sealed class PalettePanel : MonoBehaviour, IUiLayer
    {
        public SurfaceTool Tool;
        public SpatialWorkspace Workspace;
        public IPointerSource Pointer;
        public Action OnAddPattern;
        public Action<CapturableWindow> OnPickWindow;
        public Action<int> OnPickMonitor;
        public Action OnToggleKeyboard;
        public bool Visible { get; private set; } = true;

        const float W = 560f, Scale = 0.00055f;
        float H = 880f;                                              // 0.31 m x 0.48 m (the old 820 high panel needed about 850 for its 19 buttons, the last one hung below it); taller when the Labs row is shown
        const int WindowsPerPage = 8;
        Canvas m_Canvas;
        RectTransform m_Rect;
        Text m_Mode, m_Status;
        readonly List<Btn> m_Main = new List<Btn>();
        readonly List<Btn> m_Windows = new List<Btn>();
        readonly List<Btn> m_Labs = new List<Btn>();
        readonly List<GameObject> m_LabsDecor = new List<GameObject>();
        Btn m_Drag;                                                                 // the slider being dragged (trigger held)
        bool m_LabsEnabled;                                                         // the Labs row is only built when depthlab.flag exists in the app data folder
        public Action OnAddDepthTest, OnRemoveDepthTest;
        readonly List<GameObject> m_MainDecor = new List<GameObject>();            // section headers and hairlines: shown with the main page only
        readonly List<(Text text, Func<string> label)> m_Headers = new List<(Text, Func<string>)>();
        List<Btn> Current => m_Page == Page.Main ? m_Main : m_Page == Page.Windows ? m_Windows : m_Labs;
        Btn m_Hover;
        Font m_Font;
        enum Page { Main, Windows, Labs }
        Page m_Page = Page.Main;
        List<CapturableWindow> m_WindowList = new List<CapturableWindow>();
        int m_WindowPage;
        bool m_Refreshing;
        bool m_Placed;
        volatile bool m_RebuildWindows;
        static Sprite s_Round;

        /// <summary>Every colour, size and gap of the palette in one place, so the look can be tuned without touching the layout code.</summary>
        static class Style
        {
            public static readonly Color Back = new Color(0.045f, 0.06f, 0.08f, 0.93f);
            public static readonly Color Button = new Color(0.13f, 0.17f, 0.22f, 1f);
            public static readonly Color ButtonHover = new Color(0.20f, 0.46f, 0.68f, 1f);
            public static readonly Color ButtonOn = new Color(0.08f, 0.34f, 0.40f, 1f);
            public static readonly Color ButtonPressed = new Color(0.09f, 0.26f, 0.42f, 1f);
            public static readonly Color OnBar = new Color(0.40f, 0.90f, 0.95f, 1f);
            public static readonly Color Text = new Color(0.93f, 0.96f, 1f, 1f);
            public static readonly Color TextDim = new Color(0.62f, 0.72f, 0.80f, 1f);
            public static readonly Color TextFaint = new Color(0.50f, 0.58f, 0.65f, 1f);
            public static readonly Color Accent = new Color(0.45f, 0.85f, 0.95f, 1f);
            public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.12f);
            public const float ButtonH = 58f, RowGap = 5f, GroupGap = 4f, HeaderH = 24f, HeaderGap = 2f, Margin = 20f, ColGap = 20f, PressSeconds = 0.15f, HitPad = 2f;
        }

        sealed class Btn
        {
            public RectTransform rect; public Image bg, bar; public Text label, detail; public Func<string> text, detailText; public Action action; public Func<bool> active, dim;
            public float pressedUntil;
            public Image fill; public Func<float> value; public Action<float> setValue;      // sliders
        }

        public static PalettePanel Create(Transform parent, SurfaceTool tool, SpatialWorkspace ws, IPointerSource pointer)
        {
            var go = new GameObject("Palette");
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<PalettePanel>();
            p.Tool = tool; p.Workspace = ws; p.Pointer = pointer;
            p.Build();
            return p;
        }

        void Build()
        {
            m_LabsEnabled = System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "depthlab.flag"));
            if (m_LabsEnabled) H += 95f;
            m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            m_Rect = GetComponent<RectTransform>();
            m_Rect.sizeDelta = new Vector2(W, H);
            m_Rect.localScale = Vector3.one * Scale;
            Rounded(Img("Back", m_Rect, new Vector2(0, 0), new Vector2(W, H), Style.Back));
            m_Mode = Txt("Mode", m_Rect, new Vector2(Style.Margin, H - 12 - 28), new Vector2(W - 2 * Style.Margin, 28), 24, TextAnchor.MiddleLeft, Style.Accent);
            m_Mode.fontStyle = FontStyle.Bold;
            m_Status = Txt("Status", m_Rect, new Vector2(Style.Margin, H - 12 - 28 - 66), new Vector2(W - 2 * Style.Margin, 66), 24, TextAnchor.UpperLeft, Style.Text);
            Txt("Brand", m_Rect, new Vector2(Style.Margin, 6), new Vector2(W - 2 * Style.Margin, 24), 18, TextAnchor.MiddleCenter, Style.TextFaint).text = "SPATIAL CROP  ·  GAMEBREAK LABS";

            // Sections, top to bottom: what to show (source), how it sits (screen), which part (crop), how to use it (input), how it arrives (stream and sound), housekeeping (utility).
            float y = H - 12 - 28 - 66 - 4;
            int col = 0, cols = 2;
            bool rowOpen = false;
            void Row(int n) { if (rowOpen) y -= Style.ButtonH + Style.RowGap; rowOpen = false; col = 0; cols = n; }
            void Section(string title, Func<string> live = null)
            {
                Row(cols);
                if (m_Main.Count > 0) y -= Style.GroupGap;
                var t = Txt("Header", m_Rect, new Vector2(Style.Margin + 2, y - Style.HeaderH), new Vector2(W - 2 * Style.Margin, Style.HeaderH), 22, TextAnchor.LowerLeft, Style.TextDim);
                t.fontStyle = FontStyle.Bold; t.text = title;
                var line = Img("Hairline", m_Rect, new Vector2(Style.Margin, y - Style.HeaderH), new Vector2(W - 2 * Style.Margin, 2), Style.Hairline);
                m_MainDecor.Add(t.gameObject); m_MainDecor.Add(line.gameObject);
                m_Headers.Add((t, live ?? (() => title)));
                y -= Style.HeaderH + Style.HeaderGap;
            }
            void Add(Func<string> text, Action action, Func<bool> active = null)
            {
                if (col >= cols) Row(cols);
                float w = (W - 2 * Style.Margin - (cols - 1) * Style.ColGap) / cols;
                float x = Style.Margin + col * (w + Style.ColGap);
                m_Main.Add(MakeBtn(new Vector2(x, y - Style.ButtonH), new Vector2(w, Style.ButtonH), text, action, active));
                rowOpen = true; col++;
            }

            Section("SOURCE"); Row(2);
            Add(() => "Capture window...", ShowWindows);
            Add(() => "Next source", Tool.CycleSource);
            Add(() => "Test pattern", () => OnAddPattern?.Invoke());
            if (RemoteHost.Active) Add(() => "Reconnect screen", () =>
            {
                // a frozen or blocky screen: close its stream and open a fresh one (the selected screen, or the active source)
                var src = Tool.Selected != null ? Tool.Selected.Source : Workspace.GetSource(Workspace.ActiveSourceId);
                if (src == null) { Tool.Say("Select a screen first (point at it and press the trigger in edit mode).", 4f); return; }
                src.Restart(); Tool.Say("Reconnecting " + (src.Def.label ?? src.Def.id), 3f);
            });

            Section("SCREEN"); Row(2);
            Add(() => Tool.Mode == ToolMode.Place ? $"Placing {Tool.PlacedCount}/4" : "New screen", () => { if (Tool.Mode == ToolMode.Place) Tool.SetIdle(); else Tool.BeginPlace(); }, () => Tool.Mode == ToolMode.Place);
            Add(() => "Delete screen", Tool.DeleteSelected);
            Add(() => "Turn picture", Tool.RotateSelectedPicture);
            Add(() => "Fit picture", Tool.FitSelectedAspect);

            Section("CROP"); Row(2);
            Add(() => Tool.Mode == ToolMode.Crop ? "Cropping..." : "Crop", () => { if (Tool.Mode == ToolMode.Crop) Tool.SetIdle(); else Tool.BeginCrop(); }, () => Tool.Mode == ToolMode.Crop);
            Add(() => "Reset crop", Tool.ResetSelectedCrop);

            Section("INPUT"); Row(2);
            Add(() => Tool.InteractMode ? "Interact: ON" : "Interact: off", Tool.ToggleInteract, () => Tool.InteractMode);
            Add(() => Tool.TouchPlacement ? "Points: touch" : "Points: laser", Tool.ToggleTouch);

            if (RemoteHost.Active)
            {
                Section("STREAM AND SOUND", () => $"STREAM AND SOUND  ·  volume {Mathf.RoundToInt(RemoteAudio.Gain * 100f)}%"); Row(2);
                Add(() => VideoMode.Hevc ? "Video: HEVC" : "Video: JPEG", () =>
                {
                    // switch every screen between the proven JPEG stream and hardware-encoded HEVC (each screen's stream is reopened)
                    VideoMode.Hevc = !VideoMode.Hevc;
                    foreach (var d in new List<SourceDef>(Workspace.Sources)) Workspace.GetSource(d.id)?.Restart();
                    Tool.Say(VideoMode.Hevc ? "Video: HEVC (hardware encode on the PC)" : "Video: JPEG", 3f);
                }, () => VideoMode.Hevc);
                Add(() => RemoteHost.SoundEnabled ? "PC sound: ON" : "PC sound: off", () => RemoteHost.SetSound(!RemoteHost.SoundEnabled), () => RemoteHost.SoundEnabled);
                Add(() => "Volume -", () => RemoteAudio.StepGain(-1));
                Add(() => "Volume +", () => RemoteAudio.StepGain(1));
            }

            Section("UTILITY"); Row(3);
            Add(() => "Save layout", () => { Workspace.SaveNow(); Tool.Say("Layout saved", 2f); });
            Add(() => "Keyboard", () => OnToggleKeyboard?.Invoke());
            Add(() => "Hide palette", () => SetVisible(false));

            if (m_LabsEnabled) { Section("LABS  ·  experimental"); Row(2); Add(() => DepthLab.Enabled ? "Depth Lab: ON" : "Depth Lab...", ShowLabs, () => DepthLab.Enabled); }
        }

        Btn MakeBtn(Vector2 pos, Vector2 size, Func<string> text, Action action, Func<bool> active, Func<string> detail = null, Func<bool> dim = null)
        {
            var b = new Btn { text = text, action = action, active = active, detailText = detail, dim = dim };
            b.rect = Rounded(Img("Btn", m_Rect, pos, size, Style.Button, out b.bg)).rectTransform;
            // the "on" marker: a bar down the left edge, so a toggle reads as on from its shape and not only from its colour
            b.bar = Rounded(Img("OnBar", b.rect, new Vector2(8, 10), new Vector2(8, size.y - 20), Style.OnBar));
            b.bar.enabled = false;
            bool wide = detail != null;
            b.label = Txt("Label", b.rect, new Vector2(wide ? 24 : 12, 0), new Vector2(wide ? size.x * 0.64f - 24 : size.x - 24, size.y), wide ? 24 : 26, wide ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, Style.Text);
            if (wide) { b.label.resizeTextForBestFit = true; b.label.resizeTextMinSize = 16; b.label.resizeTextMaxSize = 24; }
            if (wide) b.detail = Txt("Detail", b.rect, new Vector2(size.x * 0.64f, 0), new Vector2(size.x * 0.36f - 16, size.y), 20, TextAnchor.MiddleRight, Style.TextDim);
            return b;
        }

        // ------------------------------------------------------------------ window picker page

        void ShowWindows()
        {
            m_Page = Page.Windows; m_WindowPage = 0;
            foreach (var b in m_Main) b.rect.gameObject.SetActive(false);
            foreach (var g in m_MainDecor) g.SetActive(false);
            RefreshWindows();
        }

        void ShowMain()
        {
            m_Page = Page.Main; m_Drag = null;
            foreach (var b in m_Windows) Destroy(b.rect.gameObject);
            m_Windows.Clear();
            foreach (var b in m_Labs) Destroy(b.rect.gameObject);
            m_Labs.Clear();
            foreach (var g in m_LabsDecor) Destroy(g);
            m_LabsDecor.Clear();
            foreach (var b in m_Main) b.rect.gameObject.SetActive(true);
            foreach (var g in m_MainDecor) g.SetActive(true);
        }

        // ------------------------------------------------------------------ Depth Lab page (experimental, only reachable when depthlab.flag exists)

        void ShowLabs()
        {
            m_Page = Page.Labs;
            foreach (var b in m_Main) b.rect.gameObject.SetActive(false);
            foreach (var g in m_MainDecor) g.SetActive(false);
            float y = H - 12 - 28 - 66 - 4;
            float full = W - 2 * Style.Margin;
            void Note(string text, float height)
            {
                var t = Txt("Note", m_Rect, new Vector2(Style.Margin + 2, y - height), new Vector2(full - 4, height), 20, TextAnchor.UpperLeft, Style.TextDim);
                t.text = text; m_LabsDecor.Add(t.gameObject); y -= height + 6;
            }
            var head = Txt("Header", m_Rect, new Vector2(Style.Margin + 2, y - Style.HeaderH), new Vector2(full, Style.HeaderH), 22, TextAnchor.LowerLeft, Style.TextDim);
            head.fontStyle = FontStyle.Bold; head.text = "DEPTH LAB  ·  2.5D panels, off by default"; m_LabsDecor.Add(head.gameObject);
            m_LabsDecor.Add(Img("Hairline", m_Rect, new Vector2(Style.Margin, y - Style.HeaderH), new Vector2(full, 2), Style.Hairline).gameObject);
            y -= Style.HeaderH + 8;

            m_Labs.Add(MakeBtn(new Vector2(Style.Margin, y - 66), new Vector2(full, 66), () => DepthLab.Enabled ? "Depth: ON  (tap for flat)" : "Depth: off  (flat, normal)", () => DepthLab.SetEnabled(!DepthLab.Enabled), () => DepthLab.Enabled));
            y -= 66 + 14;
            m_Labs.Add(MakeSlider(new Vector2(Style.Margin, y - 66), new Vector2(full, 66), () => $"Strength  {Mathf.RoundToInt(DepthLab.Strength * 100f)}%", () => DepthLab.Strength, DepthLab.SetStrength));
            y -= 66 + 4;
            Note("0% flat  ·  15 to 30% subtle  ·  100% exaggerated", 28);
            y -= 6;
            m_Labs.Add(MakeSlider(new Vector2(Style.Margin, y - 66), new Vector2(full, 66), () => $"Focus  {Mathf.RoundToInt(DepthLab.Focus * 100f)}%", () => DepthLab.Focus, DepthLab.SetFocus));
            y -= 66 + 4;
            Note("Which depth stays on the panel. Low: picture comes towards you. High: it sits behind a window.", 52);
            m_Labs.Add(MakeSlider(new Vector2(Style.Margin, y - 66), new Vector2(full, 66), () => $"Pop  {Mathf.RoundToInt(DepthLab.Pop * 100f)}%", () => DepthLab.Pop, DepthLab.SetPop));
            y -= 66 + 4;
            Note("Pulls near objects away from the background. 0% = depth as the model gives it.", 28);
            y -= 10;
            float half = (full - Style.ColGap) / 2f;
            m_Labs.Add(MakeBtn(new Vector2(Style.Margin, y - Style.ButtonH), new Vector2(half, Style.ButtonH), () => "Add depth test", () => OnAddDepthTest?.Invoke(), null));
            m_Labs.Add(MakeBtn(new Vector2(Style.Margin + half + Style.ColGap, y - Style.ButtonH), new Vector2(half, Style.ButtonH), () => "Remove test", () => OnRemoveDepthTest?.Invoke(), null));
            y -= Style.ButtonH + 14;
            m_Labs.Add(MakeBtn(new Vector2(Style.Margin, y - Style.ButtonH), new Vector2(full, Style.ButtonH), () => "Real windows: " + DepthProfiles.Name(DepthProfiles.Live), DepthProfiles.Next, () => DepthProfiles.Live != DepthProfiles.Profile.Off));
            y -= Style.ButtonH + 6;
            Note("Real windows only get a simple bend for now (no per-object depth yet). Turn Depth on above to see it.", 52);
            Note("The laser still points at the flat panel, so at strong depth the picture can look a little offset from it.", 76);
            m_Labs.Add(MakeBtn(new Vector2(Style.Margin, 38), new Vector2(full, 70), () => "Back", ShowMain, null));
        }

        Btn MakeSlider(Vector2 pos, Vector2 size, Func<string> text, Func<float> get, Action<float> set)
        {
            var b = MakeBtn(pos, size, text, null, null);
            b.value = get; b.setValue = set;
            b.fill = Rounded(Img("Fill", b.rect, Vector2.zero, size, new Color(0.10f, 0.42f, 0.50f, 1f)));
            b.fill.transform.SetSiblingIndex(0);                                                  // under the label and the on-bar
            return b;
        }

        void RefreshWindows()
        {
            if (!m_Refreshing)
            {
                m_Refreshing = true;
                CaptureCatalog.ListAsync().ContinueWith(t => { m_WindowList = Sorted(t.Result); m_Refreshing = false; m_RebuildWindows = true; });
            }
            m_RebuildWindows = true;
        }

        void BuildWindowButtons()
        {
            m_RebuildWindows = false;
            foreach (var b in m_Windows) Destroy(b.rect.gameObject);
            m_Windows.Clear();
            float bh = Style.ButtonH, gap = Style.RowGap, y = H - 12 - 28 - 66 - 4 - bh;
            int first = m_WindowPage * WindowsPerPage;
            for (int i = 0; i < WindowsPerPage; i++)
            {
                int idx = first + i;
                if (idx >= m_WindowList.Count) break;
                var w = m_WindowList[idx];
                string tag = w.state == "minimized" ? $"min  {w.w}x{w.h}" : w.OtherDesktop ? "other desktop" : $"{w.w}x{w.h}";
                string label = $"{w.process}: {Short(w.title, 20)}";
                m_Windows.Add(MakeBtn(new Vector2(Style.Margin, y), new Vector2(W - 2 * Style.Margin, bh), () => label, () =>
                {
                    if (w.OtherDesktop) { Tool.Say("That window is on another virtual desktop. Move it to this desktop on the PC first.", 6f); return; }
                    OnPickWindow?.Invoke(w); Tool.Say("Capturing " + w.process, 3f); ShowMain();
                }, null, () => tag, () => w.OtherDesktop));
                y -= bh + gap;
            }
            if (m_WindowList.Count == 0) m_Windows.Add(MakeBtn(new Vector2(Style.Margin, y), new Vector2(W - 2 * Style.Margin, bh), () => m_Refreshing ? "Looking for windows..." : "No windows found (tap to retry)", RefreshWindows, null));
            float by = 38f, nh = 80f, nw = (W - 2 * Style.Margin - 3 * Style.ColGap) / 4f;
            int pages = Mathf.Max(1, Mathf.CeilToInt(m_WindowList.Count / (float)WindowsPerPage));
            m_Windows.Add(MakeBtn(new Vector2(Style.Margin, by), new Vector2(nw, nh), () => "Back", ShowMain, null));
            m_Windows.Add(MakeBtn(new Vector2(Style.Margin + (nw + Style.ColGap), by), new Vector2(nw, nh), () => "Refresh", RefreshWindows, null));
            m_Windows.Add(MakeBtn(new Vector2(Style.Margin + 2 * (nw + Style.ColGap), by), new Vector2(nw, nh), () => "Monitor", () => { OnPickMonitor?.Invoke(0); ShowMain(); }, null));
            m_Windows.Add(MakeBtn(new Vector2(Style.Margin + 3 * (nw + Style.ColGap), by), new Vector2(nw, nh), () => $"Page {m_WindowPage + 1}/{pages}", () => { m_WindowPage = (m_WindowPage + 1) % pages; m_RebuildWindows = true; }, null));
        }

        /// <summary>Normal windows first, then minimized ones (the PC restores them when picked), then windows on other virtual desktops; by program name within each group.</summary>
        static List<CapturableWindow> Sorted(List<CapturableWindow> list)
        {
            int Rank(CapturableWindow w) => w.state == "minimized" ? 1 : w.OtherDesktop ? 2 : 0;
            var s = new List<CapturableWindow>(list);
            s.Sort((a, b) => { int r = Rank(a).CompareTo(Rank(b)); return r != 0 ? r : string.Compare(a.process, b.process, StringComparison.OrdinalIgnoreCase); });
            return s;
        }
        static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "...";

        // ------------------------------------------------------------------ building blocks

        Image Img(string name, RectTransform parent, Vector2 pos, Vector2 size, Color c) => Img(name, parent, pos, size, c, out _);

        Image Img(string name, RectTransform parent, Vector2 pos, Vector2 size, Color c, out Image img)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            img = go.GetComponent<Image>();
            img.color = c; img.raycastTarget = false;
            return img;
        }

        Text Txt(string name, RectTransform parent, Vector2 pos, Vector2 size, int fontSize, TextAnchor anchor, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = Vector2.zero; rt.pivot = Vector2.zero;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.GetComponent<Text>();
            t.font = m_Font; t.fontSize = fontSize; t.alignment = anchor; t.color = c; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        /// <summary>Gives an image softly rounded corners (a small generated 9-slice sprite shared by every rectangle in the palette).</summary>
        static Image Rounded(Image img)
        {
            if (!s_Round)
            {
                const int n = 32, r = 10;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                for (int py = 0; py < n; py++)
                    for (int px = 0; px < n; px++)
                    {
                        float cx = px + 0.5f, cy = py + 0.5f;
                        float dx = Mathf.Max(r - cx, cx - (n - r), 0f), dy = Mathf.Max(r - cy, cy - (n - r), 0f);
                        tex.SetPixel(px, py, new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f)));
                    }
                tex.Apply();
                s_Round = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
                s_Round.hideFlags = HideFlags.HideAndDontSave;
            }
            img.sprite = s_Round; img.type = Image.Type.Sliced;
            return img;
        }

        public void SetVisible(bool v) { Visible = v; gameObject.SetActive(v); }
        public void Toggle() => SetVisible(!Visible);

        // ------------------------------------------------------------------ frame

        void LateUpdate()
        {
            if (!Visible) return;
            Place();
            if (m_Page == Page.Windows && m_RebuildWindows) BuildWindowButtons();
            string mode = m_Page == Page.Windows ? "CHOOSE A WINDOW TO CAPTURE" : m_Page == Page.Labs ? "DEPTH LAB (EXPERIMENTAL)" : Tool.Mode == ToolMode.Place ? $"PLACE: point {Tool.PlacedCount + 1} of 4" : Tool.Mode == ToolMode.Crop ? "CROP: drag a box on a screen" : Tool.InteractMode ? "INTERACT" : "EDIT";
            var src = Workspace.GetSource(Workspace.ActiveSourceId);
            var def = Workspace.Layout.FindSource(Workspace.ActiveSourceId);
            string srcLine = def != null ? $"{(string.IsNullOrEmpty(def.label) ? def.id : def.label)}: {src?.Status}" : "no source: choose a window";
            m_Mode.text = mode;
            m_Status.text = $"{srcLine}\n{Tool.Message}";
            if (m_Page == Page.Main) foreach (var h in m_Headers) h.text.text = h.label();
            float now = Time.unscaledTime;
            foreach (var b in Current)
            {
                b.label.text = b.text();
                if (b.detail) b.detail.text = b.detailText();
                if (b.fill) b.fill.rectTransform.sizeDelta = new Vector2(Mathf.Max(24f, b.rect.sizeDelta.x * Mathf.Clamp01(b.value())), b.rect.sizeDelta.y);
                bool act = b.active != null && b.active();
                bool dimmed = b.dim != null && b.dim();
                b.bar.enabled = act;
                b.bg.color = now < b.pressedUntil ? Style.ButtonPressed : b == m_Hover ? Style.ButtonHover : act ? Style.ButtonOn : Style.Button;
                b.label.color = dimmed ? Style.TextFaint : Style.Text;
            }
        }

        void Place()
        {
            var head = Pointer?.Head;
            var anchor = Pointer?.PaletteAnchor;
            if (anchor && anchor.gameObject.activeInHierarchy)
            {
                // Over the wrist, tilted toward the face.
                var pos = anchor.TransformPoint(new Vector3(0f, 0.09f, 0.06f));
                transform.position = pos;
                var toHead = head ? (head.position - pos) : Vector3.back;
                transform.rotation = Quaternion.LookRotation(-toHead.normalized, Vector3.up);
            }
            else if (head)
            {
                if (!m_Placed) { m_Placed = true; transform.position = head.position + Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.9f + Vector3.up * -0.1f + head.right * 0.42f; }
                var toHead = head.position - transform.position;
                transform.rotation = Quaternion.LookRotation(-toHead.normalized, Vector3.up);
            }
        }

        /// <summary>World position of the centre of the first button on the current page whose label starts with <paramref name="labelStart"/> (tests and diagnostics).</summary>
        public bool TryGetButtonWorldCenter(string labelStart, out Vector3 world)
        {
            foreach (var b in Current)
                if (b.text().StartsWith(labelStart, StringComparison.OrdinalIgnoreCase)) { world = b.rect.TransformPoint(b.rect.rect.center); return true; }
            world = default; return false;
        }

        public bool HandlePointer(in PointerState s, out float hitDistance)
        {
            hitDistance = 0f;
            m_Hover = null;
            if (s.paletteToggle) Toggle();
            if (!Visible) return false;
            var plane = new Plane(transform.forward * -1f, transform.position);
            if (!plane.Raycast(s.ray, out float d) || d <= 0f || d > 3f) return false;
            Vector3 world = s.ray.origin + s.ray.direction * d;
            Vector3 local = transform.InverseTransformPoint(world);           // already in canvas units: the canvas scale is part of this transform. Origin at the rect centre (default pivot)
            Vector2 p = new Vector2(local.x + m_Rect.pivot.x * W, local.y + m_Rect.pivot.y * H);
            if (m_Drag != null)                                                // a slider being dragged follows the laser even if it strays off the bar, until the trigger is released
            {
                if (s.triggerHeld) m_Drag.setValue(Mathf.Round(Mathf.Clamp01((p.x - m_Drag.rect.anchoredPosition.x) / m_Drag.rect.sizeDelta.x) * 100f) / 100f);
                else m_Drag = null;
            }
            if (p.x < -20 || p.x > W + 20 || p.y < -20 || p.y > H + 20) return false;
            hitDistance = d;
            foreach (var b in Current)
            {
                // a couple of units of padding so the seam between two buttons is not a dead zone
                var r = new Rect(b.rect.anchoredPosition - Vector2.one * Style.HitPad, b.rect.sizeDelta + Vector2.one * (2f * Style.HitPad));
                if (r.Contains(p)) { m_Hover = b; break; }
            }
            if (m_Hover != null && s.triggerDown && m_Hover.setValue != null) { m_Drag = m_Hover; Pointer?.Haptic(0.3f, 0.03f); m_Drag.setValue(Mathf.Round(Mathf.Clamp01((p.x - m_Hover.rect.anchoredPosition.x) / m_Hover.rect.sizeDelta.x) * 100f) / 100f); }
            else if (m_Hover != null && s.triggerDown) { m_Hover.pressedUntil = Time.unscaledTime + Style.PressSeconds; Pointer?.Haptic(0.4f, 0.04f); m_Hover.action?.Invoke(); }
            return true;
        }
    }
}
