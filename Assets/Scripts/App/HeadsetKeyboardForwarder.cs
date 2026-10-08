using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using XrSpatial.Capture;

namespace XrSpatial.App
{
    /// <summary>
    /// A keyboard connected to the headset itself (Bluetooth): what it types goes to the PC window last clicked. Typed characters are sent as text (so the keyboard layout and shift
    /// state of the headset decide what appears); Backspace, Enter, arrows, function keys and the modifier keys are sent as real key presses, and letters and digits too while
    /// Ctrl or Alt is held (so Ctrl+C, Alt+Tab-style shortcuts work). A held special key repeats like on a PC. Everything is released when the app loses focus.
    /// The PC's own keyboard needs none of this: it already types into the window that was clicked last.
    /// </summary>
    public sealed class HeadsetKeyboardForwarder : MonoBehaviour
    {
        const float RepeatDelay = 0.45f, RepeatEvery = 0.035f;
        static readonly (Key key, uint vk)[] Specials =
        {
            (Key.Backspace, 0x08), (Key.Tab, 0x09), (Key.Enter, 0x0D), (Key.NumpadEnter, 0x0D), (Key.Escape, 0x1B),
            (Key.LeftArrow, 0x25), (Key.UpArrow, 0x26), (Key.RightArrow, 0x27), (Key.DownArrow, 0x28),
            (Key.Delete, 0x2E), (Key.Home, 0x24), (Key.End, 0x23), (Key.PageUp, 0x21), (Key.PageDown, 0x22), (Key.Insert, 0x2D),
            (Key.F1, 0x70), (Key.F2, 0x71), (Key.F3, 0x72), (Key.F4, 0x73), (Key.F5, 0x74), (Key.F6, 0x75), (Key.F7, 0x76), (Key.F8, 0x77), (Key.F9, 0x78), (Key.F10, 0x79), (Key.F11, 0x7A), (Key.F12, 0x7B),
        };
        static readonly (Key key, uint vk)[] Modifiers = { (Key.LeftShift, 0xA0), (Key.RightShift, 0xA1), (Key.LeftCtrl, 0xA2), (Key.RightCtrl, 0xA3), (Key.LeftAlt, 0xA4), (Key.RightAlt, 0xA5) };

        Keyboard m_Hooked;
        readonly HashSet<uint> m_Down = new HashSet<uint>();
        readonly Dictionary<uint, float> m_NextRepeat = new Dictionary<uint, float>();
        ushort m_StreamOfHeld;
        public string Status { get; private set; } = "no keyboard connected to the headset";

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != m_Hooked)
            {
                if (m_Hooked != null) m_Hooked.onTextInput -= OnText;
                if (kb != null) { kb.onTextInput += OnText; Debug.Log("[XrSpatial] keyboard on the headset: " + kb.displayName); }
                m_Hooked = kb;
                Status = kb != null ? "keyboard: " + kb.displayName : "no keyboard connected to the headset";
            }
            if (kb == null) return;
            ushort stream = RemoteHost.FocusStream;
            if (stream == 0) return;

            bool ctrlAlt = kb.ctrlKey.isPressed || kb.altKey.isPressed;
            foreach (var (key, vk) in Modifiers) Edge(kb[key], vk, stream, false);
            foreach (var (key, vk) in Specials) Edge(kb[key], vk, stream, true);
            if (ctrlAlt)
            {
                for (int i = 0; i < 26; i++) Edge(kb[Key.A + i], (uint)('A' + i), stream, false);
                for (int i = 0; i < 10; i++) Edge(kb[Key.Digit0 + i], (uint)('0' + i), stream, false);
                Edge(kb.spaceKey, 0x20, stream, false);
            }
        }

        void Edge(UnityEngine.InputSystem.Controls.KeyControl c, uint vk, ushort stream, bool repeat)
        {
            if (c.wasPressedThisFrame)
            {
                RemoteHost.SendKey(stream, RemoteHost.KeyDown, vk); m_Down.Add(vk); m_StreamOfHeld = stream;
                if (repeat) m_NextRepeat[vk] = Time.unscaledTime + RepeatDelay;
            }
            else if (c.wasReleasedThisFrame)
            {
                if (m_Down.Remove(vk)) RemoteHost.SendKey(m_StreamOfHeld, RemoteHost.KeyUp, vk);
                m_NextRepeat.Remove(vk);
            }
            else if (repeat && c.isPressed && m_NextRepeat.TryGetValue(vk, out float t) && Time.unscaledTime >= t)
            {
                RemoteHost.SendKey(stream, RemoteHost.KeyDown, vk);                      // another press while held: the PC treats it as auto-repeat
                m_NextRepeat[vk] = Time.unscaledTime + RepeatEvery;
            }
        }

        void OnText(char c)
        {
            ushort stream = RemoteHost.FocusStream;
            var kb = Keyboard.current;
            if (stream == 0 || kb == null || c < 0x20 || c == 0x7F) return;
            if (kb.ctrlKey.isPressed || kb.altKey.isPressed) return;                       // shortcuts are sent as keys
            RemoteHost.SendKey(stream, RemoteHost.KeyText, c);
        }

        void ReleaseAll()
        {
            foreach (var vk in m_Down) RemoteHost.SendKey(m_StreamOfHeld, RemoteHost.KeyUp, vk);
            m_Down.Clear(); m_NextRepeat.Clear();
        }

        void OnApplicationFocus(bool focus) { if (!focus) ReleaseAll(); }
        void OnDisable() { ReleaseAll(); if (m_Hooked != null) m_Hooked.onTextInput -= OnText; m_Hooked = null; }
    }
}
