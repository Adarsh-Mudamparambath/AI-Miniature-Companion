using System.Collections.Generic;

namespace Mini.Core
{
    /// <summary>
    /// A validated, Unity-owned description of how Mini should react. AI responses never touch Unity
    /// objects directly: their loose strings are turned into this struct by <see cref="CueValidator"/>,
    /// and only known enum values can come out of it.
    /// </summary>
    public struct BehaviourCue
    {
        public CharacterEmotion Emotion;
        public float Intensity;
        public bool HasGesture;
        public CharacterGesture Gesture;
        public HeadMotion HeadMotion;

        public static BehaviourCue Neutral
        {
            get { return new BehaviourCue { Emotion = CharacterEmotion.Neutral, Intensity = 0f, HeadMotion = HeadMotion.Subtle }; }
        }

        public static BehaviourCue Make(CharacterEmotion emotion, float intensity, CharacterGesture? gesture = null)
        {
            return new BehaviourCue
            {
                Emotion = emotion,
                Intensity = intensity,
                HasGesture = gesture.HasValue,
                Gesture = gesture.GetValueOrDefault(),
                HeadMotion = HeadMotion.Subtle
            };
        }

        public override string ToString()
        {
            return Emotion + "(" + Intensity.ToString("0.00") + ")" + (HasGesture ? " +" + Gesture : "") + " head:" + HeadMotion;
        }
    }

    /// <summary>
    /// Whitelist-based translation from AI text cues to <see cref="BehaviourCue"/>. Unknown or malformed
    /// values become neutral; nothing outside the whitelist can be selected.
    /// </summary>
    public static class CueValidator
    {
        private static readonly Dictionary<string, CharacterEmotion> Emotions = new Dictionary<string, CharacterEmotion>
        {
            { "neutral", CharacterEmotion.Neutral }, { "calm", CharacterEmotion.Neutral },
            { "happy", CharacterEmotion.Happy }, { "excited", CharacterEmotion.Happy }, { "joy", CharacterEmotion.Happy },
            { "joyful", CharacterEmotion.Happy }, { "playful", CharacterEmotion.Happy }, { "friendly", CharacterEmotion.Happy },
            { "sad", CharacterEmotion.Sad }, { "sorry", CharacterEmotion.Sad }, { "empathetic", CharacterEmotion.Sad },
            { "surprised", CharacterEmotion.Surprised }, { "surprise", CharacterEmotion.Surprised }, { "amazed", CharacterEmotion.Surprised },
            { "confused", CharacterEmotion.Confused }, { "unsure", CharacterEmotion.Confused },
            { "curious", CharacterEmotion.Curious }, { "interested", CharacterEmotion.Curious },
            { "thinking", CharacterEmotion.Thinking }, { "thoughtful", CharacterEmotion.Thinking },
            { "love", CharacterEmotion.Love }, { "loving", CharacterEmotion.Love }, { "affectionate", CharacterEmotion.Love },
            { "angry", CharacterEmotion.Angry }, { "annoyed", CharacterEmotion.Angry }, { "grumpy", CharacterEmotion.Angry }
        };

        private static readonly Dictionary<string, CharacterGesture> Gestures = new Dictionary<string, CharacterGesture>
        {
            { "nod", CharacterGesture.Nod }, { "wave", CharacterGesture.Wave },
            { "shake_head", CharacterGesture.ShakeHead }, { "shakehead", CharacterGesture.ShakeHead },
            { "shrug", CharacterGesture.Shrug },
            { "point_left", CharacterGesture.PointLeft }, { "point_right", CharacterGesture.PointRight },
            { "look_left", CharacterGesture.LookLeft }, { "look_right", CharacterGesture.LookRight },
            { "blink", CharacterGesture.Blink },
            { "jump", CharacterGesture.Jump }, { "dance", CharacterGesture.Dance }, { "spin", CharacterGesture.Spin },
            { "bow", CharacterGesture.Bow }, { "celebrate", CharacterGesture.Celebrate }, { "welcome", CharacterGesture.Welcome },
            { "beckon", CharacterGesture.Beckon }, { "walk_around", CharacterGesture.WalkAround }
        };

        public static BehaviourCue Validate(string emotion, string gesture, string headMotion, string eyeExpression)
        {
            BehaviourCue cue = BehaviourCue.Neutral;

            CharacterEmotion parsed;
            if (TryLookup(Emotions, emotion, out parsed))
            {
                cue.Emotion = parsed;
                cue.Intensity = parsed == CharacterEmotion.Neutral ? 0f : 0.8f;
            }
            else if (TryLookup(Emotions, eyeExpression, out parsed))
            {
                // Eye expression only refines a missing emotion, at lower intensity.
                cue.Emotion = parsed;
                cue.Intensity = parsed == CharacterEmotion.Neutral ? 0f : 0.5f;
            }

            CharacterGesture g;
            if (TryLookup(Gestures, gesture, out g))
            {
                cue.HasGesture = true;
                cue.Gesture = g;
            }

            string head = Normalize(headMotion);
            if (head == "none" || head == "still") cue.HeadMotion = HeadMotion.None;
            else if (head == "expressive" || head == "animated" || head == "lively") cue.HeadMotion = HeadMotion.Expressive;
            else cue.HeadMotion = HeadMotion.Subtle;

            return cue;
        }

        private static bool TryLookup<T>(Dictionary<string, T> table, string raw, out T value)
        {
            string key = Normalize(raw);
            if (key.Length == 0 || key.Length > 32)
            {
                value = default(T);
                return false;
            }
            return table.TryGetValue(key, out value);
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        }
    }
}
