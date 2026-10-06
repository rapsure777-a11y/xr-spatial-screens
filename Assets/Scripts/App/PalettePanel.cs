using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using XrSpatial.Core;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// The tool palette: a small world-space panel held over the non-pointing wrist (or floating in front of the head without controllers). The pointing hand's laser
    /// highlights a button and the trigger presses it. UGUI is used for layout/text only; hit-testing is done by intersecting the laser with the canvas plane, so no
    /// EventSystem or physics raycaster is needed (and nothing in the scene can steal the click).
    /// </summary>
    public sealed class PalettePanel : MonoBehaviour, IUiLayer
    {
        public SurfaceTool Tool;
        public SpatialWorkspace Workspace;
        public IPointerSource Pointer;
        public Action OnAddPattern;
        public bool Visible { get; private set; } = true;

        const float W = 560f, H = 700f, Scale = 0.00055f;          // 0.31 m x 0.385 m
        Canvas m_Canvas;
        RectTransform m_Rect;
        Text m_Status;
        readonly List<Btn> m_Buttons = new List<Btn>();
        Btn m_Hover;
        Font m_Font;

        sealed class Btn
        {
            public RectTransform rect; public Image bg; public Text label; public Func<string> text; public Action action; public Func<bool> active;
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
            m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            m_Rect = GetComponent<RectTransform>();
            m_Rect.sizeDelta = new Vector2(W, H);
            m_Rect.localScale = Vector3.one * Scale;
            Img("Back", m_Rect, new Vector2(0, 0), new Vector2(W, H), new Color(0.05f, 0.07f, 0.1f, 0.88f));
            m_Status = Txt("Status", m_Rect, new Vector2(20, H - 150), new Vector2(W - 40, 140), 26, TextAnchor.UpperLeft, new Color(0.85f, 0.95f, 1f));

            float y = H - 170, bw = (W - 60) / 2f, bh = 92f, gap = 14f;
            int col = 0;
            void Add(Func<string> text, Action action, Func<bool> active = null)
            {
                float x = 20 + col * (bw + 20);
                var b = new Btn { text = text, action = action, active = active };
                b.rect = Img("Btn", m_Rect, new Vector2(x, y - bh), new Vector2(bw, bh), Color.white, out b.bg).rectTransform;
                b.label = Txt("Label", b.rect, new Vector2(8, 0), new Vector2(bw - 16, bh), 27, TextAnchor.MiddleCenter, Color.white);
                m_Buttons.Add(b);
                if (++col == 2) { col = 0; y -= bh + gap; }
            }
            Add(() => Tool.Mode == ToolMode.Place ? $"Placing {Tool.PlacedCount}/4" : "New screen", () => { if (Tool.Mode == ToolMode.Place) Tool.SetIdle(); else Tool.BeginPlace(); }, () => Tool.Mode == ToolMode.Place);
            Add(() => Tool.Mode == ToolMode.Crop ? "Cropping..." : "Crop", () => { if (Tool.Mode == ToolMode.Crop) Tool.SetIdle(); else Tool.BeginCrop(); }, () => Tool.Mode == ToolMode.Crop);
            Add(() => "Delete screen", Tool.DeleteSelected);
            Add(() => "Turn picture", Tool.RotateSelectedPicture);
            Add(() => Tool.InteractMode ? "Interact: ON" : "Interact: off", Tool.ToggleInteract, () => Tool.InteractMode);
            Add(() => Tool.TouchPlacement ? "Points: touch" : "Points: laser", Tool.ToggleTouch);
            Add(() => "Next source", Tool.CycleSource);
            Add(() => "Test pattern", () => OnAddPattern?.Invoke());
            Add(() => "Reset crop", Tool.ResetSelectedCrop);
            Add(() => "Save layout", () => { Workspace.SaveNow(); Tool.Say("Layout saved", 2f); });
            Add(() => "Hide palette", () => SetVisible(false));
        }

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

        public void SetVisible(bool v) { Visible = v; gameObject.SetActive(v); }
        public void Toggle() => SetVisible(!Visible);

        void LateUpdate()
        {
            if (!Visible) return;
            Place();
            string mode = Tool.Mode == ToolMode.Place ? $"PLACE: point {Tool.PlacedCount + 1} of 4" : Tool.Mode == ToolMode.Crop ? "CROP: drag a box on a screen" : Tool.InteractMode ? "INTERACT" : "EDIT";
            var src = Workspace.GetSource(Workspace.ActiveSourceId);
            var def = Workspace.Layout.FindSource(Workspace.ActiveSourceId);
            string srcLine = def != null ? $"{(string.IsNullOrEmpty(def.label) ? def.id : def.label)}: {src?.Status}" : "no source";
            m_Status.text = $"{mode}\n{srcLine}\n{Tool.Message}";
            foreach (var b in m_Buttons)
            {
                b.label.text = b.text();
                bool act = b.active != null && b.active();
                b.bg.color = b == m_Hover ? new Color(0.25f, 0.6f, 0.85f, 1f) : act ? new Color(0.2f, 0.5f, 0.3f, 1f) : new Color(0.14f, 0.18f, 0.24f, 1f);
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
        bool m_Placed;

        public bool HandlePointer(in PointerState s, out float hitDistance)
        {
            hitDistance = 0f;
            m_Hover = null;
            if (s.paletteToggle) Toggle();
            if (!Visible) return false;
            var plane = new Plane(transform.forward * -1f, transform.position);
            if (!plane.Raycast(s.ray, out float d) || d <= 0f || d > 3f) return false;
            Vector3 world = s.ray.origin + s.ray.direction * d;
            Vector3 local = transform.InverseTransformPoint(world) / Scale;           // canvas units, origin at the rect pivot
            // The rect's pivot is centred by default for a Canvas root; shift to bottom-left space.
            Vector2 p = new Vector2(local.x + m_Rect.pivot.x * W, local.y + m_Rect.pivot.y * H);
            if (p.x < -20 || p.x > W + 20 || p.y < -20 || p.y > H + 20) return false;
            hitDistance = d;
            foreach (var b in m_Buttons)
            {
                var r = new Rect(b.rect.anchoredPosition, b.rect.sizeDelta);
                if (r.Contains(p)) { m_Hover = b; break; }
            }
            if (m_Hover != null && s.triggerDown) { Pointer?.Haptic(0.4f, 0.04f); m_Hover.action?.Invoke(); }
            return true;
        }
    }
}
