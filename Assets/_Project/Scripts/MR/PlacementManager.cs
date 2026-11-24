using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Mini.Controls;

namespace Mini.MR
{
    /// <summary>
    /// Places and moves Mini in the room.
    ///
    /// Placement mode: point the right controller at the floor or a table and pull the trigger.
    /// The surface height comes from <see cref="ISpatialEnvironmentProvider"/>; on Quest 2 that is a
    /// manual virtual surface the user sets by touching a real table with the controller and pressing grip.
    /// Voice commands call <see cref="ComeHere"/>, <see cref="MoveCloser"/> and friends.
    /// Mini always turns to face the user after moving.
    ///
    /// Positions are in tracking space, so Mini stays put for the whole session. Persisting across
    /// sessions would need Meta spatial anchors (not enabled in this project).
    /// </summary>
    [DisallowMultipleComponent]
    public class PlacementManager : MonoBehaviour
    {
        [SerializeField] private Transform miniAnchor;
        [Tooltip("The user's head (CenterEyeAnchor).")]
        [SerializeField] private Transform userHead;
        [Tooltip("Pointer used for placement (RightControllerAnchor).")]
        [SerializeField] private Transform pointer;
        [Tooltip("Component implementing ISpatialEnvironmentProvider (ManualSurfaceProvider on Quest 2).")]
        [SerializeField] private MonoBehaviour environmentProvider;
        [SerializeField] private MiniControlsBase controls;
        [SerializeField] private Material reticleMaterial;
        [Tooltip("Hide Mini until it has been placed once (phone AR: there is no sensible default spot).")]
        [SerializeField] private bool hideUntilPlaced;

        [Header("Movement")]
        [SerializeField] private float moveStep = 0.15f;
        [SerializeField] private float minDistance = 0.35f;
        [SerializeField] private float maxDistance = 3f;
        [SerializeField] private float comeHereDistance = 0.6f;
        [SerializeField] private float followSharpness = 8f;
        [SerializeField] private float heightAdjustSpeed = 0.3f;
        [Tooltip("Controller tracking point sits slightly above the table when it touches it.")]
        [SerializeField] private float calibrationOffset = -0.03f;

        private ISpatialEnvironmentProvider environmentCache;
        private Transform reticle;
        private Vector3 targetPosition;
        private Quaternion targetRotation;
        private bool hasValidPreview;

        public bool IsPlacing { get; private set; }
        public bool HasBeenPlaced { get; private set; }

        /// <summary>False while a menu is open, so its controller buttons don't also start placement.</summary>
        public bool InputEnabled { get; set; } = true;

        /// <summary>True while Mini is hidden because it has not been placed yet.</summary>
        public bool IsHiddenUntilPlaced { get { return hideUntilPlaced && !HasBeenPlaced; } }
        public ISpatialEnvironmentProvider Environment { get { return environment; } }

        /// <summary>Raised with true when placement mode starts and false when it ends.</summary>
        public event Action<bool> PlacingChanged;
        public event Action Placed;
        public event Action<float> SurfaceCalibrated;

        // Resolved lazily: MiniCompanionApp may call in before this component's Awake has run.
        private ISpatialEnvironmentProvider environment
        {
            get
            {
                if (environmentCache == null)
                {
                    environmentCache = environmentProvider as ISpatialEnvironmentProvider;
                    if (environmentCache == null)
                    {
                        ManualSurfaceProvider manual = GetComponent<ManualSurfaceProvider>();
                        if (manual == null) manual = gameObject.AddComponent<ManualSurfaceProvider>();
                        environmentCache = manual;
                    }
                }
                return environmentCache;
            }
        }

        private void Awake()
        {
            if (miniAnchor != null)
            {
                targetPosition = miniAnchor.position;
                targetRotation = miniAnchor.rotation;
            }
            CreateReticle();
        }

        public void SetEnvironmentProvider(ISpatialEnvironmentProvider provider)
        {
            if (provider != null) environmentCache = provider;
        }

        public void SetUserHead(Transform head)
        {
            if (head != null) userHead = head;
        }

        public void SetDefaultSurfaceHeight(float height)
        {
            environment.SetSurfaceHeight(height);
            SnapTargetToSurface();
        }

        public void BeginPlacement()
        {
            if (IsPlacing) return;
            IsPlacing = true;
            hasValidPreview = false;
            if (PlacingChanged != null) PlacingChanged(true);
        }

        public void ConfirmPlacement()
        {
            if (!IsPlacing) return;
            IsPlacing = false;
            HasBeenPlaced = true;
            SetReticleVisible(false);
            Debug.Log("[Mini] Placed at surface height " + environment.SurfaceHeight.ToString("0.00") + " m");
            if (PlacingChanged != null) PlacingChanged(false);
            if (Placed != null) Placed();
        }

        /// <summary>Forget the placement (e.g. leaving the AR session): Mini counts as never placed.</summary>
        public void ResetPlacement()
        {
            bool wasPlacing = IsPlacing;
            IsPlacing = false;
            HasBeenPlaced = false;
            hasValidPreview = false;
            SetReticleVisible(false);
            if (wasPlacing && PlacingChanged != null) PlacingChanged(false);
        }

        /// <summary>Place Mini in front of the user at the current surface height.</summary>
        public void ComeHere()
        {
            if (userHead == null) return;
            Vector3 forward = FlatForward();
            SetTarget(userHead.position + forward * comeHereDistance);
            FinishVoiceMove();
        }

        public void MoveCloser() { MoveTowardUser(moveStep); }
        public void MoveAway() { MoveTowardUser(-moveStep * 1.3f); }

        /// <summary>Move to the user's left (as the user sees it).</summary>
        public void MoveLeft() { MoveSideways(-moveStep); }

        /// <summary>Move to the user's right (as the user sees it).</summary>
        public void MoveRight() { MoveSideways(moveStep); }

        /// <summary>"Stay here": end placement mode if active and keep the current spot.</summary>
        public void Stay()
        {
            if (IsPlacing) ConfirmPlacement();
            else targetPosition = miniAnchor != null ? miniAnchor.position : targetPosition;
        }

        /// <summary>True on the headset, and in the Editor only when an XR device is running.</summary>
        private static bool UseTrackedPointer
        {
            get { return !Application.isEditor || UnityEngine.XR.XRSettings.isDeviceActive; }
        }

        /// <summary>Use the pointer (controller touching the table) as the new surface height.</summary>
        public void CalibrateSurfaceToPointer()
        {
            Transform source = pointer != null && UseTrackedPointer ? pointer : null;
            if (source == null) return;
            environment.SetSurfaceHeight(source.position.y + calibrationOffset);
            SnapTargetToSurface();
            if (SurfaceCalibrated != null) SurfaceCalibrated(environment.SurfaceHeight);
        }

        private void Update()
        {
            HandleInput();

            if (IsPlacing) UpdatePreview();
            else SetReticleVisible(false);

            if (miniAnchor == null) return;
            UpdateHiddenVisuals();
            float k = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            miniAnchor.position = Vector3.Lerp(miniAnchor.position, targetPosition, k);
            miniAnchor.rotation = Quaternion.Slerp(miniAnchor.rotation, targetRotation, k);
        }

        private void HandleInput()
        {
            if (controls == null || !InputEnabled) return;

            if (controls.PlaceTogglePressed)
            {
                if (IsPlacing) ConfirmPlacement(); else BeginPlacement();
            }

            if (!IsPlacing) return;

            if (controls.CalibratePressed) CalibrateSurfaceToPointer();

            float dy = controls.Adjust.y;
            if (Mathf.Abs(dy) > 0.2f)
            {
                environment.AdjustSurfaceHeight(dy * heightAdjustSpeed * Time.deltaTime);
                SnapTargetToSurface();
            }

            if (controls.ConfirmPressed && hasValidPreview) ConfirmPlacement();
        }

        private void UpdatePreview()
        {
            Pose pose;
            if (environment.TryGetSurfacePose(GetPointerRay(), out pose))
            {
                hasValidPreview = true;
                SetTarget(pose.position);
                if (reticle != null)
                {
                    reticle.position = pose.position + Vector3.up * 0.002f;
                    SetReticleVisible(true);
                }
            }
            else
            {
                SetReticleVisible(false);
            }
        }

        private Ray GetPointerRay()
        {
            Ray controlsRay;
            if (controls != null && controls.TryGetPointerRay(out controlsRay)) return controlsRay;

            if (UseTrackedPointer && pointer != null)
            {
                return new Ray(pointer.position, pointer.forward);
            }

            // Editor without a headset: aim with the mouse.
            Camera cam = Camera.main;
            if (cam != null && Mouse.current != null)
            {
                return cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            }

            Transform head = userHead != null ? userHead : transform;
            return new Ray(head.position, head.forward);
        }

        private void MoveTowardUser(float amount)
        {
            if (userHead == null || miniAnchor == null) return;
            Vector3 toUser = userHead.position - targetPosition;
            toUser.y = 0f;
            float distance = toUser.magnitude;
            if (distance < 1e-3f) return;

            float newDistance = Mathf.Clamp(distance - amount, minDistance, maxDistance);
            Vector3 userFlat = new Vector3(userHead.position.x, targetPosition.y, userHead.position.z);
            SetTarget(userFlat - toUser.normalized * newDistance);
            FinishVoiceMove();
        }

        private void MoveSideways(float amount)
        {
            if (userHead == null) return;
            Vector3 right = userHead.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-4f) return;
            SetTarget(targetPosition + right.normalized * amount);
            FinishVoiceMove();
        }

        private void FinishVoiceMove()
        {
            HasBeenPlaced = true;
            if (IsPlacing) ConfirmPlacement();
        }

        private void SetTarget(Vector3 position)
        {
            targetPosition = environment.ProjectToSurface(position);
            targetRotation = FaceUser(targetPosition);
        }

        private void SnapTargetToSurface()
        {
            SetTarget(targetPosition);
        }

        private Quaternion FaceUser(Vector3 position)
        {
            if (userHead == null) return targetRotation;
            Vector3 dir = userHead.position - position;
            dir.y = 0f;
            return dir.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(dir, Vector3.up) : targetRotation;
        }

        private Vector3 FlatForward()
        {
            Vector3 f = userHead.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        private Renderer[] hiddenRenderers;

        private void UpdateHiddenVisuals()
        {
            bool hide = IsHiddenUntilPlaced && !(IsPlacing && hasValidPreview);
            if (hide && hiddenRenderers == null)
            {
                hiddenRenderers = miniAnchor.GetComponentsInChildren<Renderer>();
                foreach (Renderer r in hiddenRenderers) if (r != null) r.enabled = false;
            }
            else if (!hide && hiddenRenderers != null)
            {
                foreach (Renderer r in hiddenRenderers) if (r != null) r.enabled = true;
                hiddenRenderers = null;
                // First appearance: jump to the preview instead of gliding in from the origin.
                miniAnchor.position = targetPosition;
                miniAnchor.rotation = targetRotation;
            }
        }

        private void CreateReticle()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "PlacementReticle";
            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.localScale = new Vector3(0.14f, 0.0015f, 0.14f);
            Renderer r = go.GetComponent<Renderer>();
            if (r != null)
            {
                if (reticleMaterial != null) r.sharedMaterial = reticleMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            reticle = go.transform;
            go.SetActive(false);
        }

        private void SetReticleVisible(bool visible)
        {
            if (reticle != null && reticle.gameObject.activeSelf != visible) reticle.gameObject.SetActive(visible);
        }

        private void OnDestroy()
        {
            if (reticle != null) Destroy(reticle.gameObject);
        }
    }
}
