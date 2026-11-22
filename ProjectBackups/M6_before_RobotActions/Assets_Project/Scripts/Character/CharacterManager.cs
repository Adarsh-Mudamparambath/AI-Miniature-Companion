using System;
using UnityEngine;
using Mini.Core;

namespace Mini.Character
{
    /// <summary>
    /// Presents a <see cref="CompanionState"/> on the character: picks the looping body state, the
    /// emotion, the look target and occasional idle behaviour (glances, blinks, small gestures).
    ///
    /// It only talks to <see cref="MiniCharacterController"/> and the optional <see cref="ICharacterFace"/>,
    /// so it works with the primitive robot today and with the final Blender model later.
    /// It does not decide <i>when</i> states change; ConversationManager does.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterManager : MonoBehaviour
    {
        [SerializeField] private MiniCharacterController character;
        [Tooltip("The user's head (CenterEyeAnchor). Mini looks at this.")]
        [SerializeField] private Transform userHead;

        [Header("Idle life")]
        [SerializeField] private Vector2 glanceInterval = new Vector2(4f, 9f);
        [SerializeField] private Vector2 glanceDuration = new Vector2(0.8f, 1.8f);
        [Range(0f, 1f)] [SerializeField] private float idleGestureChance = 0.2f;

        private ICharacterFace face;
        private CharacterEmotion emotion = CharacterEmotion.Neutral;
        private float emotionIntensity;
        private float emotionHoldUntil;
        private float glanceTimer;
        private float glanceEnd;
        private Vector3 glanceOffset;

        public CompanionState State { get; private set; }
        public bool HasFace { get { return face != null; } }
        public event Action<CompanionState> StateChanged;

        private void Awake()
        {
            if (character == null) character = GetComponentInChildren<MiniCharacterController>(true);
            if (character == null) character = FindFirstObjectByType<MiniCharacterController>();
            ResolveFace();
            ResetGlanceTimer();
            State = CompanionState.Initialising;
        }

        public void ResolveFace()
        {
            face = character != null ? character.GetComponentInChildren<ICharacterFace>(true) : null;
        }

        public void SetUserHead(Transform head)
        {
            if (head != null) userHead = head;
        }

        public void SetState(CompanionState newState)
        {
            State = newState;
            if (character == null) return;

            switch (newState)
            {
                case CompanionState.Initialising:
                    character.SetState(CharacterState.Idle);
                    SetEmotion(CharacterEmotion.Neutral, 0f);
                    break;
                case CompanionState.WakeUp:
                    character.SetState(CharacterState.Idle);
                    character.PlayGesture(CharacterGesture.Blink);
                    SetEmotion(CharacterEmotion.Surprised, 0.5f, 0.6f);
                    break;
                case CompanionState.Idle:
                    character.SetState(CharacterState.Idle);
                    if (Time.time >= emotionHoldUntil) SetEmotion(CharacterEmotion.Neutral, 0f);
                    break;
                case CompanionState.Listening:
                    character.SetState(CharacterState.Listening);
                    SetEmotion(CharacterEmotion.Curious, 0.5f);
                    break;
                case CompanionState.Processing:
                    character.SetState(CharacterState.Thinking);
                    SetEmotion(CharacterEmotion.Thinking, 0.7f);
                    break;
                case CompanionState.Speaking:
                    character.SetState(CharacterState.Speaking);
                    break;
                case CompanionState.Reacting:
                    character.SetState(CharacterState.Idle);
                    break;
                case CompanionState.Sleeping:
                    character.CancelGestures();
                    character.SetState(CharacterState.Sleeping);
                    SetEmotion(CharacterEmotion.Neutral, 0f);
                    break;
                case CompanionState.Error:
                    character.SetState(CharacterState.Idle);
                    SetEmotion(CharacterEmotion.Confused, 0.8f, 2f);
                    character.PlayGesture(CharacterGesture.Shrug);
                    break;
            }

            if (StateChanged != null) StateChanged(newState);
        }

        /// <summary>Apply a validated cue: emotion (held for a few seconds) and an optional gesture.</summary>
        public void ApplyCue(BehaviourCue cue, float holdSeconds = 4f)
        {
            SetEmotion(cue.Emotion, cue.Intensity, holdSeconds);
            if (cue.HasGesture && character != null) character.PlayGesture(cue.Gesture);
        }

        public void PlayGesture(CharacterGesture gesture)
        {
            if (character != null) character.PlayGesture(gesture);
        }

        private void SetEmotion(CharacterEmotion newEmotion, float intensity, float holdSeconds = 0f)
        {
            emotion = newEmotion;
            emotionIntensity = intensity;
            emotionHoldUntil = Time.time + holdSeconds;
            if (face != null) face.SetEmotion(newEmotion, intensity);
        }

        private void Update()
        {
            // Let a held reaction emotion fade back to neutral once its time is up.
            if (emotionIntensity > 0f && Time.time > emotionHoldUntil &&
                (State == CompanionState.Idle || State == CompanionState.Reacting) &&
                emotion != CharacterEmotion.Neutral)
            {
                SetEmotion(CharacterEmotion.Neutral, 0f);
            }

            UpdateLook();
        }

        private void UpdateLook()
        {
            if (face == null) return;

            if (State == CompanionState.Sleeping || userHead == null)
            {
                face.SetLookTarget(null);
                return;
            }

            Vector3 target = userHead.position;

            if (State == CompanionState.Processing)
            {
                // Thinking: look up and to the side, away from the user.
                target += userHead.right * 0.35f + Vector3.up * 0.3f;
            }
            else if (State == CompanionState.Idle)
            {
                UpdateIdleGlance(ref target);
            }

            face.SetLookTarget(target);
        }

        private void UpdateIdleGlance(ref Vector3 target)
        {
            float now = Time.time;
            if (now < glanceEnd)
            {
                target += glanceOffset;
                return;
            }

            glanceTimer -= Time.deltaTime;
            if (glanceTimer > 0f) return;

            ResetGlanceTimer();
            glanceEnd = now + UnityEngine.Random.Range(glanceDuration.x, glanceDuration.y);
            glanceOffset = userHead.right * UnityEngine.Random.Range(-0.7f, 0.7f) +
                           Vector3.up * UnityEngine.Random.Range(-0.35f, 0.25f);

            if (character != null && UnityEngine.Random.value < idleGestureChance)
            {
                // Occasional small sign of life; never a big gesture while idle.
                character.PlayGesture(UnityEngine.Random.value < 0.5f ? CharacterGesture.Blink : CharacterGesture.Nod);
            }
        }

        private void ResetGlanceTimer()
        {
            glanceTimer = UnityEngine.Random.Range(glanceInterval.x, glanceInterval.y);
        }
    }
}
