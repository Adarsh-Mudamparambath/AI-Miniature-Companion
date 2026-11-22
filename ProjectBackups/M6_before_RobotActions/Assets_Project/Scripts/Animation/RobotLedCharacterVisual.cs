using System;
using UnityEngine;
using Mini.Core;

namespace Mini.Animation
{
    /// <summary>
    /// Adapter for the Robot.fbx model (Generic rig, LED face texture driven by <see cref="RobotFace"/>).
    /// Put it on the robot next to its Animator and RobotFace. Everything above it
    /// (MiniCharacterController, CharacterManager, ConversationManager, LipSyncController) stays the same.
    ///
    /// The Animator Controller only needs one state per clip, named exactly like the clip
    /// (Idle, Thinking, Talk, Wave, Nod_Yes, ...). No parameters or transitions are needed:
    /// this script cross-fades between states by name.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // run before RobotFace.LateUpdate so the face frame is set first
    public class RobotLedCharacterVisual : MonoBehaviour, ICharacterVisual, ICharacterFace
    {
        [SerializeField] private Animator animator;
        [SerializeField] private RobotFace face;
        [SerializeField] private Transform headBone;

        [Header("Looping clip per state")]
        [SerializeField] private string idleClip = "Idle";
        [SerializeField] private string listeningClip = "Idle";
        [SerializeField] private string thinkingClip = "Thinking";
        [SerializeField] private string speakingClip = "Talk";
        [SerializeField] private string sleepingClip = "Idle";
        [SerializeField] private float sleepingAnimSpeed = 0.35f;

        [Header("Tuning")]
        [SerializeField] private float crossFade = 0.25f;
        [SerializeField] private float maxHeadYaw = 40f;
        [SerializeField] private float maxHeadPitch = 22f;
        [SerializeField] private float lookResponsiveness = 5f;
        [Tooltip("Speech loudness above which the open-mouth face frame is shown.")]
        [SerializeField] private float mouthOpenThreshold = 0.35f;
        [SerializeField] private float blinkLength = 0.15f;

        private CharacterState state;
        private CharacterEmotion emotion = CharacterEmotion.Happy;
        private float emotionIntensity = 1f;
        private bool speechActive;
        private float speechOpen;
        private string gestureClip;
        private float gestureTimeLeft;
        private float forcedBlinkLeft;
        private bool hasLookTarget;
        private Vector3 lookTarget;
        private float lookWeight;
        private Quaternion lookOffset = Quaternion.identity;
        private Quaternion lastBase;
        private Quaternion lastWritten;
        private bool hasLast;

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = false;
            if (face == null) face = GetComponentInChildren<RobotFace>();
        }

        public void SetState(CharacterState newState)
        {
            state = newState;
            if (string.IsNullOrEmpty(gestureClip)) PlayStateClip();
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            if (gesture == CharacterGesture.Blink) { forcedBlinkLeft = blinkLength; return; }
            string clip = GestureClip(gesture);
            if (clip == null || animator == null) return;
            int hash = Animator.StringToHash(clip);
            if (!animator.HasState(0, hash)) return;
            gestureClip = clip;
            animator.speed = 1f;
            animator.CrossFadeInFixedTime(hash, crossFade, 0, 0f);
            gestureTimeLeft = ClipLength(clip) - crossFade;
        }

        public void CancelGestures()
        {
            if (string.IsNullOrEmpty(gestureClip)) return;
            gestureClip = null;
            PlayStateClip();
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
        }

        public void ReleaseSpeechMouth()
        {
            speechActive = false;
        }

        private static string GestureClip(CharacterGesture g)
        {
            switch (g)
            {
                case CharacterGesture.LookLeft:
                case CharacterGesture.LookRight: return "Look_Around";
                case CharacterGesture.Nod: return "Nod_Yes";
                case CharacterGesture.Wave: return "Wave";
                case CharacterGesture.ShakeHead: return "Shake_No";
                case CharacterGesture.PointLeft: return "Point_Left";
                case CharacterGesture.PointRight: return "Point_Right";
                case CharacterGesture.Shrug: return "Thinking";
                default: return null;
            }
        }

        private string StateClip()
        {
            switch (state)
            {
                case CharacterState.Listening: return listeningClip;
                case CharacterState.Thinking: return thinkingClip;
                case CharacterState.Speaking: return speakingClip;
                case CharacterState.Sleeping: return sleepingClip;
                default: return idleClip;
            }
        }

        private void PlayStateClip()
        {
            if (animator == null || animator.runtimeAnimatorController == null) return;
            int hash = Animator.StringToHash(StateClip());
            if (!animator.HasState(0, hash)) return;
            animator.speed = state == CharacterState.Sleeping ? sleepingAnimSpeed : 1f;
            animator.CrossFadeInFixedTime(hash, crossFade, 0);
        }

        private float ClipLength(string clipName)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return 1f;
            foreach (var c in animator.runtimeAnimatorController.animationClips)
                if (c != null && c.name == clipName) return c.length;
            return 1f;
        }

        private RobotFace.Emotion FaceFrame()
        {
            if (state == CharacterState.Sleeping) return RobotFace.Emotion.Sleepy;
            if (forcedBlinkLeft > 0f) return RobotFace.Emotion.Blink;
            if (speechActive && speechOpen > mouthOpenThreshold) return RobotFace.Emotion.Surprised;
            if (emotionIntensity < 0.3f) return RobotFace.Emotion.Neutral;
            switch (emotion)
            {
                case CharacterEmotion.Happy: return RobotFace.Emotion.Happy;
                case CharacterEmotion.Sad: return RobotFace.Emotion.Sad;
                case CharacterEmotion.Surprised:
                case CharacterEmotion.Curious: return RobotFace.Emotion.Surprised;
                default: return RobotFace.Emotion.Neutral; // Neutral, Confused, Thinking
            }
        }

        private void Update()
        {
            if (!string.IsNullOrEmpty(gestureClip))
            {
                gestureTimeLeft -= Time.deltaTime;
                if (gestureTimeLeft <= 0f) { gestureClip = null; PlayStateClip(); }
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (forcedBlinkLeft > 0f) forcedBlinkLeft -= dt;
            if (face != null)
            {
                RobotFace.Emotion frame = FaceFrame();
                face.autoBlink = state != CharacterState.Sleeping && !speechActive;
                face.emotionIndex = (int)frame;
            }
            UpdateLook(dt);
        }

        private void UpdateLook(float dt)
        {
            if (headBone == null) return;
            bool canLook = hasLookTarget && state != CharacterState.Sleeping;
            float k = 1f - Mathf.Exp(-lookResponsiveness * dt);
            lookWeight = Mathf.Lerp(lookWeight, canLook ? 1f : 0f, k);

            if (canLook)
            {
                Vector3 local = headBone.InverseTransformDirection(lookTarget - headBone.position);
                float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -maxHeadYaw, maxHeadYaw);
                float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -maxHeadPitch, maxHeadPitch);
                lookOffset = Quaternion.Slerp(lookOffset, Quaternion.Euler(pitch * 0.7f, yaw * 0.7f, 0f), k);
            }
            Quaternion applied = Quaternion.Slerp(Quaternion.identity, lookOffset, lookWeight);

            // If the Animator did not key the head this frame, start from last frame's base pose
            // so the offset does not pile up.
            Quaternion current = headBone.localRotation;
            Quaternion baseRotation = hasLast && current == lastWritten ? lastBase : current;
            Quaternion result = baseRotation * applied;
            lastBase = baseRotation;
            lastWritten = result;
            hasLast = true;
            headBone.localRotation = result;
        }
    }
}
