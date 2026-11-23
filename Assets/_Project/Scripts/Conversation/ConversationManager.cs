using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Mini.App;
using Mini.Character;
using Mini.Core;
using Mini.MR;
using Mini.Services;
using Mini.Voice;

namespace Mini.Conversation
{
    /// <summary>
    /// The conversation loop and owner of <see cref="CompanionState"/>:
    ///
    /// Initialising → WakeUp → Idle ⇄ Listening → Processing → Speaking → Reacting → Idle
    ///                         Idle → Sleeping (after a quiet period) → Listening when spoken to
    ///                         any failure → Error → Idle
    ///
    /// Exactly one turn runs at a time, each with its own cancellation token. Starting a new turn,
    /// interrupting, pausing the app or disabling this component cancels the running turn, stops audio
    /// and releases the clip, so audio never overlaps.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConversationManager : MonoBehaviour
    {
        [SerializeField] private CharacterManager character;
        [SerializeField] private VoiceManager voice;
        [SerializeField] private AudioSource voiceSource;
        [SerializeField] private PlacementManager placement;

        private MiniSettings settings;
        private ISpeechToTextService stt;
        private IAIConversationService ai;
        private ITextToSpeechService tts;
        private ConversationHistory history;

        private CancellationTokenSource lifetimeCts;
        private CancellationTokenSource turnCts;
        private AudioClip currentClip;
        private float idleTimer;
        private int notUnderstoodCount;
        private bool initialized;

        public CompanionState State { get; private set; }
        public string LastUserText { get; private set; }
        public string LastMiniText { get; private set; }
        public ConversationHistory History { get { return history; } }
        public string ServiceSummary { get; private set; }

        public event Action<CompanionState> StateChanged;
        /// <summary>Short status line for the HUD (empty to clear).</summary>
        public event Action<string> StatusMessage;
        /// <summary>(speaker, text) for subtitles. Speaker is "You" or "Mini".</summary>
        public event Action<string, string> Subtitle;

        public void Initialize(MiniSettings appSettings, ISpeechToTextService speechToText,
            IAIConversationService conversation, ITextToSpeechService textToSpeech)
        {
            settings = appSettings;
            stt = speechToText;
            ai = conversation;
            tts = textToSpeech;
            history = new ConversationHistory(settings.maxHistoryMessages);
            ServiceSummary = "STT: " + stt.Name + " | AI: " + ai.Name + " | TTS: " + tts.Name;
            initialized = true;
        }

        private void OnEnable()
        {
            lifetimeCts = new CancellationTokenSource();
            if (voice != null)
            {
                voice.SpeechStarted += HandleSpeechStarted;
                voice.UtteranceCaptured += HandleUtterance;
                voice.UtteranceDiscarded += HandleUtteranceDiscarded;
            }
        }

        private void OnDisable()
        {
            if (voice != null)
            {
                voice.SpeechStarted -= HandleSpeechStarted;
                voice.UtteranceCaptured -= HandleUtterance;
                voice.UtteranceDiscarded -= HandleUtteranceDiscarded;
            }
            CancelTurn();
            StopVoiceAudio();
            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
                lifetimeCts = null;
            }
        }

        private void Start()
        {
            if (!initialized)
            {
                Debug.LogError("[Mini] ConversationManager was not initialized by MiniCompanionApp.", this);
                return;
            }

            Debug.Log("[Mini] Services → " + ServiceSummary);
            if (settings.talkAfterPlacement)
            {
                // Silent until the app activates us (after Mini has been placed).
                SetState(CompanionState.Sleeping);
                return;
            }
            Activate(true);
        }

        /// <summary>True while Mini is listening and able to talk.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Start listening (and optionally say hello). Safe to call repeatedly.</summary>
        public async void Activate(bool greet)
        {
            if (!initialized || IsActive || lifetimeCts == null) return;
            IsActive = true;
            RaiseStatus(string.Empty);
            SetState(CompanionState.Initialising);
            try
            {
                await Awaitable.WaitForSecondsAsync(0.5f, lifetimeCts.Token);
                if (!IsActive) return;
                if (voice != null) voice.StartListening();
                SetState(CompanionState.WakeUp);
                await Awaitable.WaitForSecondsAsync(settings.wakeUpSeconds, lifetimeCts.Token);
                if (!IsActive || State != CompanionState.WakeUp) return;
                SetState(CompanionState.Idle);
                if (greet)
                {
                    // A short spoken hello: friendly, and proves straight away that audio works.
                    CancellationToken ct = BeginTurn();
                    await SpeakAsync("Hi! I'm Mini!", BehaviourCue.Make(CharacterEmotion.Happy, 0.7f, CharacterGesture.Wave), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Debug.LogWarning("[Mini] Greeting failed: " + e.Message);
                if (IsActive) SetState(CompanionState.Idle);
            }
        }

        /// <summary>Stop talking and listening (microphone off) until <see cref="Activate"/> is called again.</summary>
        public void Deactivate()
        {
            if (!IsActive) return;
            IsActive = false;
            CancelTurn();
            StopVoiceAudio();
            if (voice != null) voice.StopListening();
            RaiseStatus(string.Empty);
            SetState(CompanionState.Sleeping);
        }

        private void Update()
        {
            if (!initialized) return;

            if (IsActive && State == CompanionState.Idle && (voice == null || !voice.IsHearingSpeech))
            {
                idleTimer += Time.deltaTime;
                if (settings.sleepAfterSeconds > 0f && idleTimer >= settings.sleepAfterSeconds)
                {
                    SetState(CompanionState.Sleeping);
                }
            }
        }

        // Public API (HUD, debug tools, voice commands)

        /// <summary>Treat text as if the user had said it (debug text box, tests, push-to-type).</summary>
        public void SubmitText(string text)
        {
            if (!initialized || string.IsNullOrWhiteSpace(text)) return;
            CancellationToken ct = BeginTurn();
            RunTextTurn(text.Trim(), ct);
        }

        /// <summary>Stop Mini talking or thinking right now and go back to Idle.</summary>
        public void Interrupt()
        {
            CancelTurn();
            StopVoiceAudio();
            if (State != CompanionState.Sleeping) SetState(CompanionState.Idle);
        }

        public void GoToSleep()
        {
            CancelTurn();
            StopVoiceAudio();
            SetState(CompanionState.Sleeping);
        }

        public void ClearConversation()
        {
            if (history != null) history.Clear();
        }

        // Voice events

        private void HandleSpeechStarted()
        {
            switch (State)
            {
                case CompanionState.Speaking:
                    if (!settings.allowBargeIn) return;
                    // Barge-in: the user talks over Mini.
                    CancelTurn();
                    StopVoiceAudio();
                    SetState(CompanionState.Listening);
                    break;
                case CompanionState.Processing:
                    // The user is still talking or correcting themselves: drop the old turn.
                    CancelTurn();
                    SetState(CompanionState.Listening);
                    break;
                case CompanionState.Sleeping:
                    character.PlayGesture(CharacterGesture.Blink);
                    SetState(CompanionState.Listening);
                    break;
                case CompanionState.Idle:
                case CompanionState.Reacting:
                case CompanionState.Error:
                    SetState(CompanionState.Listening);
                    break;
            }
        }

        private void HandleUtteranceDiscarded()
        {
            if (State == CompanionState.Listening) SetState(CompanionState.Idle);
        }

        private void HandleUtterance(float[] samples, int sampleRate)
        {
            if (State != CompanionState.Listening) return; // e.g. Mini's own voice while speaking
            CancellationToken ct = BeginTurn();
            RunAudioTurn(samples, sampleRate, ct);
        }

        // Turn flow

        private async void RunAudioTurn(float[] samples, int sampleRate, CancellationToken ct)
        {
            try
            {
                SetState(CompanionState.Processing);
                RaiseStatus("Understanding…");
                float seconds = samples.Length / (float)sampleRate;
                SpeechToTextResult result = await stt.TranscribeAsync(samples, sampleRate, ct);
                samples = null; // drop raw audio as soon as it has been transcribed
                // Log only sizes, never the words, so the device log holds no conversation content.
                Debug.Log("[Mini] Heard " + seconds.ToString("0.0") + "s of speech → " + result.Text.Length + " characters");

                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    await HandleNotUnderstoodAsync(ct);
                    return;
                }
                await HandleUserTextAsync(result.Text.Trim(), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                await HandleFailureAsync(e, ct);
            }
        }

        private async void RunTextTurn(string text, CancellationToken ct)
        {
            try
            {
                SetState(CompanionState.Processing);
                await HandleUserTextAsync(text, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                await HandleFailureAsync(e, ct);
            }
        }

        private async Task HandleUserTextAsync(string userText, CancellationToken ct)
        {
            notUnderstoodCount = 0;
            LastUserText = userText;
            RaiseSubtitle("You", userText);

            VoiceCommand command = VoiceCommandParser.Parse(userText);
            if (!command.IsNone)
            {
                await ExecuteCommandAsync(command, userText, ct);
                return;
            }

            RaiseStatus("Thinking…");
            AIResponse response = await ai.GetResponseAsync(history.Turns, userText, ct);
            string reply = CleanReply(response.Text);
            BehaviourCue cue = CueValidator.Validate(response.Emotion, response.Gesture, response.HeadMotion, response.EyeExpression);
            CharacterGesture? requested = ActionRequestParser.Detect(userText);
            if (requested.HasValue)
            {
                // The user asked for a body action: always do it, whatever the AI picked.
                cue.HasGesture = true;
                cue.Gesture = requested.Value;
                if (cue.Emotion == CharacterEmotion.Neutral) { cue.Emotion = CharacterEmotion.Happy; cue.Intensity = 0.8f; }
            }
            Debug.Log("[Mini] Cue " + cue);

            history.AddExchange(userText, reply);
            await SpeakAsync(reply, cue, ct);
        }

        private async Task ExecuteCommandAsync(VoiceCommand command, string userText, CancellationToken ct)
        {
            switch (command.Type)
            {
                case VoiceCommandType.Stop:
                    StopVoiceAudio();
                    SetState(CompanionState.Idle);
                    return;
                case VoiceCommandType.ClearConversation: ClearConversation(); break;
                case VoiceCommandType.ComeHere: if (placement != null) placement.ComeHere(); break;
                case VoiceCommandType.MoveCloser: if (placement != null) placement.MoveCloser(); break;
                case VoiceCommandType.MoveAway: if (placement != null) placement.MoveAway(); break;
                case VoiceCommandType.MoveLeft: if (placement != null) placement.MoveLeft(); break;
                case VoiceCommandType.MoveRight: if (placement != null) placement.MoveRight(); break;
                case VoiceCommandType.Stay: if (placement != null) placement.Stay(); break;
                case VoiceCommandType.StartPlacement: if (placement != null) placement.BeginPlacement(); break;
            }

            if (command.Type != VoiceCommandType.ClearConversation) history.AddExchange(userText, command.Reply);
            await SpeakAsync(command.Reply, command.Cue, ct);

            if (command.Type == VoiceCommandType.Sleep) SetState(CompanionState.Sleeping);
        }

        private async Task SpeakAsync(string text, BehaviourCue cue, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                SetState(CompanionState.Idle);
                return;
            }

            RaiseStatus(string.Empty);
            AudioClip clip = await tts.SynthesizeAsync(text, ct);
            if (ct.IsCancellationRequested)
            {
                Destroy(clip);
                ct.ThrowIfCancellationRequested();
            }

            LastMiniText = text;
            RaiseSubtitle("Mini", text);
            StopVoiceAudio();
            currentClip = clip;

            SetState(CompanionState.Speaking);
            character.ApplyCue(cue, Mathf.Max(3f, clip.length + 1.5f));

            if (voiceSource != null)
            {
                voiceSource.clip = clip;
                voiceSource.Play();
                Debug.Log("[Mini] Speaking " + clip.length.ToString("0.0") + "s (volume " + voiceSource.volume + ")");
                // AudioSource.isPlaying is false while the app is paused, so also guard on time.
                float safetyEnd = Time.realtimeSinceStartup + clip.length + 2f;
                while (voiceSource.isPlaying && Time.realtimeSinceStartup < safetyEnd)
                {
                    await Awaitable.NextFrameAsync(ct);
                }
            }
            else
            {
                await Awaitable.WaitForSecondsAsync(clip.length, ct);
            }

            StopVoiceAudio();
            SetState(CompanionState.Reacting);
            await Awaitable.WaitForSecondsAsync(settings.reactionSeconds, ct);
            SetState(CompanionState.Idle);
        }

        private async Task HandleNotUnderstoodAsync(CancellationToken ct)
        {
            notUnderstoodCount++;
            BehaviourCue confused = BehaviourCue.Make(CharacterEmotion.Confused, 0.7f, CharacterGesture.Shrug);
            if (notUnderstoodCount <= 2)
            {
                await SpeakAsync(notUnderstoodCount == 1 ? "Sorry, I didn't catch that." : "Hmm, could you say that again?", confused, ct);
            }
            else
            {
                // Do not nag: just show it silently after repeated misses.
                character.ApplyCue(confused, 2f);
                SetState(CompanionState.Idle);
            }
        }

        private async Task HandleFailureAsync(Exception e, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            ServiceException se = e as ServiceException;
            string message = se == null ? "Something went wrong." : DescribeError(se.Kind);
            Debug.LogWarning("[Mini] Turn failed: " + (se != null ? se.Kind + " " : "") + e.Message);

            StopVoiceAudio();
            SetState(CompanionState.Error);
            RaiseStatus(message);
            try
            {
                await Awaitable.WaitForSecondsAsync(settings.errorSeconds, ct);
                RaiseStatus(string.Empty);
                SetState(CompanionState.Idle);
            }
            catch (OperationCanceledException) { }
        }

        private static string DescribeError(ServiceErrorKind kind)
        {
            switch (kind)
            {
                case ServiceErrorKind.Network: return "I can't reach my server. Check the Wi-Fi.";
                case ServiceErrorKind.Timeout: return "My server is taking too long.";
                case ServiceErrorKind.Unauthorized: return "My server rejected the request (check its API keys).";
                case ServiceErrorKind.RateLimited: return "Too many requests. Let's wait a moment.";
                case ServiceErrorKind.InvalidResponse: return "I got a confusing answer from my server.";
                default: return "My speech service isn't available right now.";
            }
        }

        // Helpers

        private CancellationToken BeginTurn()
        {
            CancelTurn();
            turnCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            return turnCts.Token;
        }

        private void CancelTurn()
        {
            if (turnCts == null) return;
            turnCts.Cancel();
            turnCts.Dispose();
            turnCts = null;
        }

        private void StopVoiceAudio()
        {
            if (voiceSource != null)
            {
                voiceSource.Stop();
                voiceSource.clip = null;
            }
            if (currentClip != null)
            {
                Destroy(currentClip);
                currentClip = null;
            }
        }

        private string CleanReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Hmm, I'm not sure what to say.";
            string t = text.Replace("*", string.Empty).Trim();
            if (t.Length > settings.maxReplyCharacters)
            {
                int cut = t.LastIndexOfAny(new[] { '.', '!', '?' }, settings.maxReplyCharacters - 1);
                t = cut > 40 ? t.Substring(0, cut + 1) : t.Substring(0, settings.maxReplyCharacters) + "…";
            }
            return t;
        }

        private void SetState(CompanionState newState)
        {
            if (State == newState && newState != CompanionState.Initialising) return;
            bool wasSpeaking = State == CompanionState.Speaking;
            State = newState;
            idleTimer = 0f;
            Debug.Log("[Mini] State → " + newState);
            if (character != null) character.SetState(newState);
            if (voice != null)
            {
                bool speaking = newState == CompanionState.Speaking;
                voice.SetBargeInMode(speaking);
                // Without barge-in (phones: speaker next to the mic) Mini must not hear its own voice.
                if (!settings.allowBargeIn && speaking != wasSpeaking) voice.SetDeafened(speaking);
            }
            if (StateChanged != null) StateChanged(newState);
        }

        private void RaiseStatus(string message)
        {
            if (StatusMessage != null) StatusMessage(message);
        }

        private void RaiseSubtitle(string speaker, string text)
        {
            if (Subtitle != null) Subtitle(speaker, text);
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused || !initialized) return;
            // Headset removed / app backgrounded: stop everything; VoiceManager pauses the mic itself.
            CancelTurn();
            StopVoiceAudio();
            if (State != CompanionState.Sleeping && State != CompanionState.Initialising) SetState(CompanionState.Idle);
        }
    }
}
