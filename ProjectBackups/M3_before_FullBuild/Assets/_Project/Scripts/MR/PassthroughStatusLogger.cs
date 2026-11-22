using UnityEngine;

namespace Mini.MR
{
    /// <summary>
    /// Read-only diagnostics for Quest passthrough. It never turns passthrough on or off and never
    /// changes any setting; it only writes clear log lines so a device test can prove whether
    /// passthrough actually started.
    ///
    /// Filter the Quest device log for the "[MiniPassthrough]" prefix
    /// (for example: adb logcat -s Unity | findstr MiniPassthrough).
    ///
    /// In the Unity Editor without a headset, XR is not running, so it only reports that
    /// passthrough cannot be checked here. A black Editor background is not proof of passthrough.
    /// </summary>
    [DisallowMultipleComponent]
    public class PassthroughStatusLogger : MonoBehaviour
    {
        private const string Prefix = "[MiniPassthrough] ";

        [Tooltip("Seconds to wait for passthrough to start before logging a warning.")]
        [SerializeField] private float initTimeoutSeconds = 10f;

        private float _startTime;
        private bool _resultLogged;

        private void OnEnable()
        {
            OVRManager.OnPassthroughInitializedStateChange += HandleStateChange;
        }

        private void OnDisable()
        {
            OVRManager.OnPassthroughInitializedStateChange -= HandleStateChange;
        }

        private void Start()
        {
            _startTime = Time.realtimeSinceStartup;

            if (!UnityEngine.XR.XRSettings.isDeviceActive)
            {
                Debug.Log(Prefix + "No XR headset is active, so passthrough cannot be checked here. " +
                          "Verify passthrough on Quest 2.");
                _resultLogged = true;
                return;
            }

            Debug.Log(Prefix + "XR active. Passthrough supported by system: " +
                      OVRManager.IsInsightPassthroughSupported());
        }

        private void Update()
        {
            if (_resultLogged) return;

            if (OVRManager.IsInsightPassthroughInitialized())
            {
                LogResult(true);
            }
            else if (OVRManager.HasInsightPassthroughInitFailed())
            {
                LogResult(false);
            }
            else if (Time.realtimeSinceStartup - _startTime > initTimeoutSeconds)
            {
                Debug.LogWarning(Prefix + "Passthrough has not started after " + initTimeoutSeconds +
                                 " seconds (pending: " + OVRManager.IsInsightPassthroughInitPending() + ").");
                _resultLogged = true;
            }
        }

        private void HandleStateChange(bool initialized)
        {
            Debug.Log(Prefix + "Passthrough initialization state changed. Initialized: " + initialized);
        }

        private void LogResult(bool initialized)
        {
            _resultLogged = true;
            if (initialized)
                Debug.Log(Prefix + "Passthrough INITIALIZED.");
            else
                Debug.LogError(Prefix + "Passthrough FAILED to initialize.");
        }
    }
}
