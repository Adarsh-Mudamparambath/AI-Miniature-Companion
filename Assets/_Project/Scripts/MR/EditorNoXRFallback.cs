using UnityEngine;
using UnityEngine.InputSystem;

namespace Mini.MR
{
    /// <summary>
    /// Only active when no XR headset is running (Editor Play Mode on Windows, where the OpenXR loader is
    /// intentionally disabled). It lifts the camera rig to seated eye height and tilts it so Mini is in view,
    /// and lets you look around by holding the right mouse button. On Quest it does nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public class EditorNoXRFallback : MonoBehaviour
    {
        [SerializeField] private Transform cameraRig;
        [SerializeField] private float eyeHeight = 1.2f;
        [SerializeField] private float pitch = 32f;
        [SerializeField] private float mouseSensitivity = 0.15f;

        private bool active;
        private float yaw;
        private float currentPitch;

        private void Start()
        {
            // Editor only: on the headset XR may still be starting up during Start, so never trust
            // isDeviceActive alone there.
            active = Application.isEditor && !UnityEngine.XR.XRSettings.isDeviceActive && cameraRig != null;
            if (!active) return;

            Vector3 p = cameraRig.position;
            cameraRig.position = new Vector3(p.x, eyeHeight, p.z);
            currentPitch = pitch;
            yaw = cameraRig.eulerAngles.y;
            Apply();
            Debug.Log("[Mini] No XR headset active: Editor camera fallback enabled (hold right mouse to look around).");
        }

        private void Update()
        {
            if (!active || Mouse.current == null) return;
            if (!Mouse.current.rightButton.isPressed) return;

            Vector2 delta = Mouse.current.delta.ReadValue() * mouseSensitivity;
            yaw += delta.x;
            currentPitch = Mathf.Clamp(currentPitch - delta.y, -60f, 80f);
            Apply();
        }

        private void Apply()
        {
            cameraRig.rotation = Quaternion.Euler(currentPitch, yaw, 0f);
        }
    }
}
