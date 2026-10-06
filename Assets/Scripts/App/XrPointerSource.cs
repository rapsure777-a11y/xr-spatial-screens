using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using XrSpatial.Spatial;

namespace XrSpatial.App
{
    /// <summary>
    /// VR input: head tracking on the camera, two hand transforms from the controllers' aim poses, and the pointing hand's buttons as a <see cref="PointerState"/>.
    /// Bound by usage, so it works with any OpenXR interaction profile SteamVR selects (Index, Touch, Vive, Steam Frame controllers...).
    /// The right hand points by default (<see cref="LeftHanded"/> swaps), the other hand carries the palette and its menu/primary button toggles it.
    /// </summary>
    public sealed class XrPointerSource : MonoBehaviour, IPointerSource
    {
        public Camera Head;
        public bool LeftHanded;
        public Transform LeftHand { get; private set; }
        public Transform RightHand { get; private set; }

        HandInput m_Left, m_Right;
        bool m_TriggerPrev, m_GripPrev, m_PrimaryPrev, m_SecondaryPrev, m_MenuPrev, m_OffToggle;
        const float PressThreshold = 0.6f;

        public Transform PaletteAnchor => LeftHanded ? RightHand : LeftHand;
        Transform IPointerSource.Head => Head ? Head.transform : null;

        public static XrPointerSource Create(Transform xrOrigin, Camera head)
        {
            var go = new GameObject("XrPointer");
            go.transform.SetParent(xrOrigin, false);
            var s = go.AddComponent<XrPointerSource>();
            s.Head = head;
            s.BindHead();
            return s;
        }

        // Needs Head, which Create assigns only after AddComponent has already run Awake.
        void BindHead()
        {
            var tpd = Head.GetComponent<TrackedPoseDriver>();
            if (!tpd) tpd = Head.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            tpd.rotationInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        }

        void Awake()
        {
            LeftHanded = PlayerPrefs.GetInt("xrss.lefthanded", 0) == 1;
            m_Left = new HandInput("LeftHand");
            m_Right = new HandInput("RightHand");
            LeftHand = NewHand("LeftHand", new Color(0.5f, 0.6f, 1f));
            RightHand = NewHand("RightHand", new Color(1f, 0.7f, 0.4f));
        }

        Transform NewHand(string name, Color c)
        {
            var root = new GameObject(name).transform;
            root.SetParent(transform, false);
            // A small controller stand-in at the aim pose: a body and a pointing tip, so the user sees where each hand is.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root, false);
            body.transform.localScale = new Vector3(0.035f, 0.05f, 0.035f);
            body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            body.transform.localPosition = new Vector3(0f, 0f, -0.04f);
            var mat = Materials.NewUnlit(new Color(c.r, c.g, c.b, 1f), false);
            mat.renderQueue = 2500;
            body.GetComponent<MeshRenderer>().sharedMaterial = mat;
            body.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return root;
        }

        void OnEnable() { m_Left.Enable(); m_Right.Enable(); Application.onBeforeRender += UpdatePoses; }
        void OnDisable() { m_Left.Disable(); m_Right.Disable(); Application.onBeforeRender -= UpdatePoses; }
        void OnDestroy() { m_Left?.Dispose(); m_Right?.Dispose(); }

        HandInput Main => LeftHanded ? m_Left : m_Right;
        HandInput Off => LeftHanded ? m_Right : m_Left;

        void UpdatePoses()
        {
            Pose(m_Left, LeftHand); Pose(m_Right, RightHand);
        }

        void Pose(HandInput h, Transform t)
        {
            bool has = h.aimRotation.controls.Count > 0 && h.aimPosition.controls.Count > 0;
            var pos = has ? h.aimPosition.ReadValue<Vector3>() : h.position.ReadValue<Vector3>();
            var rot = has ? h.aimRotation.ReadValue<Quaternion>() : h.rotation.ReadValue<Quaternion>();
            bool tracked = pos.sqrMagnitude > 1e-6f || rot != Quaternion.identity;
            t.gameObject.SetActive(tracked);
            if (!tracked) return;
            t.localPosition = pos; t.localRotation = rot;
        }

        public PointerState Poll()
        {
            var h = Main;
            var t = LeftHanded ? LeftHand : RightHand;
            var s = new PointerState { valid = t.gameObject.activeSelf };
            if (!s.valid) return s;
            UpdatePoses();
            s.ray = new Ray(t.position, t.forward);
            s.rotation = t.rotation;
            bool trig = h.trigger.ReadValue<float>() > PressThreshold || h.trigger.IsPressed();
            bool grip = h.grip.ReadValue<float>() > PressThreshold || h.grip.IsPressed();
            bool prim = h.primary.IsPressed(), sec = h.secondary.IsPressed(), menu = h.menu.IsPressed();
            s.triggerHeld = trig; s.triggerDown = trig && !m_TriggerPrev; s.triggerUp = !trig && m_TriggerPrev; m_TriggerPrev = trig;
            s.gripHeld = grip; s.gripDown = grip && !m_GripPrev; s.gripUp = !grip && m_GripPrev; m_GripPrev = grip;
            s.primaryDown = prim && !m_PrimaryPrev; m_PrimaryPrev = prim;
            s.secondaryDown = sec && !m_SecondaryPrev; m_SecondaryPrev = sec;
            s.menuDown = menu && !m_MenuPrev; m_MenuPrev = menu;
            s.stick = h.stick.ReadValue<Vector2>();
            var o = Off;
            bool off = o.menu.IsPressed() || o.primary.IsPressed() || o.frameY.IsPressed();
            s.paletteToggle = off && !m_OffToggle; m_OffToggle = off;
            return s;
        }

        public void Haptic(float amplitude, float seconds)
        {
            var node = LeftHanded ? XRNode.LeftHand : XRNode.RightHand;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid) device.SendHapticImpulse(0, Mathf.Clamp01(amplitude), seconds);
        }
    }
}
