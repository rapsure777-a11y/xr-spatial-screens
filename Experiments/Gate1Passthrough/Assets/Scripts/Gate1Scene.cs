using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace Gate1
{
    /// <summary>
    /// Gate 1 scene, built in code: a head-tracked camera that clears to transparent black and six obvious opaque coloured panels around the user.
    /// A text board repeats what the runtime reported, and a 3 s log line says what the display subsystem believes about blending.
    /// </summary>
    public sealed class Gate1Scene : MonoBehaviour
    {
        Camera m_Cam;
        TextMesh m_Text;
        float m_Next;

        void Start()
        {
            var rig = new GameObject("Rig").transform;
            m_Cam = new GameObject("Head").AddComponent<Camera>();
            m_Cam.transform.SetParent(rig, false);
            m_Cam.tag = "MainCamera";
            m_Cam.clearFlags = CameraClearFlags.SolidColor;
            m_Cam.backgroundColor = new Color(0, 0, 0, 0);        // transparent black: required for alpha-blend passthrough
            m_Cam.allowHDR = false; m_Cam.allowMSAA = false;
            m_Cam.nearClipPlane = 0.05f; m_Cam.farClipPlane = 50f;
            var tpd = m_Cam.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            tpd.rotationInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
            tpd.positionInput.action.Enable(); tpd.rotationInput.action.Enable();

            // six panels in a half circle, 2 m away: plain unlit opaque colours.
            var colours = new[] { Color.red, new Color(1, 0.5f, 0), Color.yellow, Color.green, Color.cyan, Color.magenta };
            for (int i = 0; i < colours.Length; i++)
            {
                float a = (-75f + 30f * i) * Mathf.Deg2Rad;
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.transform.position = new Vector3(Mathf.Sin(a) * 2f, 1.4f + (i % 2 == 0 ? 0.15f : -0.15f), Mathf.Cos(a) * 2f);
                q.transform.rotation = Quaternion.LookRotation(q.transform.position - new Vector3(0, 1.4f, 0));
                q.transform.localScale = new Vector3(0.7f, 0.5f, 1f);
                var mat = new Material(Shader.Find("Unlit/Color")) { color = colours[i] };
                q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }

            var board = new GameObject("Board");
            board.transform.position = new Vector3(0, 1.0f, 1.6f);
            m_Text = board.AddComponent<TextMesh>();
            m_Text.characterSize = 0.012f; m_Text.fontSize = 60; m_Text.anchor = TextAnchor.UpperCenter; m_Text.color = Color.white;
            m_Text.text = "Gate 1: do you see your room in colour behind the panels?";
        }

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 3f;
            var subs = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(subs);
            string opaque = subs.Count > 0 ? subs[0].displayOpaque.ToString() : "no display subsystem";
            Debug.Log($"[Gate1] tick: displayOpaque={opaque} cam.clear={m_Cam.clearFlags} bg.alpha={m_Cam.backgroundColor.a} head={m_Cam.transform.position:0.00}");
            if (m_Text) m_Text.text = "Gate 1: do you see your room in colour behind the panels?\ndisplayOpaque=" + opaque + "\n" + PassthroughFeature.Report;
        }
    }

    public static class Boot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start() { new GameObject("Gate1Scene").AddComponent<Gate1Scene>(); }
    }
}
