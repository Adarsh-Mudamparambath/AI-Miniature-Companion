using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Mini.Voice
{
    public enum MicrophoneStatus
    {
        NotStarted,
        WaitingForPermission,
        PermissionDenied,
        NoDevice,
        Active,
        Muted,
        Paused
    }

    /// <summary>
    /// Owns the microphone: permission, start/stop, mute, pause/resume, and voice activity detection.
    /// Raises <see cref="SpeechStarted"/> when the user starts talking and <see cref="UtteranceCaptured"/>
    /// with the finished audio. It never uploads anything itself; the conversation layer decides what
    /// happens to an utterance. Audio is kept in memory only and released after each utterance.
    /// </summary>
    [DisallowMultipleComponent]
    public class VoiceManager : MonoBehaviour
    {
        [SerializeField] private int preferredSampleRate = 16000;
        [SerializeField] private string microphoneDevice = "";
        [SerializeField] private VoiceActivitySettings activitySettings = new VoiceActivitySettings();
        [SerializeField] private float bargeInMultiplier = 2.5f;

        private readonly MicrophoneInput mic = new MicrophoneInput();
        private VoiceActivityDetector vad;
        private bool wantListening;
        private bool muted;
        private bool paused;
        private bool bargeInMode;
        private bool pushToTalkActive;

        public MicrophoneStatus Status { get; private set; }
        public bool IsHearingSpeech { get { return vad != null && vad.InSpeech; } }
        public float InputLevel { get { return vad != null ? vad.LastLevel : 0f; } }
        public float Threshold { get { return vad != null ? vad.CurrentThreshold : 0f; } }
        public bool IsMuted { get { return muted; } }

        public event Action SpeechStarted;
        public event Action<float[], int> UtteranceCaptured;
        public event Action UtteranceDiscarded;
        public event Action<MicrophoneStatus> StatusChanged;

        public void Configure(VoiceActivitySettings settings, float bargeIn)
        {
            if (settings != null) activitySettings = settings;
            bargeInMultiplier = Mathf.Max(1f, bargeIn);
            if (vad != null) CreateDetector(vad.SampleRate);
        }

        /// <summary>Ask for microphone permission if needed, then start listening.</summary>
        public void StartListening()
        {
            wantListening = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                SetStatus(MicrophoneStatus.WaitingForPermission);
                PermissionCallbacks callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => { if (wantListening) StartMicrophone(); };
                callbacks.PermissionDenied += _ => SetStatus(MicrophoneStatus.PermissionDenied);
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
                return;
            }
#endif
            StartMicrophone();
        }

        public void StopListening()
        {
            wantListening = false;
            StopMicrophone();
            SetStatus(MicrophoneStatus.NotStarted);
        }

        public void SetMuted(bool value)
        {
            if (muted == value) return;
            muted = value;
            if (muted)
            {
                StopMicrophone();
                SetStatus(MicrophoneStatus.Muted);
            }
            else if (wantListening && !paused)
            {
                StartListening();
            }
        }

        public void ToggleMute() { SetMuted(!muted); }

        /// <summary>While Mini speaks, require a clearly louder voice to interrupt and stop learning noise.</summary>
        public void SetBargeInMode(bool enabled)
        {
            bargeInMode = enabled;
            ApplyDetectorMode();
        }

        /// <summary>Hold-to-talk fallback for noisy rooms: captures regardless of loudness until released.</summary>
        public void BeginPushToTalk()
        {
            if (vad == null || Status != MicrophoneStatus.Active) return;
            pushToTalkActive = true;
            vad.HoldOpen = true;
            vad.ForceStart();
        }

        public void EndPushToTalk()
        {
            if (!pushToTalkActive || vad == null) return;
            pushToTalkActive = false;
            vad.HoldOpen = false;
            vad.ForceEnd();
        }

        private void StartMicrophone()
        {
            if (muted)
            {
                SetStatus(MicrophoneStatus.Muted);
                return;
            }
            if (paused) return;

            if (!MicrophoneInput.HasDevice)
            {
                SetStatus(MicrophoneStatus.NoDevice);
                return;
            }

            if (!mic.IsRecording && !mic.Start(microphoneDevice, preferredSampleRate))
            {
                SetStatus(MicrophoneStatus.NoDevice);
                return;
            }

            if (vad == null || vad.SampleRate != mic.SampleRate) CreateDetector(mic.SampleRate);
            else vad.Reset();
            SetStatus(MicrophoneStatus.Active);
        }

        private void StopMicrophone()
        {
            mic.Stop();
            pushToTalkActive = false;
            if (vad != null)
            {
                vad.HoldOpen = false;
                vad.Reset();
            }
        }

        private void CreateDetector(int rate)
        {
            if (vad != null)
            {
                vad.SpeechStarted -= HandleSpeechStarted;
                vad.SpeechEnded -= HandleSpeechEnded;
                vad.SpeechDiscarded -= HandleSpeechDiscarded;
            }
            vad = new VoiceActivityDetector(rate, activitySettings);
            vad.SpeechStarted += HandleSpeechStarted;
            vad.SpeechEnded += HandleSpeechEnded;
            vad.SpeechDiscarded += HandleSpeechDiscarded;
            ApplyDetectorMode();
        }

        private void ApplyDetectorMode()
        {
            if (vad == null) return;
            vad.ThresholdMultiplier = bargeInMode ? bargeInMultiplier : 1f;
            vad.FreezeNoiseFloor = bargeInMode;
        }

        private void Update()
        {
            if (Status != MicrophoneStatus.Active || vad == null) return;
            mic.Read(vad.Process);
        }

        private void HandleSpeechStarted()
        {
            if (SpeechStarted != null) SpeechStarted();
        }

        private void HandleSpeechEnded(float[] samples)
        {
            if (UtteranceCaptured != null) UtteranceCaptured(samples, vad.SampleRate);
        }

        private void HandleSpeechDiscarded()
        {
            if (UtteranceDiscarded != null) UtteranceDiscarded();
        }

        private void SetStatus(MicrophoneStatus status)
        {
            if (Status == status) return;
            Status = status;
            Debug.Log("[Mini] Microphone → " + status + (status == MicrophoneStatus.Active ? " (" + mic.SampleRate + " Hz)" : ""));
            if (StatusChanged != null) StatusChanged(status);
        }

        private void OnApplicationPause(bool isPaused)
        {
            paused = isPaused;
            if (isPaused)
            {
                StopMicrophone();
                if (wantListening) SetStatus(MicrophoneStatus.Paused);
            }
            else if (wantListening)
            {
                StartListening();
            }
        }

        private void OnDisable()
        {
            StopMicrophone();
        }
    }
}
