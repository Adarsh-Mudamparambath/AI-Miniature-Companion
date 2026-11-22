using UnityEngine;
using UnityEngine.InputSystem;

namespace Mini.Controls
{
    /// <summary>
    /// The small set of fallback controls (voice is the main interface). Uses Input System usages so it
    /// works with the Oculus Touch profile on Quest, plus keyboard/mouse equivalents in the Editor.
    ///
    /// Right A = place / move Mini, right trigger = confirm, right grip = "this is the table height",
    /// right thumbstick = surface height, right B = microphone on/off, left trigger = hold to talk.
    /// </summary>
    [DisallowMultipleComponent]
    public class MiniControllerInput : MonoBehaviour
    {
        private InputAction placeToggle;
        private InputAction confirm;
        private InputAction calibrate;
        private InputAction adjust;
        private InputAction micToggle;
        private InputAction talk;

        // Each button is read from the Input System AND from Meta's OVRInput, so it works whichever
        // input path the Quest runtime feeds.
        public bool PlaceTogglePressed { get { return Log("A", Pressed(placeToggle) || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)); } }
        public bool ConfirmPressed { get { return Log("RightTrigger", Pressed(confirm) || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)); } }
        public bool CalibratePressed { get { return Log("RightGrip", Pressed(calibrate) || OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch)); } }
        public bool MicTogglePressed { get { return Log("B", Pressed(micToggle) || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)); } }
        public bool TalkPressed { get { return Log("LeftTrigger", Pressed(talk) || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch)); } }
        public bool TalkReleased
        {
            get { return (talk != null && talk.WasReleasedThisFrame()) || OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch); }
        }
        public Vector2 Adjust
        {
            get
            {
                Vector2 a = adjust != null ? adjust.ReadValue<Vector2>() : Vector2.zero;
                Vector2 b = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
                return a.sqrMagnitude >= b.sqrMagnitude ? a : b;
            }
        }

        private static bool Log(string button, bool pressed)
        {
            if (pressed) Debug.Log("[Mini] Button " + button);
            return pressed;
        }

        private void Awake()
        {
            placeToggle = new InputAction("PlaceToggle", InputActionType.Button);
            placeToggle.AddBinding("<XRController>{RightHand}/{PrimaryButton}");
            placeToggle.AddBinding("<Keyboard>/p");

            confirm = new InputAction("Confirm", InputActionType.Button);
            confirm.AddBinding("<XRController>{RightHand}/{TriggerButton}");
            confirm.AddBinding("<Keyboard>/enter");
            confirm.AddBinding("<Mouse>/leftButton");

            calibrate = new InputAction("CalibrateSurface", InputActionType.Button);
            calibrate.AddBinding("<XRController>{RightHand}/{GripButton}");
            calibrate.AddBinding("<Keyboard>/g");

            adjust = new InputAction("Adjust", InputActionType.Value);
            adjust.expectedControlType = "Vector2";
            adjust.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
            adjust.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");

            micToggle = new InputAction("MicToggle", InputActionType.Button);
            micToggle.AddBinding("<XRController>{RightHand}/{SecondaryButton}");
            micToggle.AddBinding("<Keyboard>/m");

            talk = new InputAction("PushToTalk", InputActionType.Button);
            talk.AddBinding("<XRController>{LeftHand}/{TriggerButton}");
            talk.AddBinding("<Keyboard>/space");
        }

        private void OnEnable() { SetEnabled(true); }
        private void OnDisable() { SetEnabled(false); }

        private void OnDestroy()
        {
            foreach (InputAction a in All()) if (a != null) a.Dispose();
        }

        private void SetEnabled(bool on)
        {
            foreach (InputAction a in All())
            {
                if (a == null) continue;
                if (on) a.Enable(); else a.Disable();
            }
        }

        private InputAction[] All()
        {
            return new[] { placeToggle, confirm, calibrate, adjust, micToggle, talk };
        }

        private static bool Pressed(InputAction action)
        {
            return action != null && action.WasPressedThisFrame();
        }
    }
}
