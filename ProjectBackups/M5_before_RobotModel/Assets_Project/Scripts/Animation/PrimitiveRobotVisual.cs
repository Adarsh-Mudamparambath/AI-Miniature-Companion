using UnityEngine;
using Mini.Core;

namespace Mini.Animation
{
    /// <summary>
    /// Procedural animation for the primitive prototype robot.
    ///
    /// All joints are assigned through the Inspector, so nothing here depends on GameObject names
    /// or on the hierarchy layout. A final Blender character gets its own ICharacterVisual
    /// implementation (for example one driving an Animator) and replaces this component.
    ///
    /// Animation is evaluated every frame from a single Update. There are no coroutines: each body
    /// channel (head, arm, eyes) holds at most one running gesture, so playing a new gesture simply
    /// replaces the old one and the smoothed pose blends between them without overlap or popping.
    ///
    /// Layers, evaluated in this order every frame:
    /// 1. State base pose (idle, listening, thinking, speaking, sleeping).
    /// 2. Emotion offsets (happy, sad, surprised, confused, curious, thinking).
    /// 3. Look-at (head and eyes follow a target).
    /// 4. One-shot gestures per channel.
    /// 5. Speech mouth (from lip sync) replaces the procedural speaking mouth when active.
    /// </summary>
    [DisallowMultipleComponent]
    public class PrimitiveRobotVisual : MonoBehaviour, ICharacterVisual, ICharacterFace
    {
        [Header("Joints (assign pivots)")]
        [SerializeField] private Transform bodyRoot;
        [SerializeField] private Transform headPivot;
        [SerializeField] private Transform leftArmPivot;
        [SerializeField] private Transform rightArmPivot;
        [SerializeField] private Transform leftElbowPivot;
        [SerializeField] private Transform rightElbowPivot;

        [Header("Face")]
        [SerializeField] private Transform[] eyes;
        [SerializeField] private Transform mouth;

        [Header("Tuning")]
        [SerializeField] private float poseResponsiveness = 14f;
        [SerializeField] private float eyeResponsiveness = 40f;
        [SerializeField] private bool autoBlink = true;
        [SerializeField] private Vector2 autoBlinkInterval = new Vector2(2.5f, 5f);

        [Header("Look At")]
        [SerializeField] private float maxHeadYaw = 40f;
        [SerializeField] private float maxHeadPitch = 22f;
        [SerializeField] private float lookResponsiveness = 5f;
        [Tooltip("How far the eyes move inside the face, as a fraction of the eye size.")]
        [SerializeField] private float eyeLookRange = 0.3f;

        private const float LookDuration = 1.4f;
        private const float NodDuration = 0.9f;
        private const float WaveDuration = 2.4f;
        private const float BlinkDuration = 0.22f;
        private const float ShakeDuration = 1.1f;
        private const float PointDuration = 2.0f;
        private const float ShrugDuration = 1.4f;

        private const float LookAngle = 35f;
        private const float NodAngle = 18f;
        private const float ShakeAngle = 16f;
        private const float WaveRaiseAngle = 125f;
        private const float WaveSwingAngle = 30f;
        private const float WaveSwingSpeed = 15f;

        private struct Channel
        {
            public bool active;
            public CharacterGesture gesture;
            public float time;
            public float duration;

            public float Normalized
            {
                get { return duration > 0f ? Mathf.Clamp01(time / duration) : 1f; }
            }

            public void Play(CharacterGesture newGesture, float newDuration)
            {
                active = true;
                gesture = newGesture;
                time = 0f;
                duration = newDuration;
            }

            public void Advance(float dt)
            {
                if (!active) return;
                time += dt;
                if (time >= duration) active = false;
            }

            public void Cancel()
            {
                active = false;
            }
        }

        private CharacterState state = CharacterState.Idle;
        private Channel headChannel;
        private Channel armChannel;
        private Channel eyeChannel;

        private float clock;
        private float autoBlinkTimer;

        // Rest pose captured at startup.
        private Quaternion headRest = Quaternion.identity;
        private Quaternion leftArmRest = Quaternion.identity;
        private Quaternion rightArmRest = Quaternion.identity;
        private Quaternion leftElbowRest = Quaternion.identity;
        private Quaternion rightElbowRest = Quaternion.identity;
        private Vector3 bodyRestPosition;
        private Vector3[] eyeRestScale;
        private Vector3[] eyeRestPosition;
        private Vector3 mouthRestScale;

        // Smoothed current pose.
        private Vector3 headEuler;
        private Vector3 leftArmEuler;
        private Vector3 rightArmEuler;
        private Vector3 leftElbowEuler;
        private Vector3 rightElbowEuler;
        private float bodyLift;
        private float eyeOpen = 1f;
        private float eyeAsymmetry;
        private Vector2 eyeOffset;
        private float mouthOpen;
        private float mouthWidth = 1f;

        // Face inputs.
        private CharacterEmotion emotion = CharacterEmotion.Neutral;
        private float emotionIntensity;
        private bool hasLookTarget;
        private Vector3 lookTarget;
        private float lookWeight;
        private float lookYaw;
        private float lookPitch;
        private bool speechMouthActive;
        private float speechOpenness;
        private Viseme speechViseme = Viseme.Rest;

        private void Awake()
        {
            CaptureRestPose();
            ResetAutoBlinkTimer();
        }

        private void OnDisable()
        {
            ResetToRest();
        }

        // Cancel everything and put every joint, eye and the mouth back at its rest pose,
        // so the eyes can never stay closed if the visual is disabled mid-blink.
        private void ResetToRest()
        {
            CancelGestures();

            headEuler = Vector3.zero;
            leftArmEuler = Vector3.zero;
            rightArmEuler = Vector3.zero;
            leftElbowEuler = Vector3.zero;
            rightElbowEuler = Vector3.zero;
            bodyLift = 0f;
            eyeOpen = 1f;
            eyeAsymmetry = 0f;
            eyeOffset = Vector2.zero;
            mouthOpen = 0f;
            mouthWidth = 1f;
            lookWeight = 0f;
            lookYaw = 0f;
            lookPitch = 0f;
            speechMouthActive = false;
            speechOpenness = 0f;

            if (eyeRestScale != null)
            {
                ApplyPose();
            }
        }

        private void CaptureRestPose()
        {
            if (headPivot != null) headRest = headPivot.localRotation;
            if (leftArmPivot != null) leftArmRest = leftArmPivot.localRotation;
            if (rightArmPivot != null) rightArmRest = rightArmPivot.localRotation;
            if (leftElbowPivot != null) leftElbowRest = leftElbowPivot.localRotation;
            if (rightElbowPivot != null) rightElbowRest = rightElbowPivot.localRotation;
            if (bodyRoot != null) bodyRestPosition = bodyRoot.localPosition;

            int eyeCount = eyes != null ? eyes.Length : 0;
            eyeRestScale = new Vector3[eyeCount];
            eyeRestPosition = new Vector3[eyeCount];
            for (int i = 0; i < eyeCount; i++)
            {
                eyeRestScale[i] = eyes[i] != null ? eyes[i].localScale : Vector3.one;
                eyeRestPosition[i] = eyes[i] != null ? eyes[i].localPosition : Vector3.zero;
            }

            if (mouth != null) mouthRestScale = mouth.localScale;
        }

        // ICharacterVisual

        public void SetState(CharacterState newState)
        {
            state = newState;
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            switch (gesture)
            {
                case CharacterGesture.LookLeft:
                case CharacterGesture.LookRight:
                    headChannel.Play(gesture, LookDuration);
                    break;
                case CharacterGesture.Nod:
                    headChannel.Play(gesture, NodDuration);
                    break;
                case CharacterGesture.ShakeHead:
                    headChannel.Play(gesture, ShakeDuration);
                    break;
                case CharacterGesture.Wave:
                    armChannel.Play(gesture, WaveDuration);
                    break;
                case CharacterGesture.PointLeft:
                case CharacterGesture.PointRight:
                    armChannel.Play(gesture, PointDuration);
                    break;
                case CharacterGesture.Shrug:
                    armChannel.Play(gesture, ShrugDuration);
                    break;
                case CharacterGesture.Blink:
                    eyeChannel.Play(gesture, BlinkDuration);
                    ResetAutoBlinkTimer();
                    break;
            }
        }

        public void CancelGestures()
        {
            headChannel.Cancel();
            armChannel.Cancel();
            eyeChannel.Cancel();
        }

        // ICharacterFace

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
            speechMouthActive = true;
            speechOpenness = Mathf.Clamp01(openness);
            speechViseme = viseme;
        }

        public void ReleaseSpeechMouth()
        {
            speechMouthActive = false;
            speechOpenness = 0f;
            speechViseme = Viseme.Rest;
        }

        // Frame update

        private void Update()
        {
            // Cap the step so a long frame (editor hitch, load spike) can never skip a short gesture entirely.
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            clock += dt;

            headChannel.Advance(dt);
            armChannel.Advance(dt);
            eyeChannel.Advance(dt);
            UpdateAutoBlink(dt);
            UpdateLook(dt);

            Vector3 headT = Vector3.zero;
            Vector3 leftArmT = Vector3.zero;
            Vector3 rightArmT = Vector3.zero;
            Vector3 leftElbowT = Vector3.zero;
            Vector3 rightElbowT = Vector3.zero;
            float bodyLiftT = 0f;
            float eyeOpenT = 1f;
            float mouthOpenT = 0f;
            float mouthWidthT = 1f;
            float eyeAsymT = 0f;

            EvaluateState(ref headT, ref leftArmT, ref rightArmT, ref leftElbowT, ref rightElbowT,
                ref bodyLiftT, ref eyeOpenT, ref mouthOpenT);
            EvaluateEmotion(ref headT, ref leftArmT, ref rightArmT, ref bodyLiftT, ref eyeOpenT,
                ref mouthOpenT, ref mouthWidthT, ref eyeAsymT);

            headT.y += lookYaw * lookWeight;
            headT.x += lookPitch * lookWeight;
            Vector2 eyeOffsetT = EyeLookOffset();

            EvaluateHeadGesture(ref headT, ref eyeOffsetT);
            EvaluateArmGesture(ref leftArmT, ref rightArmT, ref leftElbowT, ref rightElbowT, ref headT, ref bodyLiftT);
            eyeOpenT *= BlinkFactor();

            if (speechMouthActive)
            {
                mouthOpenT = speechOpenness;
                mouthWidthT *= VisemeWidth(speechViseme);
            }

            float k = 1f - Mathf.Exp(-poseResponsiveness * dt);
            float kEye = 1f - Mathf.Exp(-eyeResponsiveness * dt);

            headEuler = Vector3.Lerp(headEuler, headT, k);
            leftArmEuler = Vector3.Lerp(leftArmEuler, leftArmT, k);
            rightArmEuler = Vector3.Lerp(rightArmEuler, rightArmT, k);
            leftElbowEuler = Vector3.Lerp(leftElbowEuler, leftElbowT, k);
            rightElbowEuler = Vector3.Lerp(rightElbowEuler, rightElbowT, k);
            bodyLift = Mathf.Lerp(bodyLift, bodyLiftT, k);
            eyeOpen = Mathf.Lerp(eyeOpen, eyeOpenT, kEye);
            eyeAsymmetry = Mathf.Lerp(eyeAsymmetry, eyeAsymT, k);
            eyeOffset = Vector2.Lerp(eyeOffset, eyeOffsetT, kEye);
            mouthOpen = Mathf.Lerp(mouthOpen, mouthOpenT, kEye);
            mouthWidth = Mathf.Lerp(mouthWidth, mouthWidthT, kEye);

            ApplyPose();
        }

        private void EvaluateState(ref Vector3 head, ref Vector3 leftArm, ref Vector3 rightArm,
            ref Vector3 leftElbow, ref Vector3 rightElbow, ref float lift, ref float eye, ref float mouthT)
        {
            float t = clock;

            // Shared idle base: breathing bob, small arm sway, relaxed elbows, slow head drift.
            lift = Mathf.Sin(t * 1.6f) * 0.0012f;
            leftArm.z = -(3f + 1.5f * Mathf.Sin(t * 1.3f));
            rightArm.z = 3f + 1.5f * Mathf.Sin(t * 1.3f + 1f);
            leftElbow.x = -8f;
            rightElbow.x = -8f;
            head.y = 2.5f * Mathf.Sin(t * 0.6f);
            head.x = 1.5f * Mathf.Sin(t * 0.9f);

            switch (state)
            {
                case CharacterState.Listening:
                    head.z = -8f;
                    head.x = 3f;
                    eye = 1.15f;
                    lift += 0.0015f;
                    break;

                case CharacterState.Thinking:
                    head.z = 9f;
                    head.x = -9f;
                    head.y = 12f * Mathf.Sin(t * 0.8f);
                    eye = 0.8f;
                    rightArm.x = -55f;
                    rightArm.z = 6f;
                    rightElbow.x = -75f;
                    break;

                case CharacterState.Speaking:
                    mouthT = Mathf.PerlinNoise(t * 9f, 0.37f);
                    head.x += 2f * Mathf.Sin(t * 7f);
                    head.y += 3f * Mathf.Sin(t * 2.1f);
                    rightArm.z = 10f + 6f * Mathf.Sin(t * 3f);
                    rightElbow.x = -20f - 10f * Mathf.Sin(t * 3.4f);
                    leftArm.z = -(8f + 5f * Mathf.Sin(t * 2.6f));
                    leftElbow.x = -15f - 8f * Mathf.Sin(t * 2.9f + 1f);
                    break;

                case CharacterState.Sleeping:
                    // Head drops forward, eyes closed, slow deep breathing, arms hang.
                    lift = Mathf.Sin(t * 0.9f) * 0.002f - 0.002f;
                    head.x = 22f + 1.5f * Mathf.Sin(t * 0.9f);
                    head.y = 0f;
                    head.z = 6f;
                    eye = 0.03f;
                    leftArm.z = -1.5f;
                    rightArm.z = 1.5f;
                    leftElbow.x = -4f;
                    rightElbow.x = -4f;
                    break;
            }
        }

        private void EvaluateEmotion(ref Vector3 head, ref Vector3 leftArm, ref Vector3 rightArm, ref float lift,
            ref float eye, ref float mouthT, ref float mouthW, ref float eyeAsym)
        {
            if (state == CharacterState.Sleeping) return;

            float w = emotionIntensity;
            if (w <= 0.001f) return;
            float t = clock;

            switch (emotion)
            {
                case CharacterEmotion.Happy:
                    eye *= Mathf.Lerp(1f, 0.6f, w);               // smiling squint
                    mouthW *= Mathf.Lerp(1f, 1.35f, w);
                    mouthT = Mathf.Max(mouthT, 0.15f * w);
                    lift += 0.0025f * w * Mathf.Abs(Mathf.Sin(t * 4f)); // small happy bounce
                    head.z += 5f * w * Mathf.Sin(t * 1.7f);
                    break;

                case CharacterEmotion.Sad:
                    head.x += 14f * w;
                    eye *= Mathf.Lerp(1f, 0.6f, w);
                    mouthW *= Mathf.Lerp(1f, 0.75f, w);
                    leftArm.z += 2f * w;
                    rightArm.z -= 2f * w;
                    lift -= 0.002f * w;
                    break;

                case CharacterEmotion.Surprised:
                    eye *= Mathf.Lerp(1f, 1.35f, w);
                    mouthT = Mathf.Max(mouthT, 0.65f * w);
                    mouthW *= Mathf.Lerp(1f, 0.8f, w);
                    head.x -= 6f * w;
                    lift += 0.003f * w;
                    break;

                case CharacterEmotion.Confused:
                    head.z += 14f * w;
                    eyeAsym = 0.35f * w;                          // one eye narrows
                    mouthW *= Mathf.Lerp(1f, 0.85f, w);
                    break;

                case CharacterEmotion.Curious:
                    head.z -= 10f * w;
                    head.x += 4f * w;
                    eye *= Mathf.Lerp(1f, 1.15f, w);
                    break;

                case CharacterEmotion.Thinking:
                    head.x -= 6f * w;
                    eye *= Mathf.Lerp(1f, 0.85f, w);
                    mouthW *= Mathf.Lerp(1f, 0.8f, w);
                    break;
            }
        }

        private void UpdateLook(float dt)
        {
            bool canLook = hasLookTarget && headPivot != null && state != CharacterState.Sleeping;
            float targetWeight = canLook ? 1f : 0f;
            float k = 1f - Mathf.Exp(-lookResponsiveness * dt);
            lookWeight = Mathf.Lerp(lookWeight, targetWeight, k);

            if (!canLook) return;

            Vector3 dirWorld = lookTarget - headPivot.position;
            if (dirWorld.sqrMagnitude < 1e-6f) return;

            Transform parent = headPivot.parent;
            Vector3 dirParent = parent != null ? parent.InverseTransformDirection(dirWorld) : dirWorld;
            Vector3 local = Quaternion.Inverse(headRest) * dirParent;

            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;

            // Targets behind Mini: do not spin around, just look as far as allowed.
            yaw = Mathf.Clamp(yaw, -maxHeadYaw, maxHeadYaw);
            pitch = Mathf.Clamp(pitch, -maxHeadPitch, maxHeadPitch);

            lookYaw = Mathf.Lerp(lookYaw, yaw, k);
            lookPitch = Mathf.Lerp(lookPitch, pitch, k);
        }

        private Vector2 EyeLookOffset()
        {
            if (lookWeight <= 0.001f) return Vector2.zero;
            // Eyes lead the head slightly toward the target.
            float x = Mathf.Clamp(lookYaw / Mathf.Max(1f, maxHeadYaw), -1f, 1f);
            float y = Mathf.Clamp(-lookPitch / Mathf.Max(1f, maxHeadPitch), -1f, 1f);
            return new Vector2(x, y) * (0.6f * lookWeight);
        }

        private void EvaluateHeadGesture(ref Vector3 head, ref Vector2 eyeLook)
        {
            if (!headChannel.active) return;

            float n = headChannel.Normalized;
            switch (headChannel.gesture)
            {
                case CharacterGesture.LookLeft:
                    head.y += -LookAngle * Envelope(n);
                    eyeLook.x = Mathf.Lerp(eyeLook.x, -0.9f, Envelope(n));
                    break;
                case CharacterGesture.LookRight:
                    head.y += LookAngle * Envelope(n);
                    eyeLook.x = Mathf.Lerp(eyeLook.x, 0.9f, Envelope(n));
                    break;
                case CharacterGesture.Nod:
                    head.x += NodAngle * 0.5f * (1f - Mathf.Cos(4f * Mathf.PI * n));
                    break;
                case CharacterGesture.ShakeHead:
                    head.y += ShakeAngle * Mathf.Sin(6f * Mathf.PI * n) * Envelope(n);
                    break;
            }
        }

        private void EvaluateArmGesture(ref Vector3 leftArm, ref Vector3 rightArm, ref Vector3 leftElbow,
            ref Vector3 rightElbow, ref Vector3 head, ref float lift)
        {
            if (!armChannel.active) return;

            float env = Envelope(armChannel.Normalized);
            switch (armChannel.gesture)
            {
                case CharacterGesture.Wave:
                    rightArm.z = Mathf.Lerp(rightArm.z, WaveRaiseAngle, env);
                    rightArm.x = Mathf.Lerp(rightArm.x, 0f, env);
                    rightElbow.x = Mathf.Lerp(rightElbow.x, -10f, env);
                    rightElbow.z = WaveSwingAngle * Mathf.Sin(armChannel.time * WaveSwingSpeed) * env;
                    break;

                case CharacterGesture.PointRight:
                    // Right arm forward and out, head follows.
                    rightArm.x = Mathf.Lerp(rightArm.x, -80f, env);
                    rightArm.z = Mathf.Lerp(rightArm.z, 30f, env);
                    rightElbow.x = Mathf.Lerp(rightElbow.x, -5f, env);
                    head.y += 20f * env;
                    break;

                case CharacterGesture.PointLeft:
                    leftArm.x = Mathf.Lerp(leftArm.x, -80f, env);
                    leftArm.z = Mathf.Lerp(leftArm.z, -30f, env);
                    leftElbow.x = Mathf.Lerp(leftElbow.x, -5f, env);
                    head.y -= 20f * env;
                    break;

                case CharacterGesture.Shrug:
                    leftArm.z = Mathf.Lerp(leftArm.z, -28f, env);
                    rightArm.z = Mathf.Lerp(rightArm.z, 28f, env);
                    leftElbow.x = Mathf.Lerp(leftElbow.x, -70f, env);
                    rightElbow.x = Mathf.Lerp(rightElbow.x, -70f, env);
                    head.z += 8f * env;
                    lift += 0.004f * env;
                    break;
            }
        }

        private static float VisemeWidth(Viseme viseme)
        {
            switch (viseme)
            {
                case Viseme.EE: return 1.25f;
                case Viseme.IH: return 1.1f;
                case Viseme.OH: return 0.8f;
                case Viseme.OO: return 0.65f;
                case Viseme.FV: return 1.05f;
                default: return 1f;
            }
        }

        private float BlinkFactor()
        {
            if (!eyeChannel.active) return 1f;
            return 1f - 0.95f * Mathf.Sin(Mathf.PI * eyeChannel.Normalized);
        }

        private void UpdateAutoBlink(float dt)
        {
            if (!autoBlink || state == CharacterState.Sleeping) return;

            autoBlinkTimer -= dt;
            if (autoBlinkTimer <= 0f)
            {
                if (!eyeChannel.active)
                {
                    eyeChannel.Play(CharacterGesture.Blink, BlinkDuration);
                }
                ResetAutoBlinkTimer();
            }
        }

        private void ResetAutoBlinkTimer()
        {
            autoBlinkTimer = Random.Range(autoBlinkInterval.x, autoBlinkInterval.y);
        }

        private void ApplyPose()
        {
            ApplyRotation(headPivot, headRest, headEuler);
            ApplyRotation(leftArmPivot, leftArmRest, leftArmEuler);
            ApplyRotation(rightArmPivot, rightArmRest, rightArmEuler);
            ApplyRotation(leftElbowPivot, leftElbowRest, leftElbowEuler);
            ApplyRotation(rightElbowPivot, rightElbowRest, rightElbowEuler);

            if (bodyRoot != null)
            {
                bodyRoot.localPosition = bodyRestPosition + Vector3.up * bodyLift;
            }

            if (eyes != null)
            {
                for (int i = 0; i < eyes.Length; i++)
                {
                    if (eyes[i] == null) continue;
                    Vector3 s = eyeRestScale[i];
                    // The second eye narrows when confused.
                    float open = eyeOpen * (i == 1 ? 1f - eyeAsymmetry : 1f);
                    eyes[i].localScale = new Vector3(s.x, s.y * Mathf.Max(0.02f, open), s.z);
                    eyes[i].localPosition = eyeRestPosition[i] +
                        new Vector3(eyeOffset.x * s.x * eyeLookRange, eyeOffset.y * s.y * eyeLookRange, 0f);
                }
            }

            if (mouth != null)
            {
                mouth.localScale = new Vector3(mouthRestScale.x * mouthWidth,
                    mouthRestScale.y * (1f + 2.5f * mouthOpen), mouthRestScale.z);
            }
        }

        private static void ApplyRotation(Transform joint, Quaternion rest, Vector3 euler)
        {
            if (joint == null) return;
            joint.localRotation = rest * Quaternion.Euler(euler);
        }

        // Smooth rise, hold, smooth fall over normalized time 0..1.
        private static float Envelope(float n)
        {
            float rise = Mathf.SmoothStep(0f, 1f, n / 0.25f);
            float fall = Mathf.SmoothStep(0f, 1f, (1f - n) / 0.25f);
            return Mathf.Min(rise, fall);
        }
    }
}
