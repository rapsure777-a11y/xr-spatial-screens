using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Gate1
{
    /// <summary>
    /// Gate 3a: a laser from the right controller's aim pose onto the streamed panel. Trigger = left button (hold to drag), grip = right click,
    /// left stick up/down = wheel. Positions are sent to the PC as (u, v) on the streamed image; the PC maps them onto the window and injects the input.
    /// Bound by usage, so it works with whichever interaction profile the Frame's runtime chooses. A diagnostic line every 3 s says what the controllers report.
    /// </summary>
    public sealed class Gate3Pointer : MonoBehaviour
    {
        const byte Move = 0, LeftDown = 1, LeftUp = 2, RightClick = 3, Wheel = 4;
        const float PressThreshold = 0.6f;

        Gate2Stream m_Stream;
        InputAction m_PosR, m_RotR, m_Trigger, m_Grip, m_StickL, m_PosL, m_RotL, m_Primary, m_StickR;
        bool m_PrimaryPrev, m_Grabbing;
        Vector3 m_GrabOffsetLocal;
        LineRenderer m_Line;
        Transform m_Dot;
        bool m_Held, m_GripPrev, m_Hit;
        Vector2 m_LastSent;
        float m_NextWheel, m_NextDiag;
        public string Status = "controllers: not seen yet";

        public static Gate3Pointer Create(Gate2Stream stream)
        {
            var go = new GameObject("Gate3Pointer");
            var p = go.AddComponent<Gate3Pointer>();
            p.m_Stream = stream;
            return p;
        }

        static InputAction Value(string path, string type) { var a = new InputAction(type: InputActionType.Value, binding: path, expectedControlType: type); a.Enable(); return a; }
        static InputAction Button(string path) { var a = new InputAction(type: InputActionType.Button, binding: path); a.Enable(); return a; }

        void Start()
        {
            const string R = "<XRController>{RightHand}", L = "<XRController>{LeftHand}";
            m_PosR = Value(R + "/pointerPosition", "Vector3"); m_RotR = Value(R + "/pointerRotation", "Quaternion");
            m_PosL = Value(L + "/pointerPosition", "Vector3"); m_RotL = Value(L + "/pointerRotation", "Quaternion");
            m_Trigger = Button(R + "/{Trigger}"); m_Grip = Button(R + "/{Grip}"); m_StickL = Value(L + "/{Primary2DAxis}", "Vector2");
            m_Primary = Button(R + "/{PrimaryButton}"); m_StickR = Value(R + "/{Primary2DAxis}", "Vector2");

            var lineGo = new GameObject("Laser");
            lineGo.transform.SetParent(transform, false);
            m_Line = lineGo.AddComponent<LineRenderer>();
            m_Line.positionCount = 2; m_Line.widthMultiplier = 0.004f; m_Line.useWorldSpace = true;
            m_Line.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(1f, 0.85f, 0.2f) };
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dot.GetComponent<Collider>());
            dot.transform.localScale = Vector3.one * 0.02f;
            dot.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
            m_Dot = dot.transform;
        }

        void Update()
        {
            bool tracked = m_RotR.controls.Count > 0 && m_PosR.controls.Count > 0;
            Vector3 origin = tracked ? m_PosR.ReadValue<Vector3>() : Vector3.zero;
            Quaternion rot = tracked ? m_RotR.ReadValue<Quaternion>() : Quaternion.identity;
            tracked = tracked && (origin.sqrMagnitude > 1e-6f || rot != Quaternion.identity);
            m_Line.enabled = tracked; m_Dot.gameObject.SetActive(false);

            bool trig = m_Trigger.ReadValue<float>() > PressThreshold || m_Trigger.IsPressed();
            bool grip = m_Grip.ReadValue<float>() > PressThreshold || m_Grip.IsPressed();

            Diagnose(tracked, trig, grip, origin);

            if (!tracked)
            {
                if (m_Held) { m_Stream.SendLost(); m_Held = false; }
                m_GripPrev = grip; return;
            }

            var ray = new Ray(origin, rot * Vector3.forward);
            Vector3 end = origin + ray.direction * 3f;
            m_Hit = m_Stream.Raycast(ray, out Vector2 uv, out Vector3 hitPoint);
            if (m_Hit) { end = hitPoint; m_Dot.position = hitPoint; m_Dot.gameObject.SetActive(true); }
            m_Line.SetPosition(0, origin + ray.direction * 0.03f); m_Line.SetPosition(1, end);

            // ---- placement: hold the grip on the panel to carry it with the hand; right stick up/down resizes it
            var panel = m_Stream.PanelTransform;
            if (grip && !m_GripPrev && m_Hit && !m_Held)
            {
                m_Grabbing = true; m_GrabOffsetLocal = Quaternion.Inverse(rot) * (panel.position - origin);
            }
            if (!grip) m_Grabbing = false;
            if (m_Grabbing)
            {
                var pos = origin + rot * m_GrabOffsetLocal;
                var head = Camera.main ? Camera.main.transform.position : new Vector3(0, 1.5f, 0);
                var away = pos - head; away.y = 0f;
                panel.position = pos;
                if (away.sqrMagnitude > 1e-4f) panel.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);   // upright, facing the user
                float sy = m_StickR.ReadValue<Vector2>().y;
                if (Mathf.Abs(sy) > 0.2f) m_Stream.SetWidth(Mathf.Clamp(m_Stream.Width * (1f + sy * 1.2f * Time.unscaledDeltaTime), 0.4f, 4f));
                m_GripPrev = grip;
                return;                                          // no clicks while carrying
            }
            bool primary = m_Primary.IsPressed();
            bool primaryDown = primary && !m_PrimaryPrev; m_PrimaryPrev = primary;

            if (m_Hit)
            {
                if (trig && !m_Held) { m_Stream.SendPointer(LeftDown, uv.x, uv.y, 0); m_Held = true; m_LastSent = uv; }
                else if (m_Held && (uv - m_LastSent).sqrMagnitude > 1e-8f) { m_Stream.SendPointer(Move, uv.x, uv.y, 0); m_LastSent = uv; }
                else if (!m_Held && (uv - m_LastSent).sqrMagnitude > 4e-7f) { m_Stream.SendPointer(Move, uv.x, uv.y, 0); m_LastSent = uv; }
                if (primaryDown && !m_Held) m_Stream.SendPointer(RightClick, uv.x, uv.y, 0);
                float sy = m_StickL.ReadValue<Vector2>().y;
                if (Mathf.Abs(sy) > 0.5f && Time.unscaledTime > m_NextWheel) { m_Stream.SendPointer(Wheel, uv.x, uv.y, Mathf.Sign(sy)); m_NextWheel = Time.unscaledTime + 0.08f; }
            }
            if (m_Held && !trig) { m_Stream.SendPointer(LeftUp, m_Hit ? uv.x : m_LastSent.x, m_Hit ? uv.y : m_LastSent.y, 0); m_Held = false; }
            m_GripPrev = grip;
        }

        void Diagnose(bool tracked, bool trig, bool grip, Vector3 origin)
        {
            if (Time.unscaledTime < m_NextDiag) return;
            m_NextDiag = Time.unscaledTime + 3f;
            var sb = new StringBuilder();
            foreach (var d in InputSystem.devices) if (d is UnityEngine.InputSystem.XR.XRController || d is UnityEngine.InputSystem.XR.XRHMD) sb.Append(d.layout + "/" + d.name + " ");
            Status = $"right tracked={tracked} hit={m_Hit} trig={trig} grip={grip} held={m_Held}";
            Debug.Log($"[Gate3] devices: {sb}| {Status} pos={origin:0.00} | input: {m_Stream.LastInputSent}");
        }

        void OnDestroy() { m_Primary?.Dispose(); m_StickR?.Dispose(); m_PosR?.Dispose(); m_RotR?.Dispose(); m_PosL?.Dispose(); m_RotL?.Dispose(); m_Trigger?.Dispose(); m_Grip?.Dispose(); m_StickL?.Dispose(); }
    }
}
