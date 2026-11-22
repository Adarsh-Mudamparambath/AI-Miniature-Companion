using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Mini.Conversation;
using Mini.Core;
using Mini.Services;
using Mini.Services.Offline;
using Mini.Voice;

namespace Mini.Tests
{
    public class VoiceActivityDetectorTests
    {
        private const int Rate = 16000;

        private static float[] Tone(float seconds, float amplitude)
        {
            float[] s = new float[(int)(seconds * Rate)];
            for (int i = 0; i < s.Length; i++) s[i] = amplitude * Mathf.Sin(2f * Mathf.PI * 220f * i / Rate);
            return s;
        }

        private static float[] Noise(float seconds, float amplitude, int seed = 1)
        {
            System.Random r = new System.Random(seed);
            float[] s = new float[(int)(seconds * Rate)];
            for (int i = 0; i < s.Length; i++) s[i] = amplitude * (float)(r.NextDouble() * 2 - 1);
            return s;
        }

        private static void Feed(VoiceActivityDetector vad, float[] samples)
        {
            const int chunk = 160;
            float[] buffer = new float[chunk];
            for (int i = 0; i + chunk <= samples.Length; i += chunk)
            {
                System.Array.Copy(samples, i, buffer, 0, chunk);
                vad.Process(buffer, chunk);
            }
        }

        [Test]
        public void QuietNoise_ProducesNoSpeech()
        {
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, new VoiceActivitySettings());
            int started = 0;
            vad.SpeechStarted += () => started++;
            Feed(vad, Noise(3f, 0.003f));
            Assert.AreEqual(0, started);
        }

        [Test]
        public void SpeechAfterSilence_IsCapturedWithPreRoll()
        {
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, new VoiceActivitySettings());
            int started = 0;
            float[] captured = null;
            vad.SpeechStarted += () => started++;
            vad.SpeechEnded += s => captured = s;

            Feed(vad, Noise(1f, 0.003f));
            Feed(vad, Tone(1.2f, 0.3f));
            Feed(vad, Noise(1.5f, 0.003f, 2));

            Assert.AreEqual(1, started);
            Assert.IsNotNull(captured);
            float seconds = captured.Length / (float)Rate;
            Assert.That(seconds, Is.InRange(1.2f, 1.8f), "utterance length " + seconds);
        }

        [Test]
        public void ShortClick_IsDiscarded()
        {
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, new VoiceActivitySettings());
            bool discarded = false, ended = false;
            vad.SpeechDiscarded += () => discarded = true;
            vad.SpeechEnded += _ => ended = true;

            Feed(vad, Noise(1f, 0.003f));
            Feed(vad, Tone(0.12f, 0.4f));
            Feed(vad, Noise(1.5f, 0.003f, 3));

            Assert.IsFalse(ended);
            Assert.IsTrue(discarded);
        }

        [Test]
        public void LongSpeech_IsCutAtMaximumLength()
        {
            VoiceActivitySettings settings = new VoiceActivitySettings { maxUtteranceSeconds = 2f };
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, settings);
            float[] captured = null;
            vad.SpeechEnded += s => captured = s;

            Feed(vad, Noise(0.5f, 0.003f));
            Feed(vad, Tone(4f, 0.3f));

            Assert.IsNotNull(captured);
            Assert.That(captured.Length / (float)Rate, Is.LessThanOrEqualTo(2.05f));
        }

        [Test]
        public void BargeInMultiplier_IgnoresModerateVoice()
        {
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, new VoiceActivitySettings());
            int started = 0;
            vad.SpeechStarted += () => started++;
            Feed(vad, Noise(1f, 0.003f));
            vad.ThresholdMultiplier = 10f;
            vad.FreezeNoiseFloor = true;
            Feed(vad, Tone(1f, 0.05f)); // RMS ~0.035, below 10x threshold
            Assert.AreEqual(0, started);
        }

        [Test]
        public void PushToTalk_HoldOpenIgnoresSilence()
        {
            VoiceActivityDetector vad = new VoiceActivityDetector(Rate, new VoiceActivitySettings());
            float[] captured = null;
            vad.SpeechEnded += s => captured = s;
            vad.HoldOpen = true;
            vad.ForceStart();
            Feed(vad, Noise(2f, 0.003f));
            Assert.IsNull(captured, "silence must not end a held utterance");
            vad.HoldOpen = false;
            vad.ForceEnd();
            Assert.IsNotNull(captured);
        }
    }

    public class WavUtilityTests
    {
        [Test]
        public void EncodeDecode_RoundTrips()
        {
            float[] samples = { 0f, 0.5f, -0.5f, 0.99f, -0.99f };
            byte[] wav = WavUtility.EncodePcm16(samples, 16000);
            float[] decoded;
            int rate, channels;
            Assert.IsTrue(WavUtility.TryDecode(wav, out decoded, out rate, out channels));
            Assert.AreEqual(16000, rate);
            Assert.AreEqual(1, channels);
            Assert.AreEqual(samples.Length, decoded.Length);
            for (int i = 0; i < samples.Length; i++) Assert.AreEqual(samples[i], decoded[i], 0.001f);
        }

        [Test]
        public void Garbage_IsRejected()
        {
            float[] s;
            int r, c;
            Assert.IsFalse(WavUtility.TryDecode(new byte[60], out s, out r, out c));
            Assert.IsFalse(WavUtility.TryDecode(null, out s, out r, out c));
        }
    }

    public class CueValidatorTests
    {
        [Test]
        public void KnownValues_AreMapped()
        {
            BehaviourCue cue = CueValidator.Validate("Happy", "shake head", "expressive", "curious");
            Assert.AreEqual(CharacterEmotion.Happy, cue.Emotion);
            Assert.IsTrue(cue.HasGesture);
            Assert.AreEqual(CharacterGesture.ShakeHead, cue.Gesture);
            Assert.AreEqual(HeadMotion.Expressive, cue.HeadMotion);
        }

        [Test]
        public void UnknownOrHostileValues_BecomeNeutral()
        {
            BehaviourCue cue = CueValidator.Validate("Destroy(gameObject)", "System.IO.File.Delete", null, "");
            Assert.AreEqual(CharacterEmotion.Neutral, cue.Emotion);
            Assert.IsFalse(cue.HasGesture);
            Assert.AreEqual(HeadMotion.Subtle, cue.HeadMotion);
        }

        [Test]
        public void EyeExpression_FillsMissingEmotion()
        {
            BehaviourCue cue = CueValidator.Validate("", "", "", "surprised");
            Assert.AreEqual(CharacterEmotion.Surprised, cue.Emotion);
            Assert.Less(cue.Intensity, 0.8f);
        }
    }

    public class VoiceCommandParserTests
    {
        [TestCase("Come here", VoiceCommandType.ComeHere)]
        [TestCase("Mini, come closer please", VoiceCommandType.MoveCloser)]
        [TestCase("Go back a little", VoiceCommandType.MoveAway)]
        [TestCase("Move a little to the left", VoiceCommandType.MoveLeft)]
        [TestCase("Move to the right.", VoiceCommandType.MoveRight)]
        [TestCase("Stay here", VoiceCommandType.Stay)]
        [TestCase("Sit on this table", VoiceCommandType.StartPlacement)]
        [TestCase("Go to sleep", VoiceCommandType.Sleep)]
        [TestCase("Stop talking", VoiceCommandType.Stop)]
        [TestCase("Let's start over", VoiceCommandType.ClearConversation)]
        public void Commands_AreRecognised(string text, VoiceCommandType expected)
        {
            Assert.AreEqual(expected, VoiceCommandParser.Parse(text).Type);
        }

        [TestCase("I came here yesterday because my friend told me there was a great cafe nearby")]
        [TestCase("What is the capital of France?")]
        [TestCase("")]
        public void NormalSpeech_IsNotACommand(string text)
        {
            Assert.AreEqual(VoiceCommandType.None, VoiceCommandParser.Parse(text).Type);
        }
    }

    public class ConversationHistoryTests
    {
        [Test]
        public void History_IsCappedAndStartsWithUser()
        {
            ConversationHistory history = new ConversationHistory(4);
            for (int i = 0; i < 5; i++) history.AddExchange("u" + i, "a" + i);
            Assert.AreEqual(4, history.Count);
            Assert.AreEqual(ConversationRole.User, history.Turns[0].Role);
            Assert.AreEqual("u3", history.Turns[0].Text);
            history.Clear();
            Assert.AreEqual(0, history.Count);
        }
    }

    public class OfflineServiceTests
    {
        [Test]
        public void RuleBasedBrain_AlwaysAnswersWithValidCues()
        {
            RuleBasedConversationService brain = new RuleBasedConversationService();
            foreach (string input in new[] { "Hello Mini!", "tell me a joke", "qwerty", "", null })
            {
                AIResponse r = brain.Respond(input);
                Assert.IsFalse(string.IsNullOrWhiteSpace(r.Text));
                BehaviourCue cue = CueValidator.Validate(r.Emotion, r.Gesture, r.HeadMotion, r.EyeExpression);
                Assert.IsTrue(System.Enum.IsDefined(typeof(CharacterEmotion), cue.Emotion));
            }
        }

        [Test]
        public void ProceduralVoice_ProducesBoundedAudio()
        {
            float[] samples = ProceduralVoiceSynthesizer.Generate("Hello there! How are you today?");
            Assert.Greater(samples.Length, ProceduralVoiceSynthesizer.SampleRate / 2);
            float peak = 0f;
            foreach (float s in samples) peak = Mathf.Max(peak, Mathf.Abs(s));
            Assert.Greater(peak, 0.05f);
            Assert.Less(peak, 1f);
            Assert.AreEqual(0, ProceduralVoiceSynthesizer.Generate("").Length);
        }
    }
}
