using UnityEngine;
using UnityEngine.InputSystem;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// A stand-in "virtual hand" for working without a headset (development, screenshots, a flat-screen preview): the mouse cursor is the laser, left button = trigger,
    /// middle button or G = grip, arrow keys = thumbstick, Backspace = secondary, Esc = menu, Tab = palette. Hold the right mouse button and use WASD/QE to fly the camera.
    /// </summary>
    public sealed class DesktopPointerSource : MonoBehaviour, IPointerSource
    {
        public Camera Cam;
        public float MoveSpeed = 2.5f, LookSpeed = 0.15f;
        public Transform PaletteAnchor => null;
        Transform IPointerSource.Head => Cam ? Cam.transform : null;
        public bool InputEnabled = true;
        /// <summary>Return true for a screen position that is over desktop UI (the control panel), so the virtual hand ignores clicks there.</summary>
        public System.Func<Vector2, bool> Blocker;
        bool m_TriggerPrev, m_GripPrev, m_SecondaryPrev, m_MenuPrev, m_TabPrev;
        float m_Yaw, m_Pitch;

        public static DesktopPointerSource Create(Camera cam)
        {
            var s = cam.gameObject.AddComponent<DesktopPointerSource>();
            s.Cam = cam;
            var e = cam.transform.eulerAngles; s.m_Yaw = e.y; s.m_Pitch = e.x > 180f ? e.x - 360f : e.x;
            return s;
        }

        void Update()
        {
            var kb = Keyboard.current; var mouse = Mouse.current;
            if (!InputEnabled || kb == null || mouse == null) return;
            if (mouse.rightButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                m_Yaw += d.x * LookSpeed; m_Pitch = Mathf.Clamp(m_Pitch - d.y * LookSpeed, -85f, 85f);
                Cam.transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
                Vector3 move = Vector3.zero;
                if (kb.wKey.isPressed) move += Cam.transform.forward;
                if (kb.sKey.isPressed) move -= Cam.transform.forward;
                if (kb.dKey.isPressed) move += Cam.transform.right;
                if (kb.aKey.isPressed) move -= Cam.transform.right;
                if (kb.eKey.isPressed) move += Vector3.up;
                if (kb.qKey.isPressed) move -= Vector3.up;
                Cam.transform.position += move * MoveSpeed * Time.unscaledDeltaTime * (kb.leftShiftKey.isPressed ? 3f : 1f);
            }
        }

        public PointerState Poll()
        {
            var kb = Keyboard.current; var mouse = Mouse.current;
            var s = new PointerState();
            if (!Cam || mouse == null || kb == null) return s;
            s.valid = true;
            var pos = mouse.position.ReadValue();
            bool looking = mouse.rightButton.isPressed;
            s.ray = looking ? new Ray(Cam.transform.position, Cam.transform.forward) : Cam.ScreenPointToRay(new Vector3(pos.x, pos.y, 0f));
            s.rotation = Cam.transform.rotation;
            bool overUi = Blocker != null && Blocker(pos);
            bool trig = InputEnabled && mouse.leftButton.isPressed && !looking && !overUi;
            bool grip = InputEnabled && (mouse.middleButton.isPressed || kb.gKey.isPressed);
            bool sec = InputEnabled && kb.backspaceKey.isPressed, menu = InputEnabled && kb.escapeKey.isPressed, tab = InputEnabled && kb.tabKey.isPressed;
            s.triggerHeld = trig; s.triggerDown = trig && !m_TriggerPrev; s.triggerUp = !trig && m_TriggerPrev; m_TriggerPrev = trig;
            s.gripHeld = grip; s.gripDown = grip && !m_GripPrev; s.gripUp = !grip && m_GripPrev; m_GripPrev = grip;
            s.secondaryDown = sec && !m_SecondaryPrev; m_SecondaryPrev = sec;
            s.menuDown = menu && !m_MenuPrev; m_MenuPrev = menu;
            s.paletteToggle = tab && !m_TabPrev; m_TabPrev = tab;
            float x = 0f, y = 0f;
            if (InputEnabled)
            {
                if (kb.upArrowKey.isPressed) y += 1f; if (kb.downArrowKey.isPressed) y -= 1f;
                if (kb.rightArrowKey.isPressed) x += 1f; if (kb.leftArrowKey.isPressed) x -= 1f;
                y += Mathf.Clamp(mouse.scroll.ReadValue().y / 120f, -1f, 1f) * 2f;
            }
            s.stick = new Vector2(x, y);
            return s;
        }

        public void Haptic(float amplitude, float seconds) { }
    }
}
