using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mini.Voice
{
    [Serializable]
    public class VoiceActivitySettings
    {
        [Tooltip("Speech must be this many times louder than the background noise.")]
        public float speechToNoiseRatio = 3f;
        [Tooltip("Absolute minimum loudness (RMS) treated as speech.")]
        public float minSpeechLevel = 0.012f;
        [Tooltip("Consecutive loud 20 ms frames needed to start an utterance.")]
        public int startFrames = 4;
        public float endSilenceSeconds = 0.8f;
        public float minUtteranceSeconds = 0.35f;
        public float maxUtteranceSeconds = 15f;
        public float preRollSeconds = 0.3f;
    }

    /// <summary>
    /// Energy-based voice activity detection with an adaptive noise floor. Plain C# (no Unity objects),
    /// so it is unit tested directly. Feed it microphone samples; it reports when speech starts and hands
    /// back the finished utterance (including a short pre-roll so the first syllable is not cut off).
    /// </summary>
    public sealed class VoiceActivityDetector
    {
        private readonly VoiceActivitySettings settings;
        private readonly int sampleRate;
        private readonly int frameSize;
        private readonly float[] frame;
        private int frameFill;

        private readonly float[] preRoll;
        private int preRollStart;
        private int preRollCount;

        private readonly List<float> utterance = new List<float>();
        private bool inSpeech;
        private int loudRun;
        private int silentFrames;
        private float noiseFloor = 0.004f;

        public event Action SpeechStarted;
        public event Action<float[]> SpeechEnded;
        public event Action SpeechDiscarded;

        /// <summary>Raises the speech threshold, e.g. while Mini is talking (barge-in needs a clear voice).</summary>
        public float ThresholdMultiplier = 1f;

        /// <summary>Stop the noise floor from learning (while Mini's own voice is playing).</summary>
        public bool FreezeNoiseFloor;

        /// <summary>While true (push-to-talk held), silence does not end the utterance.</summary>
        public bool HoldOpen;

        public bool InSpeech { get { return inSpeech; } }
        public float LastLevel { get; private set; }
        public float NoiseFloor { get { return noiseFloor; } }
        public int SampleRate { get { return sampleRate; } }
        public float FrameSeconds { get { return frameSize / (float)sampleRate; } }

        public VoiceActivityDetector(int sampleRate, VoiceActivitySettings settings)
        {
            this.sampleRate = Mathf.Max(8000, sampleRate);
            this.settings = settings ?? new VoiceActivitySettings();
            frameSize = Mathf.Max(1, this.sampleRate / 50); // 20 ms
            frame = new float[frameSize];
            preRoll = new float[Mathf.Max(frameSize, Mathf.RoundToInt(this.settings.preRollSeconds * this.sampleRate))];
        }

        public float CurrentThreshold
        {
            get { return Mathf.Max(settings.minSpeechLevel, noiseFloor * settings.speechToNoiseRatio) * ThresholdMultiplier; }
        }

        public void Process(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                frame[frameFill++] = samples[i];
                if (frameFill == frameSize)
                {
                    ProcessFrame();
                    frameFill = 0;
                }
            }
        }

        /// <summary>End the current utterance now (push-to-talk release).</summary>
        public void ForceEnd()
        {
            if (inSpeech) Finish(false);
        }

        /// <summary>Start an utterance now regardless of loudness (push-to-talk press).</summary>
        public void ForceStart()
        {
            if (!inSpeech) BeginUtterance();
        }

        public void Reset()
        {
            inSpeech = false;
            loudRun = 0;
            silentFrames = 0;
            frameFill = 0;
            preRollCount = 0;
            preRollStart = 0;
            utterance.Clear();
        }

        private void ProcessFrame()
        {
            float sum = 0f;
            for (int i = 0; i < frameSize; i++) sum += frame[i] * frame[i];
            float rms = Mathf.Sqrt(sum / frameSize);
            LastLevel = rms;

            bool loud = rms > CurrentThreshold;

            if (!inSpeech)
            {
                PushPreRoll();
                if (loud)
                {
                    loudRun++;
                    if (loudRun >= settings.startFrames) BeginUtterance();
                }
                else
                {
                    loudRun = 0;
                    LearnNoise(rms);
                }
                return;
            }

            utterance.AddRange(frame);
            silentFrames = loud ? 0 : silentFrames + 1;

            if (!HoldOpen && silentFrames * FrameSeconds >= settings.endSilenceSeconds) Finish(true);
            else if (utterance.Count >= settings.maxUtteranceSeconds * sampleRate) Finish(false);
        }

        private void LearnNoise(float rms)
        {
            if (FreezeNoiseFloor) return;
            // Falls quickly when it gets quieter, rises slowly so speech does not become "noise".
            float rate = rms < noiseFloor ? 0.2f : 0.02f;
            noiseFloor = Mathf.Clamp(Mathf.Lerp(noiseFloor, rms, rate), 0.0005f, 0.2f);
        }

        private void BeginUtterance()
        {
            inSpeech = true;
            loudRun = 0;
            silentFrames = 0;
            utterance.Clear();
            for (int i = 0; i < preRollCount; i++) utterance.Add(preRoll[(preRollStart + i) % preRoll.Length]);
            preRollCount = 0;
            preRollStart = 0;
            if (SpeechStarted != null) SpeechStarted();
        }

        private void Finish(bool trimTrailingSilence)
        {
            inSpeech = false;
            int length = utterance.Count;
            if (trimTrailingSilence)
            {
                length -= Mathf.Max(0, silentFrames - 5) * frameSize; // keep ~100 ms of tail
                length = Mathf.Max(0, length);
            }
            silentFrames = 0;
            loudRun = 0;

            float seconds = length / (float)sampleRate;
            if (seconds < settings.minUtteranceSeconds + settings.preRollSeconds * 0.5f)
            {
                utterance.Clear();
                if (SpeechDiscarded != null) SpeechDiscarded();
                return;
            }

            float[] result = new float[length];
            utterance.CopyTo(0, result, 0, length);
            utterance.Clear();
            if (SpeechEnded != null) SpeechEnded(result);
        }

        private void PushPreRoll()
        {
            for (int i = 0; i < frameSize; i++)
            {
                if (preRollCount < preRoll.Length)
                {
                    preRoll[(preRollStart + preRollCount) % preRoll.Length] = frame[i];
                    preRollCount++;
                }
                else
                {
                    preRoll[preRollStart] = frame[i];
                    preRollStart = (preRollStart + 1) % preRoll.Length;
                }
            }
        }
    }
}
