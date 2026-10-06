using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.XR;
using UnityEngine.Scripting;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Input;
using PoseControl = UnityEngine.XR.OpenXR.Input.PoseControl;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace XrSpatial.XR
{
    /// <summary>
    /// OpenXR interaction profile for the Steam Frame controllers (/interaction_profiles/valve/frame_controller),
    /// which SteamVR's runtime supports but Unity's OpenXR plugin does not ship.
    ///
    /// Without it SteamVR emulates Oculus Touch and collapses the Frame's buttons: right B, X and Y all
    /// become Touch "B", and the left D-pad becomes X/Y. With it every button arrives separately.
    ///
    /// Frame layout: the right controller has A, B, X, Y and Menu; the left has a D-pad and View.
    /// Mapped to the usual usages so generic code keeps working:
    ///   right A = PrimaryButton, right B = SecondaryButton, right X = FrameX, right Y = FrameY
    ///   left D-pad down = PrimaryButton, left D-pad up = SecondaryButton,
    ///   left D-pad left/right = DpadLeft/DpadRight, right Menu / left View = MenuButton.
    /// Requires the OpenXR extension XR_VALVE_frame_controller_interaction (SteamVR advertises it).
    /// Paths were taken from SteamVR's frame_controller input profile and runtime. If the runtime ever
    /// rejects them, SteamVR falls back to the Touch profile and the game still works.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "Steam Frame Controller Profile",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
        Company = "Gamebreak Labs",
        Desc = "Native input for Steam Frame controllers through SteamVR.",
        // The profile path is only valid once this extension is enabled; without it SteamVR rejects
        // the bindings with XR_ERROR_PATH_UNSUPPORTED (seen in the first Frame test).
        OpenxrExtensionStrings = "XR_VALVE_frame_controller_interaction",
        Version = "0.1.0",
        Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Interaction,
        FeatureId = featureId)]
#endif
    public class SteamFrameControllerProfile : OpenXRInteractionFeature
    {
        public const string featureId = "com.gamebreak.openxr.feature.input.steamframe";
        public const string profile = "/interaction_profiles/valve/frame_controller";
        const string kDeviceLocalizedName = "Steam Frame Controller OpenXR";

        const string Left = "/user/hand/left";
        const string Right = "/user/hand/right";

        [Preserve, InputControlLayout(displayName = "Steam Frame Controller (OpenXR)", commonUsages = new[] { "LeftHand", "RightHand" })]
        public class SteamFrameController : XRControllerWithRumble
        {
            [Preserve, InputControl(usage = "PrimaryButton")] public ButtonControl primaryButton { get; private set; }
            [Preserve, InputControl(usage = "SecondaryButton")] public ButtonControl secondaryButton { get; private set; }
            [Preserve, InputControl(usage = "FrameX")] public ButtonControl xButton { get; private set; }
            [Preserve, InputControl(usage = "FrameY")] public ButtonControl yButton { get; private set; }
            [Preserve, InputControl(usage = "DpadLeft")] public ButtonControl dpadLeft { get; private set; }
            [Preserve, InputControl(usage = "DpadRight")] public ButtonControl dpadRight { get; private set; }
            [Preserve, InputControl(usage = "MenuButton")] public ButtonControl menu { get; private set; }
            [Preserve, InputControl(aliases = new[] { "GripAxis", "squeeze" }, usage = "Grip")] public AxisControl grip { get; private set; }
            [Preserve, InputControl(aliases = new[] { "GripButton", "squeezeClicked" }, usage = "GripButton")] public ButtonControl gripPressed { get; private set; }
            [Preserve, InputControl(usage = "Trigger")] public AxisControl trigger { get; private set; }
            [Preserve, InputControl(usage = "TriggerButton")] public ButtonControl triggerPressed { get; private set; }
            [Preserve, InputControl(aliases = new[] { "joystick", "Primary2DAxis" }, usage = "Primary2DAxis")] public Vector2Control thumbstick { get; private set; }
            [Preserve, InputControl(alias = "joystickClicked", usage = "Primary2DAxisClick")] public ButtonControl thumbstickClicked { get; private set; }
            [Preserve, InputControl(offset = 0, aliases = new[] { "device", "gripPose" }, usage = "Device")] public PoseControl devicePose { get; private set; }
            [Preserve, InputControl(offset = 0, alias = "aimPose", usage = "Pointer")] public PoseControl pointer { get; private set; }
            [Preserve, InputControl(offset = 33)] new public ButtonControl isTracked { get; private set; }
            [Preserve, InputControl(offset = 36)] new public IntegerControl trackingState { get; private set; }
            [Preserve, InputControl(offset = 40, alias = "gripPosition")] new public Vector3Control devicePosition { get; private set; }
            [Preserve, InputControl(offset = 52, alias = "gripOrientation")] new public QuaternionControl deviceRotation { get; private set; }
            [Preserve, InputControl(offset = 100)] public Vector3Control pointerPosition { get; private set; }
            [Preserve, InputControl(offset = 112, alias = "pointerOrientation")] public QuaternionControl pointerRotation { get; private set; }
            [Preserve, InputControl(usage = "Haptic")] public HapticControl haptic { get; private set; }

            protected override void FinishSetup()
            {
                base.FinishSetup();
                primaryButton = GetChildControl<ButtonControl>("primaryButton");
                secondaryButton = GetChildControl<ButtonControl>("secondaryButton");
                xButton = GetChildControl<ButtonControl>("xButton");
                yButton = GetChildControl<ButtonControl>("yButton");
                dpadLeft = GetChildControl<ButtonControl>("dpadLeft");
                dpadRight = GetChildControl<ButtonControl>("dpadRight");
                menu = GetChildControl<ButtonControl>("menu");
                grip = GetChildControl<AxisControl>("grip");
                gripPressed = GetChildControl<ButtonControl>("gripPressed");
                trigger = GetChildControl<AxisControl>("trigger");
                triggerPressed = GetChildControl<ButtonControl>("triggerPressed");
                thumbstick = GetChildControl<Vector2Control>("thumbstick");
                thumbstickClicked = GetChildControl<ButtonControl>("thumbstickClicked");
                devicePose = GetChildControl<PoseControl>("devicePose");
                pointer = GetChildControl<PoseControl>("pointer");
                isTracked = GetChildControl<ButtonControl>("isTracked");
                trackingState = GetChildControl<IntegerControl>("trackingState");
                devicePosition = GetChildControl<Vector3Control>("devicePosition");
                deviceRotation = GetChildControl<QuaternionControl>("deviceRotation");
                pointerPosition = GetChildControl<Vector3Control>("pointerPosition");
                pointerRotation = GetChildControl<QuaternionControl>("pointerRotation");
                haptic = GetChildControl<HapticControl>("haptic");
            }
        }

        protected override void RegisterDeviceLayout()
        {
#if UNITY_EDITOR
            if (!OpenXRLoaderEnabledForSelectedBuildTarget(EditorUserBuildSettings.selectedBuildTargetGroup))
                return;
#endif
            InputSystem.RegisterLayout(typeof(SteamFrameController),
                matches: new InputDeviceMatcher()
                    .WithInterface(XRUtilities.InterfaceMatchAnyVersion)
                    .WithProduct(kDeviceLocalizedName));
        }

        protected override void UnregisterDeviceLayout()
        {
#if UNITY_EDITOR
            if (!OpenXRLoaderEnabledForSelectedBuildTarget(EditorUserBuildSettings.selectedBuildTargetGroup))
                return;
#endif
            InputSystem.RemoveLayout(nameof(SteamFrameController));
        }

        protected override string GetDeviceLayoutName() => nameof(SteamFrameController);

        static ActionBinding Bind(string path, string hand = null) => new ActionBinding
        {
            interactionPath = path,
            interactionProfileName = profile,
            userPaths = hand == null ? null : new List<string> { hand },
        };

        static ActionConfig Action(string name, ActionType type, string usage, params ActionBinding[] bindings) => new ActionConfig
        {
            name = name,
            localizedName = name,
            type = type,
            usages = new List<string> { usage },
            bindings = new List<ActionBinding>(bindings),
        };

        protected override void RegisterActionMapsWithRuntime()
        {
            var map = new ActionMapConfig
            {
                name = "steamframecontroller",
                localizedName = kDeviceLocalizedName,
                desiredInteractionProfile = profile,
                manufacturer = "Valve",
                serialNumber = "",
                deviceInfos = new List<DeviceConfig>
                {
                    new DeviceConfig
                    {
                        characteristics = InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.TrackedDevice | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left,
                        userPath = UserPaths.leftHand,
                    },
                    new DeviceConfig
                    {
                        characteristics = InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.TrackedDevice | InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
                        userPath = UserPaths.rightHand,
                    },
                },
                actions = new List<ActionConfig>
                {
                    Action("primaryButton", ActionType.Binary, "PrimaryButton", Bind("/input/a/click", Right), Bind("/input/dpad_down/click", Left)),
                    Action("secondaryButton", ActionType.Binary, "SecondaryButton", Bind("/input/b/click", Right), Bind("/input/dpad_up/click", Left)),
                    Action("xButton", ActionType.Binary, "FrameX", Bind("/input/x/click", Right)),
                    Action("yButton", ActionType.Binary, "FrameY", Bind("/input/y/click", Right)),
                    Action("dpadLeft", ActionType.Binary, "DpadLeft", Bind("/input/dpad_left/click", Left)),
                    Action("dpadRight", ActionType.Binary, "DpadRight", Bind("/input/dpad_right/click", Left)),
                    Action("menu", ActionType.Binary, "MenuButton", Bind("/input/menu/click", Right), Bind("/input/view/click", Left)),
                    Action("grip", ActionType.Axis1D, "Grip", Bind("/input/squeeze/value")),
                    Action("gripPressed", ActionType.Binary, "GripButton", Bind("/input/squeeze/value")),
                    Action("trigger", ActionType.Axis1D, "Trigger", Bind("/input/trigger/value")),
                    Action("triggerPressed", ActionType.Binary, "TriggerButton", Bind("/input/trigger/value")),
                    Action("thumbstick", ActionType.Axis2D, "Primary2DAxis", Bind("/input/thumbstick")),
                    Action("thumbstickClicked", ActionType.Binary, "Primary2DAxisClick", Bind("/input/thumbstick/click")),
                    Action("devicePose", ActionType.Pose, "Device", Bind("/input/grip/pose")),
                    Action("pointer", ActionType.Pose, "Pointer", Bind("/input/aim/pose")),
                    Action("haptic", ActionType.Vibrate, "Haptic", Bind("/output/haptic")),
                },
            };
            AddActionMap(map);
        }
    }
}
