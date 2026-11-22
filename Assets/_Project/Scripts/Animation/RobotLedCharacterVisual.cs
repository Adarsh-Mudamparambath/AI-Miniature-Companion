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
    ///
    /// On top of the clips it adds code-driven life:
    /// - the whole body turns to face the user (with a little walk while turning), the head follows them;
    /// - while speaking, random hand gestures and a head bob that follows the voice;
    /// - full-body actions: Jump, Dance (Dance.anim made in Unity), Spin and WalkAround.
    /// This object's own local position/rotation are owned by this script (keep its parent as the anchor).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // run before RobotFace.LateUpdate so the face frame is set first
    public class RobotLedCharacterVisual : MonoBehaviour, ICharacterVisual, ICharacterFace
    {
        private enum BodyAction { None, Jump, Dance, Spin, WalkAround }

        [SerializeField] private Animator animator;
        [SerializeField] private RobotFace face;
        [SerializeField] private Transform headBone;
        [Tooltip("What the body turns toward. Empty = the main camera (the user's head).")]
        [SerializeField] private Transform bodyTarget;

        [Header("Looping clip per state")]
        [SerializeField] private string idleClip = "Idle";
        [SerializeField] private string listeningClip = "Idle";
        [SerializeField] private string thinkingClip = "Look_Around";
        [SerializeField] private string speakingClip = "Talk";
        [SerializeField] private string sleepingClip = "Idle";
        [SerializeField] private string walkClip = "Walk";
        [SerializeField] private float sleepingAnimSpeed = 0.35f;

        [Header("Facing the user")]
        [SerializeField] private float turnStartAngle = 20f;
        [SerializeField] private float turnStopAngle = 4f;
        [SerializeField] private float turnSpeed = 140f;

        [Header("Talking")]
        [SerializeField] private Vector2 talkGestureInterval = new Vector2(1.8f, 3.2f);
        [SerializeField] private string[] talkGestureClips = { "Welcome", "Nod_Yes", "Talk", "Welcome", "Point_Right", "Point_Left" };
        [SerializeField] private float speechHeadBob = 8f;

        [Header("Actions")]
        [SerializeField] private float jumpHeight = 0.07f;
        [SerializeField] private float walkRadius = 0.08f;

        [Header("Tuning")]
        [SerializeField] private float crossFade = 0.25f;
        [SerializeField] private float maxHeadYaw = 45f;
        [SerializeField] private float maxHeadPitch = 25f;
        [SerializeField] private float lookResponsiveness = 6f;
        [Tooltip("Speech loudness above which the open-mouth face frame is shown.")]
        [SerializeField] private float mouthOpenThreshold = 0.35f;
        [SerializeField] private float blinkLength = 0.15f;

        private CharacterState state;
        private CharacterEmotion emotion = CharacterEmotion.Happy;
        private float emotionIntensity = 1f;
        private bool speechActive;
        private float speechOpen;
        private float headBob;

        private string currentClip;
        private string gestureClip;
        private float gestureTimeLeft;
        private float talkTimer;
        private float forcedBlinkLeft;

        private BodyAction action;
        private float actionTime;
        private float actionDuration;

        private float bodyYaw;
        private bool turning;

        private bool hasLookTarget;
        private Vector3 lookTarget;
        private float lookWeight;
        private Quaternion lookOffset = Quaternion.identity;
        private Quaternion lastBase;
        private Quaternion lastWritten;
        private bool hasLast;

        public bool IsBusy { get { return action != BodyAction.None || !string.IsNullOrEmpty(gestureClip); } }

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            if (animator != null) animator.applyRootMotion = false;
            if (face == null) face = GetComponentInChildren<RobotFace>();
            bodyYaw = transform.localEulerAngles.y;
            ResetTalkTimer();
        }

        // ---------------------------------------------------------------- ICharacterVisual

        public void SetState(CharacterState newState)
        {
            state = newState;
            if (state == CharacterState.Sleeping) StopAll();
            if (!IsBusy && !turning) PlayStateClip();
            if (state == CharacterState.Speaking) talkTimer = 0.6f; // first hand gesture soon after starting to talk
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            if (gesture == CharacterGesture.Blink) { forcedBlinkLeft = blinkLength; return; }
            if (state == CharacterState.Sleeping) return;

            switch (gesture)
            {
                case CharacterGesture.Jump: StartAction(BodyAction.Jump, 1.7f, "Celebrate"); return;
                case CharacterGesture.Dance: StartAction(BodyAction.Dance, 4f, HasClip("Dance") ? "Dance" : "Celebrate"); return;
                case CharacterGesture.Spin: StartAction(BodyAction.Spin, 1.6f, walkClip); return;
                case CharacterGesture.WalkAround: StartAction(BodyAction.WalkAround, 6f, walkClip); return;
            }

            string clip = GestureClip(gesture);
            if (clip == null || !HasClip(clip)) return;
            action = BodyAction.None;
            PlayClipAsGesture(clip, ClipLength(clip));
        }

        public void CancelGestures()
        {
            if (!IsBusy) return;
            StopAll();
            PlayStateClip();
        }

        // ---------------------------------------------------------------- ICharacterFace

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
            speechOpen = 0f;
        }

        // ---------------------------------------------------------------- clips

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
                case CharacterGesture.Shrug: return "Look_Around";
                case CharacterGesture.Bow: return "Bow";
                case CharacterGesture.Celebrate: return "Celebrate";
                case CharacterGesture.Welcome: return "Welcome";
                case CharacterGesture.Beckon: return "Beckon";
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

        private bool HasClip(string clip)
        {
            return animator != null && animator.runtimeAnimatorController != null &&
                   animator.HasState(0, Animator.StringToHash(clip));
        }

        private void CrossFadeTo(string clip, bool restart)
        {
            if (!HasClip(clip)) return;
            if (!restart && clip == currentClip) return;
            currentClip = clip;
            if (restart) animator.CrossFadeInFixedTime(Animator.StringToHash(clip), crossFade, 0, 0f);
            else animator.CrossFadeInFixedTime(Animator.StringToHash(clip), crossFade, 0);
        }

        private void PlayStateClip()
        {
            if (animator == null) return;
            animator.speed = state == CharacterState.Sleeping ? sleepingAnimSpeed : 1f;
            CrossFadeTo(StateClip(), false);
        }

        private void PlayClipAsGesture(string clip, float duration)
        {
            gestureClip = clip;
            gestureTimeLeft = Mathf.Max(0.3f, duration - crossFade);
            if (animator != null) animator.speed = 1f;
            CrossFadeTo(clip, true);
        }

        private float ClipLength(string clipName)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return 1f;
            foreach (var c in animator.runtimeAnimatorController.animationClips)
                if (c != null && c.name == clipName) return c.length;
            return 1f;
        }

        private void StartAction(BodyAction newAction, float duration, string clip)
        {
            action = newAction;
            actionTime = 0f;
            actionDuration = duration;
            turning = false;
            PlayClipAsGesture(HasClip(clip) ? clip : idleClip, duration);
        }

        private void StopAll()
        {
            action = BodyAction.None;
            gestureClip = null;
        }

        private void ResetTalkTimer()
        {
            talkTimer = Random.Range(talkGestureInterval.x, talkGestureInterval.y);
        }

        // ---------------------------------------------------------------- per frame

        private void Update()
        {
            float dt = Time.deltaTime;

            if (!string.IsNullOrEmpty(gestureClip))
            {
                gestureTimeLeft -= dt;
                if (gestureTimeLeft <= 0f)
                {
                    StopAll();
                    if (!turning) PlayStateClip();
                }
            }

            UpdateTalkGestures(dt);
        }

        private void UpdateTalkGestures(float dt)
        {
            // Only while speaking, and never on top of a running gesture or action.
            if (state != CharacterState.Speaking || IsBusy) return;

            talkTimer -= dt;
            if (talkTimer > 0f) return;
            ResetTalkTimer();

            if (talkGestureClips == null || talkGestureClips.Length == 0) return;
            string clip = talkGestureClips[Random.Range(0, talkGestureClips.Length)];
            if (clip == speakingClip || !HasClip(clip)) { CrossFadeTo(speakingClip, false); return; }
            // Play only part of long clips so the hands keep changing while talking.
            PlayClipAsGesture(clip, Mathf.Min(ClipLength(clip), 1.8f));
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (forcedBlinkLeft > 0f) forcedBlinkLeft -= dt;

            UpdateBody(dt);
            UpdateFace();
            UpdateLook(dt);
        }

        private void UpdateFace()
        {
            if (face == null) return;
            face.autoBlink = state != CharacterState.Sleeping && !speechActive && action == BodyAction.None;
            face.emotionIndex = (int)FaceFrame();
        }

        private RobotFace.Emotion FaceFrame()
        {
            if (state == CharacterState.Sleeping) return RobotFace.Emotion.Sleepy;
            if (forcedBlinkLeft > 0f) return RobotFace.Emotion.Blink;
            if (action != BodyAction.None) return RobotFace.Emotion.Happy;
            if (speechActive && speechOpen > mouthOpenThreshold) return RobotFace.Emotion.Surprised;
            if (emotionIntensity < 0.3f) return state == CharacterState.Speaking ? RobotFace.Emotion.Happy : RobotFace.Emotion.Neutral;
            switch (emotion)
            {
                case CharacterEmotion.Happy: return RobotFace.Emotion.Happy;
                case CharacterEmotion.Sad: return RobotFace.Emotion.Sad;
                case CharacterEmotion.Surprised:
                case CharacterEmotion.Curious: return RobotFace.Emotion.Surprised;
                case CharacterEmotion.Love: return RobotFace.Emotion.Love;
                case CharacterEmotion.Angry: return RobotFace.Emotion.Angry;
                default: return RobotFace.Emotion.Neutral; // Neutral, Confused, Thinking
            }
        }

        // ---------------------------------------------------------------- body: facing + actions

        private void UpdateBody(float dt)
        {
            Vector3 offset = Vector3.zero;
            float actionYaw = 0f;
            float roll = 0f;

            if (action != BodyAction.None)
            {
                actionTime += dt;
                float t = Mathf.Clamp01(actionTime / actionDuration);
                switch (action)
                {
                    case BodyAction.Jump:
                    {
                        // Two hops.
                        float hop = Mathf.Abs(Mathf.Sin(Mathf.PI * 2f * t));
                        offset.y = jumpHeight * hop * hop;
                        break;
                    }
                    case BodyAction.Dance:
                    {
                        // The arms/legs come from the Dance clip (one beat every 0.5 s).
                        // Here only a soft bounce on each beat and a slow left-right turn.
                        float time = actionTime;
                        float fade = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 6f);
                        offset.y = Mathf.Abs(Mathf.Sin(time * Mathf.PI * 2f)) * 0.012f * fade;
                        actionYaw = Mathf.Sin(time * Mathf.PI) * 15f * fade;
                        break;
                    }
                    case BodyAction.Spin:
                    {
                        float e = t * t * (3f - 2f * t);
                        actionYaw = 360f * e;
                        offset.y = Mathf.Sin(Mathf.PI * t) * 0.02f;
                        break;
                    }
                    case BodyAction.WalkAround:
                    {
                        // A small circle on the table that starts and ends where Mini stands.
                        float e = t * t * (3f - 2f * t);
                        float a = Mathf.PI * 2f * e;
                        Quaternion facing = Quaternion.Euler(0f, bodyYaw, 0f);
                        Vector3 local = new Vector3(walkRadius * (1f - Mathf.Cos(a)), 0f, walkRadius * Mathf.Sin(a));
                        offset = facing * local;
                        // Face along the path, blending in and out at the ends.
                        float pathYaw = Mathf.Rad2Deg * a;
                        float blend = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 6f);
                        actionYaw = Mathf.LerpAngle(0f, pathYaw, blend);
                        break;
                    }
                }
                if (actionTime >= actionDuration) action = BodyAction.None;
            }
            else
            {
                UpdateFacing(dt);
            }

            transform.localPosition = offset;
            transform.localRotation = Quaternion.Euler(0f, bodyYaw + actionYaw, roll);
        }

        private void UpdateFacing(float dt)
        {
            if (state == CharacterState.Sleeping) { turning = false; return; }
            Transform target = bodyTarget;
            if (target == null && Camera.main != null) target = Camera.main.transform;
            if (target == null || transform.parent == null) return;

            Vector3 local = transform.parent.InverseTransformPoint(target.position);
            local.y = 0f;
            if (local.sqrMagnitude < 0.0004f) return;
            float wanted = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float diff = Mathf.DeltaAngle(bodyYaw, wanted);

            if (!turning && Mathf.Abs(diff) > turnStartAngle)
            {
                turning = true;
                if (!IsBusy && (state == CharacterState.Idle || state == CharacterState.Listening)) CrossFadeTo(walkClip, false);
            }
            if (!turning) return;

            bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, wanted, turnSpeed * dt);
            if (Mathf.Abs(Mathf.DeltaAngle(bodyYaw, wanted)) <= turnStopAngle)
            {
                turning = false;
                if (!IsBusy) PlayStateClip();
            }
        }

        // ---------------------------------------------------------------- head look-at

        private void UpdateLook(float dt)
        {
            if (headBone == null) return;
            bool canLook = hasLookTarget && state != CharacterState.Sleeping && action == BodyAction.None;
            float k = 1f - Mathf.Exp(-lookResponsiveness * dt);
            lookWeight = Mathf.Lerp(lookWeight, canLook ? 1f : 0f, k);

            if (canLook)
            {
                Vector3 local = transform.InverseTransformDirection(lookTarget - headBone.position);
                float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -maxHeadYaw, maxHeadYaw);
                float pitch = Mathf.Clamp(-Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg, -maxHeadPitch, maxHeadPitch);
                lookOffset = Quaternion.Slerp(lookOffset, Quaternion.Euler(pitch, yaw, 0f), k);
            }

            // Little nods that follow the voice while speaking.
            float bobTarget = speechActive ? speechOpen * speechHeadBob : 0f;
            headBob = Mathf.Lerp(headBob, bobTarget, 1f - Mathf.Exp(-14f * dt));

            Quaternion look = Quaternion.Slerp(Quaternion.identity, lookOffset, lookWeight);

            // If the Animator did not key the head this frame, start from last frame's base pose
            // so the offset does not pile up.
            Quaternion current = headBone.localRotation;
            Quaternion baseRotation = hasLast && current == lastWritten ? lastBase : current;

            // Apply the look in the body's space, so it works whatever the head bone's own axes are.
            Quaternion parentWorld = headBone.parent != null ? headBone.parent.rotation : Quaternion.identity;
            Quaternion baseWorld = parentWorld * baseRotation;
            Quaternion body = transform.rotation;
            Quaternion lookWorld = body * look * Quaternion.Euler(headBob, 0f, 0f) * Quaternion.Inverse(body);
            Quaternion result = Quaternion.Inverse(parentWorld) * (lookWorld * baseWorld);

            lastBase = baseRotation;
            lastWritten = result;
            hasLast = true;
            headBone.localRotation = result;
        }
    }
}
