using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Mini.Services.Offline
{
    /// <summary>
    /// Demo speech recognizer for Offline mode. It cannot understand audio: each utterance is "heard"
    /// as the next phrase of a fixed script. This lets the whole listen-think-speak loop run with no
    /// credentials. Use Backend mode for real recognition.
    /// </summary>
    public sealed class ScriptedSpeechToText : ISpeechToTextService
    {
        private static readonly string[] Script =
        {
            "Hello Mini!",
            "What is your name?",
            "How are you today?",
            "Tell me a joke.",
            "Come closer.",
            "What can you do?",
            "That's great, thank you!",
            "Do you like music?"
        };

        private int index;

        public string Name { get { return "Scripted demo (offline)"; } }

        public async Task<SpeechToTextResult> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct)
        {
            await Awaitable.WaitForSecondsAsync(0.35f, ct);
            string text = Script[index % Script.Length];
            index++;
            return new SpeechToTextResult(text);
        }
    }

    /// <summary>
    /// Small rule-based brain used in Offline mode and as the fallback when the cloud AI cannot be
    /// reached. Keeps Mini's personality: friendly, curious, playful, short answers.
    /// </summary>
    public sealed class RuleBasedConversationService : IAIConversationService
    {
        private struct Rule
        {
            public string[] Keys;
            public string[] Replies;
            public string Emotion;
            public string Gesture;
        }

        private static readonly Rule[] Rules =
        {
            new Rule { Keys = new[] { "hello", " hi ", " hey ","good morning", "good evening" },
                Replies = new[] { "Hi there! It's so nice to see you.", "Hello! I was hoping you'd say hi.", "Hey! What shall we talk about?" },
                Emotion = "happy", Gesture = "wave" },
            new Rule { Keys = new[] { "your name", "who are you" },
                Replies = new[] { "I'm Mini, your tiny robot friend. I live right here on your table!" },
                Emotion = "happy", Gesture = "nod" },
            new Rule { Keys = new[] { "how are you", "how's it going", "how do you feel" },
                Replies = new[] { "I'm feeling bright and bouncy today! How about you?", "All my circuits are happy. Thanks for asking!" },
                Emotion = "happy", Gesture = "nod" },
            new Rule { Keys = new[] { "joke", "funny" },
                Replies = new[] { "Why did the robot go on holiday? It needed to recharge its batteries!",
                                  "What's a robot's favourite snack? Micro chips!" },
                Emotion = "happy", Gesture = "shrug" },
            new Rule { Keys = new[] { "what can you do", "help", "what do you do" },
                Replies = new[] { "I can chat with you, keep you company, and move around your table. Try saying come closer!" },
                Emotion = "curious", Gesture = "point_right" },
            new Rule { Keys = new[] { "thank", "thanks", "great", "awesome", "cool" },
                Replies = new[] { "You're welcome! That makes me happy.", "Yay! Glad I could help." },
                Emotion = "happy", Gesture = "nod" },
            new Rule { Keys = new[] { "sad", "tired", "bad day", "lonely" },
                Replies = new[] { "Oh no, I'm sorry. I'm right here with you. Do you want to tell me about it?" },
                Emotion = "sad", Gesture = "" },
            new Rule { Keys = new[] { "music", "song", "dance" },
                Replies = new[] { "I love music! My favourite beat is a happy little beep boop." },
                Emotion = "happy", Gesture = "wave" },
            new Rule { Keys = new[] { "bye", "goodbye", "see you" },
                Replies = new[] { "Bye for now! I'll be right here.", "See you soon!" },
                Emotion = "happy", Gesture = "wave" },
        };

        private static readonly string[] Unknown =
        {
            "Ooh, interesting! Tell me more?",
            "Hmm, I'm still a small robot, so I'm not sure. What do you think?",
            "That sounds fascinating. Can you explain it to me?"
        };

        private int counter;

        public string Name { get { return "Rule-based (offline)"; } }

        public async Task<AIResponse> GetResponseAsync(IReadOnlyList<ConversationTurn> history, string userMessage,
            CancellationToken ct)
        {
            await Awaitable.WaitForSecondsAsync(0.4f, ct);
            return Respond(userMessage);
        }

        /// <summary>Synchronous core, also used by tests.</summary>
        public AIResponse Respond(string userMessage)
        {
            string text = " " + (userMessage ?? string.Empty).ToLowerInvariant() + " ";
            counter++;

            foreach (Rule rule in Rules)
            {
                foreach (string key in rule.Keys)
                {
                    if (text.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                    return new AIResponse
                    {
                        Text = rule.Replies[counter % rule.Replies.Length],
                        Emotion = rule.Emotion,
                        Gesture = rule.Gesture,
                        HeadMotion = "subtle",
                        EyeExpression = rule.Emotion
                    };
                }
            }

            return new AIResponse
            {
                Text = Unknown[counter % Unknown.Length],
                Emotion = "curious",
                Gesture = counter % 2 == 0 ? "shrug" : "",
                HeadMotion = "subtle",
                EyeExpression = "curious"
            };
        }
    }

    /// <summary>
    /// Offline "robot voice": synthesises chirpy syllables that follow the rhythm of the text (one tone
    /// per syllable, pauses at spaces and punctuation, rising pitch for questions). Not real speech,
    /// but real audio, so lip sync, interruption and state flow can all be tested without a TTS provider.
    /// </summary>
    public sealed class ProceduralVoiceSynthesizer : ITextToSpeechService
    {
        public const int SampleRate = 22050;
        private const float MaxSeconds = 20f;
        private const float BasePitch = 290f;

        public string Name { get { return "Procedural robot voice (offline)"; } }

        public Task<AudioClip> SynthesizeAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            float[] samples = Generate(text);
            AudioClip clip = AudioClip.Create("MiniVoice_Procedural", Mathf.Max(1, samples.Length), 1, SampleRate, false);
            clip.SetData(samples.Length > 0 ? samples : new float[1], 0);
            return Task.FromResult(clip);
        }

        /// <summary>Pure sample generation (no Unity objects), used by tests.</summary>
        public static float[] Generate(string text)
        {
            List<float> output = new List<float>(SampleRate * 3);
            if (string.IsNullOrEmpty(text)) return output.ToArray();

            bool question = text.TrimEnd().EndsWith("?");
            string[] words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int maxSamples = (int)(MaxSeconds * SampleRate);

            for (int w = 0; w < words.Length && output.Count < maxSamples; w++)
            {
                string word = words[w];
                int syllables = CountSyllables(word);
                int hash = StableHash(word);

                for (int s = 0; s < syllables; s++)
                {
                    float progress = (w + s / (float)syllables) / Mathf.Max(1, words.Length);
                    float contour = question ? Mathf.Lerp(0.95f, 1.3f, progress * progress) : Mathf.Lerp(1.08f, 0.88f, progress);
                    float jitter = 1f + (((hash >> (s * 3)) & 7) - 3.5f) * 0.035f;
                    float pitch = BasePitch * contour * jitter;
                    float duration = 0.10f + ((hash >> (s * 2)) & 3) * 0.022f;
                    char vowel = VowelAt(word, s);
                    AppendSyllable(output, pitch, duration, vowel);
                    AppendSilence(output, 0.018f);
                }

                char last = word[word.Length - 1];
                if (last == '.' || last == '!' || last == '?') AppendSilence(output, 0.28f);
                else if (last == ',' || last == ';' || last == ':') AppendSilence(output, 0.16f);
                else AppendSilence(output, 0.055f);
            }

            return output.ToArray();
        }

        private static void AppendSyllable(List<float> output, float pitch, float duration, char vowel)
        {
            // Vowel colour: relative strength of harmonics 1..4.
            float h2, h3, h4;
            switch (vowel)
            {
                case 'a': h2 = 0.6f; h3 = 0.45f; h4 = 0.2f; break;
                case 'e': h2 = 0.35f; h3 = 0.55f; h4 = 0.35f; break;
                case 'i': h2 = 0.25f; h3 = 0.3f; h4 = 0.5f; break;
                case 'o': h2 = 0.55f; h3 = 0.2f; h4 = 0.08f; break;
                case 'u': h2 = 0.3f; h3 = 0.1f; h4 = 0.05f; break;
                default: h2 = 0.45f; h3 = 0.3f; h4 = 0.15f; break;
            }

            int count = Mathf.RoundToInt(duration * SampleRate);
            double phase = 0;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                float glide = 1f + 0.06f * Mathf.Sin(t * Mathf.PI);                 // little chirp
                float vibrato = 1f + 0.012f * Mathf.Sin(i * 2f * Mathf.PI * 6f / SampleRate);
                phase += 2.0 * Math.PI * pitch * glide * vibrato / SampleRate;

                float env = Mathf.Min(1f, t / 0.12f) * Mathf.Min(1f, (1f - t) / 0.3f);
                float p = (float)phase;
                float v = Mathf.Sin(p) + h2 * Mathf.Sin(2f * p) + h3 * Mathf.Sin(3f * p) + h4 * Mathf.Sin(4f * p);
                output.Add(0.34f * env * v);
            }
        }

        private static void AppendSilence(List<float> output, float seconds)
        {
            int count = Mathf.RoundToInt(seconds * SampleRate);
            for (int i = 0; i < count; i++) output.Add(0f);
        }

        private static int CountSyllables(string word)
        {
            int groups = 0;
            bool prevVowel = false;
            foreach (char raw in word.ToLowerInvariant())
            {
                bool isVowel = "aeiouy".IndexOf(raw) >= 0;
                if (isVowel && !prevVowel) groups++;
                prevVowel = isVowel;
            }
            return Mathf.Clamp(groups, 1, 4);
        }

        private static char VowelAt(string word, int index)
        {
            int found = 0;
            foreach (char c in word.ToLowerInvariant())
            {
                if ("aeiou".IndexOf(c) < 0) continue;
                if (found == index) return c;
                found++;
            }
            return 'a';
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }
    }
}
