using UnityEngine;
using UnityEngine.InputSystem;

namespace XrSpatial.App
{
    /// <summary>
    /// Input actions for one controller, bound by usage so they work with any OpenXR
    /// interaction profile SteamVR picks (Index, Touch, Vive, Frame controllers, etc.).
    /// </summary>
    public class HandInput
    {
        public readonly string side;
        public readonly InputAction position, rotation, aimPosition, aimRotation;
        public readonly InputAction stick, grip, trigger, primary, secondary, menu;
        /// <summary>Steam Frame right-controller X and Y (only bound when the Frame profile is active).</summary>
        public readonly InputAction frameX, frameY;

        public HandInput(string side) // "LeftHand" or "RightHand"
        {
            this.side = side;
            string c = $"<XRController>{{{side}}}";
            position = Value(c + "/devicePosition", "Vector3");
            rotation = Value(c + "/deviceRotation", "Quaternion");
            aimPosition = Value(c + "/pointerPosition", "Vector3");
            aimRotation = Value(c + "/pointerRotation", "Quaternion");
            stick = Value(c + "/{Primary2DAxis}", "Vector2");
            // Analog axes (pressed above 50%) rather than click usages: Index-style "grip click"
            // needs a hard squeeze, and every profile exposes the Grip and Trigger axes.
            grip = Button(c + "/{Grip}");
            trigger = Button(c + "/{Trigger}");
            primary = Button(c + "/{PrimaryButton}");
            secondary = Button(c + "/{SecondaryButton}");
            menu = Button(c + "/{MenuButton}");
            frameX = Button(c + "/{FrameX}");
            frameY = Button(c + "/{FrameY}");
        }

        static InputAction Value(string binding, string type) =>
            new InputAction(type: InputActionType.Value, binding: binding, expectedControlType: type);

        static InputAction Button(string binding) =>
            new InputAction(type: InputActionType.Button, binding: binding);

        InputAction[] All => new[] { position, rotation, aimPosition, aimRotation, stick, grip, trigger, primary, secondary, menu, frameX, frameY };

        public void Enable() { foreach (var a in All) a.Enable(); }
        public void Disable() { foreach (var a in All) a.Disable(); }
        public void Dispose() { foreach (var a in All) a.Dispose(); }

        public Vector2 Stick => stick.ReadValue<Vector2>();
        public bool Grip => grip.IsPressed();
        public bool Trigger => trigger.IsPressed();
    }
}
