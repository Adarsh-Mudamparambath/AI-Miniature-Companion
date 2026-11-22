using System;
using UnityEngine;
using Mini.Core;

namespace Mini.Animation
{
    /// <summary>
    /// Adapter for the final rigged Blender character (Generic rig, root motion off). Put it on the
    /// imported model next to its Animator, in place of PrimitiveRobotVisual. Everything above it
    /// (MiniCharacterController, CharacterManager, ConversationManager, LipSyncController) stays the same.
    ///
    /// Animator contract (create these in the Animator Controller):
    /// - int parameter "State": 0 Idle, 1 Listening, 2 Thinking, 3 Speaking, 4 Sleeping
    ///   (clips: Idle_Base, Listen_Idle, Think_Idle, Speak_Idle, Sleep; Listen_Start/Think_Start/Wake_Up as transitions)
    /// - trigger per gesture, named exactly like CharacterGesture: LookLeft, LookRight, Blink, Nod, Wave,
    ///   ShakeHead, PointLeft, PointRight, Shrug (put gestures on an upper-body layer with an avatar mask)
    /// - trigger "CancelGestures"
    /// Face: blend shapes on the face mesh, named in the arrays below (missing names are skipped).
    /// Look-at: the head and eye bones are rotated procedurally after the Animator runs.
    /// </summary>
    [DisallowMultipleComponent]
    public class AnimatorCharacterVisual : MonoBehaviour, ICharacterVisual, ICharacterFace
    {
        [SerializeField] private Animator animator;
        [SerializeField] private SkinnedMeshRenderer faceMesh;
        [SerializeField] private Transform headBone;
        [SerializeField] private Transform[] eyeBones;

        [Header("Blend shape names (index = enum value)")]
        [Tooltip("Order: Neutral, Happy, Sad, Surprised, Confused, Curious, Thinking.")]
        [SerializeField] private string[] emotionShapes = { "", "Smile", "Sad", "Surprise", "Confused", "Curious", "Thinking" };
        [Tooltip("Order: Rest, AA, EE, IH, OH, OO, FV, MBP, LTD.")]
        [SerializeField] private string[] visemeShapes = { "", "V_AA", "V_EE", "V_IH", "V_OH", "V_OO", "V_FV", "V_MBP", "V_LTD" };
        [SerializeField] private string blinkShape = "Blink";

        [Header("Tuning")]
        [SerializeField] private float maxHeadYaw = 40f;
        [SerializeField] private float maxHeadPitch = 22f;
        [SerializeField] private float maxEyeAngle = 12f;
        [SerializeField] private float faceResponsiveness = 12f;
        [SerializeField] private float lookResponsiveness = 5f;
        [SerializeField] private Vector2 autoBlinkInterval = new Vector2(2.5f, 5f);

        private static readonly int StateParam = Animator.StringToHash("State");
        private static readonly int CancelParam = Animator.StringToHash("CancelGestures");

        private int[] emotionIndex;
        private int[] visemeIndex;
        private int blinkIndex = -1;
        private float[] emotionWeights;
        private float[] visemeWeights;

        private CharacterState state;
        private CharacterEmotion emotion;
        private float emotionIntensity;
        private bool speechActive;
        private float speechOpen;
        private Viseme speechViseme;
        private bool hasLookTarget;
        private Vector3 lookTarget;
        private float lookWeight;
        private Quaternion lookOffset = Quaternion.identity;
        private float blinkTimer;
        private float blinkTime = -1f;

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = false;
            emotionIndex = ResolveShapes(emotionShapes);
            visemeIndex = ResolveShapes(visemeShapes);
            emotionWeights = new float[emotionIndex.Length];
            visemeWeights = new float[visemeIndex.Length];
            if (faceMesh != null && !string.IsNullOrEmpty(blinkShape))
                blinkIndex = faceMesh.sharedMesh.GetBlendShapeIndex(blinkShape);
            blinkTimer = UnityEngine.Random.Range(autoBlinkInterval.x, autoBlinkInterval.y);
        }

        private int[] ResolveShapes(string[] names)
        {
            int[] result = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                result[i] = faceMesh != null && faceMesh.sharedMesh != null && !string.IsNullOrEmpty(names[i])
                    ? faceMesh.sharedMesh.GetBlendShapeIndex(names[i]) : -1;
            }
            return result;
        }

        public void SetState(CharacterState newState)
        {
            state = newState;
            if (animator != null) animator.SetInteger(StateParam, (int)newState);
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            if (gesture == CharacterGesture.Blink) { blinkTime = 0f; return; }
            if (animator != null) animator.SetTrigger(gesture.ToString());
        }

        public void CancelGestures()
        {
            if (animator == null) return;
            foreach (CharacterGesture g in Enum.GetValues(typeof(CharacterGesture))) animator.ResetTrigger(g.ToString());
            animator.SetTrigger(CancelParam);
        }

        public void SetEmotion(CharacterEmotion newEmotion, float intensity)
        {
            emotion = newEmotion;
            emotionIntensity = Mathf.Clamp01(intensity);
        }

        public void SetLookTarget(Vector3? worldPosition)
        {
            hasLookTarget = worldPosition.HasValue;
            if (hasLookTarget) lookTarget = worldPosition.Value;
        }

        public void SetSpeechMouth(float openness, Viseme viseme)
        {
            speechActive = true;
            speechOpen = Mathf.Clamp01(openness);
            speechViseme = viseme;
        }

        public void ReleaseSpeechMouth()
        {
            speechActive = false;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            float k = 1f - Mathf.Exp(-faceResponsiveness * dt);

            for (int i = 0; i < emotionWeights.Length; i++)
            {
                float target = (int)emotion == i && state != CharacterState.Sleeping ? emotionIntensity : 0f;
                emotionWeights[i] = Mathf.Lerp(emotionWeights[i], target, k);
                SetShape(emotionIndex[i], emotionWeights[i]);
            }
            for (int i = 0; i < visemeWeights.Length; i++)
            {
                float target = speechActive && (int)speechViseme == i ? Mathf.Max(0.15f, speechOpen) : 0f;
                visemeWeights[i] = Mathf.Lerp(visemeWeights[i], target, k * 2f);
                SetShape(visemeIndex[i], visemeWeights[i]);
            }

            UpdateBlink(dt);
            UpdateLook(dt);
        }

        private void UpdateBlink(float dt)
        {
            float closed = state == CharacterState.Sleeping ? 1f : 0f;
            if (state != CharacterState.Sleeping)
            {
                blinkTimer -= dt;
                if (blinkTimer <= 0f)
                {
                    blinkTime = 0f;
                    blinkTimer = UnityEngine.Random.Range(autoBlinkInterval.x, autoBlinkInterval.y);
                }
                if (blinkTime >= 0f)
                {
                    blinkTime += dt;
                    closed = Mathf.Sin(Mathf.PI * Mathf.Clamp01(blinkTime / 0.22f));
                    if (blinkTime > 0.22f) blinkTime = -1f;
                }
            }
            SetShape(blinkIndex, closed);
        }

        private void UpdateLook(float dt)
        {
            if (headBone == null) return;
            bool canLook = hasLookTarget && state != CharacterState.Sleeping;
            float k = 1f - Mathf.Exp(-lookResponsiveness * dt);
            lookWeight = Mathf.Lerp(lookWeight, canLook ? 1f : 0f, k);

            if (canLook)
            {
                // Additive rotation on top of the animated head pose, clamped to a comfortable range.
                Vector3 local = headBone.InverseTransformDirection(lookTarget - headBone.position);
                float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -maxHeadYaw, maxHeadYaw);
                float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -maxHeadPitch, maxHeadPitch);
                lookOffset = Quaternion.Slerp(lookOffset, Quaternion.Euler(pitch * 0.7f, yaw * 0.7f, 0f), k);
            }
            Quaternion applied = Quaternion.Slerp(Quaternion.identity, lookOffset, lookWeight);
            headBone.localRotation = Additive(headBone, 0, applied);

            if (eyeBones == null) return;
            for (int i = 0; i < eyeBones.Length; i++)
            {
                Transform eye = eyeBones[i];
                if (eye == null) continue;
                Quaternion eyeOffset = Quaternion.identity;
                if (canLook)
                {
                    Vector3 e = eye.InverseTransformDirection(lookTarget - eye.position);
                    float ey = Mathf.Clamp(Mathf.Atan2(e.x, e.z) * Mathf.Rad2Deg, -maxEyeAngle, maxEyeAngle);
                    float ep = Mathf.Clamp(-Mathf.Atan2(e.y, new Vector2(e.x, e.z).magnitude) * Mathf.Rad2Deg, -maxEyeAngle, maxEyeAngle);
                    eyeOffset = Quaternion.Euler(ep * lookWeight, ey * lookWeight, 0f);
                }
                eye.localRotation = Additive(eye, i + 1, eyeOffset);
            }
        }

        // Bones the Animator does not key would otherwise accumulate the offset every frame.
        // If the bone still holds what we wrote last frame, start again from the pose underneath.
        private Quaternion[] lastBase = new Quaternion[0];
        private Quaternion[] lastWritten = new Quaternion[0];

        private Quaternion Additive(Transform bone, int slot, Quaternion offset)
        {
            if (lastBase.Length <= slot)
            {
                Array.Resize(ref lastBase, slot + 1);
                Array.Resize(ref lastWritten, slot + 1);
            }
            Quaternion current = bone.localRotation;
            Quaternion baseRotation = current == lastWritten[slot] && lastBase[slot] != new Quaternion() ? lastBase[slot] : current;
            Quaternion result = baseRotation * offset;
            lastBase[slot] = baseRotation;
            lastWritten[slot] = result;
            return result;
        }

        private void SetShape(int index, float weight01)
        {
            if (index >= 0 && faceMesh != null) faceMesh.SetBlendShapeWeight(index, weight01 * 100f);
        }

        private void OnDisable()
        {
            if (faceMesh == null || faceMesh.sharedMesh == null) return;
            for (int i = 0; i < faceMesh.sharedMesh.blendShapeCount; i++) faceMesh.SetBlendShapeWeight(i, 0f);
        }
    }
}
