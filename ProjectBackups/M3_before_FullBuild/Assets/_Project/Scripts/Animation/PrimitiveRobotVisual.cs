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
    /// </summary>
    [DisallowMultipleComponent]
    public class PrimitiveRobotVisual : MonoBehaviour, ICharacterVisual
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

        private const float LookDuration = 1.4f;
        private const float NodDuration = 0.9f;
        private const float WaveDuration = 2.4f;
        private const float BlinkDuration = 0.22f;

        private const float LookAngle = 35f;
        private const float NodAngle = 18f;
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
        private Vector3 mouthRestScale;

        // Smoothed current pose.
        private Vector3 headEuler;
        private Vector3 leftArmEuler;
        private Vector3 rightArmEuler;
        private Vector3 leftElbowEuler;
        private Vector3 rightElbowEuler;
        private float bodyLift;
        private float eyeOpen = 1f;
        private float mouthOpen;

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
            mouthOpen = 0f;

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
            for (int i = 0; i < eyeCount; i++)
            {
                eyeRestScale[i] = eyes[i] != null ? eyes[i].localScale : Vector3.one;
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
                case CharacterGesture.Wave:
                    armChannel.Play(gesture, WaveDuration);
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

            Vector3 headT = Vector3.zero;
            Vector3 leftArmT = Vector3.zero;
            Vector3 rightArmT = Vector3.zero;
            Vector3 leftElbowT = Vector3.zero;
            Vector3 rightElbowT = Vector3.zero;
            float bodyLiftT = 0f;
            float eyeOpenT = 1f;
            float mouthOpenT = 0f;

            EvaluateState(ref headT, ref leftArmT, ref rightArmT, ref leftElbowT, ref rightElbowT,
                ref bodyLiftT, ref eyeOpenT, ref mouthOpenT);
            EvaluateHeadGesture(ref headT);
            EvaluateArmGesture(ref rightArmT, ref rightElbowT);
            eyeOpenT *= BlinkFactor();

            float k = 1f - Mathf.Exp(-poseResponsiveness * dt);
            float kEye = 1f - Mathf.Exp(-eyeResponsiveness * dt);

            headEuler = Vector3.Lerp(headEuler, headT, k);
            leftArmEuler = Vector3.Lerp(leftArmEuler, leftArmT, k);
            rightArmEuler = Vector3.Lerp(rightArmEuler, rightArmT, k);
            leftElbowEuler = Vector3.Lerp(leftElbowEuler, leftElbowT, k);
            rightElbowEuler = Vector3.Lerp(rightElbowEuler, rightElbowT, k);
            bodyLift = Mathf.Lerp(bodyLift, bodyLiftT, k);
            eyeOpen = Mathf.Lerp(eyeOpen, eyeOpenT, kEye);
            mouthOpen = Mathf.Lerp(mouthOpen, mouthOpenT, kEye);

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
            }
        }

        private void EvaluateHeadGesture(ref Vector3 head)
        {
            if (!headChannel.active) return;

            float n = headChannel.Normalized;
            switch (headChannel.gesture)
            {
                case CharacterGesture.LookLeft:
                    head.y += -LookAngle * Envelope(n);
                    break;
                case CharacterGesture.LookRight:
                    head.y += LookAngle * Envelope(n);
                    break;
                case CharacterGesture.Nod:
                    head.x += NodAngle * 0.5f * (1f - Mathf.Cos(4f * Mathf.PI * n));
                    break;
            }
        }

        private void EvaluateArmGesture(ref Vector3 rightArm, ref Vector3 rightElbow)
        {
            if (!armChannel.active || armChannel.gesture != CharacterGesture.Wave) return;

            float env = Envelope(armChannel.Normalized);
            rightArm.z = Mathf.Lerp(rightArm.z, WaveRaiseAngle, env);
            rightArm.x = Mathf.Lerp(rightArm.x, 0f, env);
            rightElbow.x = Mathf.Lerp(rightElbow.x, -10f, env);
            rightElbow.z = WaveSwingAngle * Mathf.Sin(armChannel.time * WaveSwingSpeed) * env;
        }

        private float BlinkFactor()
        {
            if (!eyeChannel.active) return 1f;
            return 1f - 0.95f * Mathf.Sin(Mathf.PI * eyeChannel.Normalized);
        }

        private void UpdateAutoBlink(float dt)
        {
            if (!autoBlink) return;

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
                    eyes[i].localScale = new Vector3(s.x, s.y * Mathf.Max(0.02f, eyeOpen), s.z);
                }
            }

            if (mouth != null)
            {
                mouth.localScale = new Vector3(mouthRestScale.x, mouthRestScale.y * (1f + 2.5f * mouthOpen), mouthRestScale.z);
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
