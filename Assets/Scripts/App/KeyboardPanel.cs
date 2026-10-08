using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using XrSpatial.Capture;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// A US-layout keyboard floating in front of the user, poked with the laser like the palette (UGUI for drawing, laser-plane hit test for pressing). Keys go to the window
    /// last clicked, through the PC host: letters and symbols are typed as characters, Backspace/Enter/Tab/Esc/arrows/Delete as virtual keys, and Shift/Ctrl/Alt latch for one key
    /// (Ctrl or Alt plus a letter or digit is sent as a real key combination, e.g. Ctrl+C). Shown and hidden from the palette's Keyboard button.
    /// </summary>
    public sealed class KeyboardPanel : MonoBehaviour, IUiLayer
    {
        public IPointerSource Pointer;
        public SurfaceTool Tool;
        public bool Visible { get; private set; }

        const float Unit = 70f, KeyH = 70f, Gap = 6f, Head = 64f, Scale = 0.00045f;
        const int VK_SHIFT = 0xA0, VK_CONTROL = 0xA2, VK_MENU = 0xA4;
        enum Kind { Char, Vk, Shift, Ctrl, Alt, Space, Close, Move }

        sealed class Key
        {
            public string label; public char ch, shiftCh; public Kind kind; public int vk; public float width = 1f;
            public RectTransform rect; public Image bg; public Text text;
        }

        readonly List<Key> m_Keys = new List<Key>();
        Canvas m_Canvas;
        RectTransform m_Rect;
        Text m_Title;
        Font m_Font;
        Key m_Hover;
        bool m_Shift, m_Ctrl, m_Alt, m_Placed;
        float W, H;

        public static KeyboardPanel Create(Transform parent, SurfaceTool tool, IPointerSource pointer)
        {
            var go = new GameObject("Keyboard");
            go.transform.SetParent(parent, false);
            var k = go.AddComponent<KeyboardPanel>();
            k.Tool = tool; k.Pointer = pointer;
            k.Build();
            k.SetVisible(false);
            return k;
        }

        static Key C(string pair) => new Key { kind = Kind.Char, ch = pair[0], shiftCh = pair[1], label = pair[0].ToString() };
        static Key V(string label, int vk, float w = 1f) => new Key { kind = Kind.Vk, label = label, vk = vk, width = w };
        static Key M(Kind kind, string label, float w) => new Key { kind = kind, label = label, width = w };

        List<List<Key>> Layout()
        {
            var rows = new List<List<Key>>();
            var r1 = new List<Key>(); foreach (var p in new[] { "`~", "1!", "2@", "3#", "4$", "5%", "6^", "7&", "8*", "9(", "0)", "-_", "=+" }) r1.Add(C(p)); r1.Add(V("Bksp", 0x08, 2f)); rows.Add(r1);
            var r2 = new List<Key> { V("Tab", 0x09, 1.5f) }; foreach (var p in new[] { "qQ", "wW", "eE", "rR", "tT", "yY", "uU", "iI", "oO", "pP", "[{", "]}" }) r2.Add(C(p)); r2.Add(C("\\|")); r2[r2.Count - 1].width = 1.5f; rows.Add(r2);
            var r3 = new List<Key> { V("Esc", 0x1B, 1.75f) }; foreach (var p in new[] { "aA", "sS", "dD", "fF", "gG", "hH", "jJ", "kK", "lL", ";:", "'\"" }) r3.Add(C(p)); r3.Add(V("Enter", 0x0D, 2.25f)); rows.Add(r3);
            var r4 = new List<Key> { M(Kind.Shift, "Shift", 2.25f) }; foreach (var p in new[] { "zZ", "xX", "cC", "vV", "bB", "nN", "mM", ",<", ".>", "/?" }) r4.Add(C(p)); r4.Add(M(Kind.Shift, "Shift", 2.75f)); rows.Add(r4);
            var r5 = new List<Key> { M(Kind.Ctrl, "Ctrl", 1.5f), M(Kind.Alt, "Alt", 1.5f), M(Kind.Space, "Space", 4.75f), V("Del", 0x2E, 1.25f), V("<", 0x25), V("v", 0x28), V("^", 0x26), V(">", 0x27), M(Kind.Move, "Move", 1f), M(Kind.Close, "X", 1f) }; rows.Add(r5);
            return rows;
        }

        void Build()
        {
            m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var rows = Layout();
            float rowW = 0f; foreach (var k in rows[0]) rowW += k.width;       // the widest row defines the width (15 units)
            W = 15f * Unit + 20f; H = rows.Count * (KeyH + Gap) + Head + 20f;
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            m_Rect = GetComponent<RectTransform>();
            m_Rect.sizeDelta = new Vector2(W, H);
            m_Rect.localScale = Vector3.one * Scale;
            Img("Back", m_Rect, Vector2.zero, new Vector2(W, H), new Color(0.05f, 0.07f, 0.1f, 0.9f));
            m_Title = Txt("Title", m_Rect, new Vector2(16, H - Head), new Vector2(W - 32, Head - 8), 28, TextAnchor.MiddleLeft, new Color(0.85f, 0.95f, 1f));

            for (int r = 0; r < rows.Count; r++)
            {
                float x = 10f, y = H - Head - 10f - (r + 1) * (KeyH + Gap) + Gap;
                foreach (var k in rows[r])
                {
                    float w = k.width * Unit - Gap;
                    k.rect = Img("Key", m_Rect, new Vector2(x, y), new Vector2(w, KeyH), Color.white, out k.bg).rectTransform;
                    k.text = Txt("L", k.rect, Vector2.zero, new Vector2(w, KeyH), 28, TextAnchor.MiddleCenter, Color.white);
                    m_Keys.Add(k);
                    x += k.width * Unit;
                }
            }
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
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        public void SetVisible(bool v)
        {
            Visible = v; gameObject.SetActive(v);
            if (v) m_Placed = false;                                 // appears in front of the user each time it is opened
        }

        public void Toggle() => SetVisible(!Visible);

        void LateUpdate()
        {
            if (!Visible) return;
            PlaceInFront();
            m_Title.text = RemoteHost.FocusStream != 0 ? "Typing into: " + RemoteHost.FocusLabel : "Click a window first, then type";
            foreach (var k in m_Keys)
            {
                bool latched = (k.kind == Kind.Shift && m_Shift) || (k.kind == Kind.Ctrl && m_Ctrl) || (k.kind == Kind.Alt && m_Alt);
                k.text.text = k.kind == Kind.Char ? (m_Shift ? k.shiftCh : k.ch).ToString() : k.label;
                k.bg.color = k == m_Hover ? new Color(0.25f, 0.6f, 0.85f) : latched ? new Color(0.2f, 0.5f, 0.3f) : k.kind == Kind.Char || k.kind == Kind.Space ? new Color(0.14f, 0.18f, 0.24f) : new Color(0.1f, 0.13f, 0.18f);
            }
        }

        void PlaceInFront()
        {
            if (m_Placed) return;
            var head = Pointer?.Head;
            if (!head) return;
            m_Placed = true;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            transform.position = head.position + flat * 0.62f + Vector3.up * -0.38f;
            var toHead = head.position - transform.position;
            transform.rotation = Quaternion.LookRotation(-toHead.normalized, Vector3.up);
            transform.rotation = Quaternion.AngleAxis(-18f, transform.right) * transform.rotation;      // leaned back like a keyboard on a stand
        }

        public bool HandlePointer(in PointerState s, out float hitDistance)
        {
            hitDistance = 0f;
            m_Hover = null;
            if (!Visible) return false;
            var plane = new Plane(transform.forward * -1f, transform.position);
            if (!plane.Raycast(s.ray, out float d) || d <= 0f || d > 3f) return false;
            Vector3 local = transform.InverseTransformPoint(s.ray.origin + s.ray.direction * d);
            Vector2 p = new Vector2(local.x + m_Rect.pivot.x * W, local.y + m_Rect.pivot.y * H);
            if (p.x < -20 || p.x > W + 20 || p.y < -20 || p.y > H + 20) return false;
            hitDistance = d;
            foreach (var k in m_Keys)
                if (new Rect(k.rect.anchoredPosition, k.rect.sizeDelta).Contains(p)) { m_Hover = k; break; }
            if (m_Hover != null && s.triggerDown) { Pointer?.Haptic(0.35f, 0.03f); Press(m_Hover); }
            return true;
        }

        // ------------------------------------------------------------------ typing

        void Press(Key k)
        {
            switch (k.kind)
            {
                case Kind.Shift: m_Shift = !m_Shift; return;
                case Kind.Ctrl: m_Ctrl = !m_Ctrl; return;
                case Kind.Alt: m_Alt = !m_Alt; return;
                case Kind.Close: SetVisible(false); return;
                case Kind.Move: m_Placed = false; return;
            }
            ushort stream = RemoteHost.FocusStream;
            if (stream == 0) { Tool?.Say("Click the window you want to type into first.", 3f); return; }

            bool combo = m_Ctrl || m_Alt;
            int vk = 0;
            if (k.kind == Kind.Vk) vk = k.vk;
            else if (k.kind == Kind.Space) { if (combo) vk = 0x20; }
            else if (combo)
            {
                char c = char.ToUpperInvariant(k.ch);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) vk = c;      // Ctrl+C, Alt+4 ...
            }

            if (vk != 0)
            {
                if (m_Ctrl) RemoteHost.SendKey(stream, RemoteHost.KeyDown, VK_CONTROL);
                if (m_Alt) RemoteHost.SendKey(stream, RemoteHost.KeyDown, VK_MENU);
                if (m_Shift) RemoteHost.SendKey(stream, RemoteHost.KeyDown, VK_SHIFT);
                RemoteHost.SendKey(stream, RemoteHost.KeyDown, (uint)vk);
                RemoteHost.SendKey(stream, RemoteHost.KeyUp, (uint)vk);
                if (m_Shift) RemoteHost.SendKey(stream, RemoteHost.KeyUp, VK_SHIFT);
                if (m_Alt) RemoteHost.SendKey(stream, RemoteHost.KeyUp, VK_MENU);
                if (m_Ctrl) RemoteHost.SendKey(stream, RemoteHost.KeyUp, VK_CONTROL);
            }
            else
            {
                char c = k.kind == Kind.Space ? ' ' : (m_Shift ? k.shiftCh : k.ch);
                RemoteHost.SendKey(stream, RemoteHost.KeyText, c);
            }
            m_Shift = m_Ctrl = m_Alt = false;                         // modifiers latch for one key
        }
    }
}
