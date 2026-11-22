using System.Collections.Generic;
using System.Text;
using Mini.Core;
using Mini.Services;

namespace Mini.Conversation
{
    /// <summary>Session-only memory of the conversation. Never written to disk; cleared on app exit.</summary>
    public sealed class ConversationHistory
    {
        private readonly List<ConversationTurn> turns = new List<ConversationTurn>();
        private readonly int maxTurns;

        public ConversationHistory(int maxTurns)
        {
            this.maxTurns = maxTurns < 2 ? 2 : maxTurns;
        }

        public IReadOnlyList<ConversationTurn> Turns { get { return turns; } }
        public int Count { get { return turns.Count; } }

        public void AddExchange(string userText, string assistantText)
        {
            turns.Add(new ConversationTurn(ConversationRole.User, userText));
            turns.Add(new ConversationTurn(ConversationRole.Assistant, assistantText));
            // Drop whole exchanges from the front so history always starts with a user turn.
            while (turns.Count > maxTurns) turns.RemoveRange(0, 2);
        }

        public void Clear() { turns.Clear(); }
    }

    public enum VoiceCommandType
    {
        None,
        ComeHere,
        MoveCloser,
        MoveAway,
        MoveLeft,
        MoveRight,
        Stay,
        StartPlacement,
        Sleep,
        Stop,
        ClearConversation
    }

    public struct VoiceCommand
    {
        public VoiceCommandType Type;
        public string Reply;
        public BehaviourCue Cue;
        public bool IsNone { get { return Type == VoiceCommandType.None; } }
    }

    /// <summary>
    /// Local commands handled by Unity before anything is sent to the AI (faster, works offline, and
    /// keeps placement under Unity's control). Movement commands only match short utterances, so normal
    /// sentences like "I came here yesterday" are not treated as commands.
    /// </summary>
    public static class VoiceCommandParser
    {
        private const int MaxCommandWords = 8;

        public static VoiceCommand Parse(string text)
        {
            string t = Normalize(text);
            VoiceCommand none = new VoiceCommand { Type = VoiceCommandType.None };
            if (t.Length == 0) return none;

            int words = t.Split(' ').Length;
            if (words > MaxCommandWords) return none;
            string padded = " " + t + " ";

            if (Has(padded, "forget everything", "new conversation", "start over", "reset conversation"))
                return Make(VoiceCommandType.ClearConversation, "Okay, fresh start!", CharacterEmotion.Happy, CharacterGesture.Nod);
            if (Has(padded, "go to sleep", "goodnight", "good night", "sleep now", "take a nap"))
                return Make(VoiceCommandType.Sleep, "Okay. Goodnight!", CharacterEmotion.Happy, CharacterGesture.Wave);
            if (Has(padded, " stop talking ", " be quiet ", " shush ", " quiet please ", " stop "))
                return Make(VoiceCommandType.Stop, string.Empty, CharacterEmotion.Neutral, null);
            if (Has(padded, "sit on this table", "sit on the table", "go on the table", "move you", "place you", "put you", "change your spot", "new spot"))
                return Make(VoiceCommandType.StartPlacement, "Sure! Point where you want me and pull the trigger.", CharacterEmotion.Curious, CharacterGesture.Nod);
            if (Has(padded, "come here", "come to me", "over here"))
                return Make(VoiceCommandType.ComeHere, "Coming!", CharacterEmotion.Happy, CharacterGesture.Nod);
            if (Has(padded, "move closer", "come closer", "get closer", " closer "))
                return Make(VoiceCommandType.MoveCloser, "A little closer.", CharacterEmotion.Happy, null);
            if (Has(padded, "go back", "move back", "back up", "move away", "further away", " farther "))
                return Make(VoiceCommandType.MoveAway, "Okay, giving you some space.", CharacterEmotion.Neutral, CharacterGesture.Nod);
            if (Has(padded, "to the left", "move left", "go left", " left a bit ", " a little left "))
                return Make(VoiceCommandType.MoveLeft, "Scooting left.", CharacterEmotion.Happy, null);
            if (Has(padded, "to the right", "move right", "go right", " right a bit ", " a little right "))
                return Make(VoiceCommandType.MoveRight, "Scooting right.", CharacterEmotion.Happy, null);
            if (Has(padded, "stay here", "stay there", "stay put", " stay ", "right there"))
                return Make(VoiceCommandType.Stay, "Got it, I'll stay right here.", CharacterEmotion.Happy, CharacterGesture.Nod);

            return none;
        }

        private static VoiceCommand Make(VoiceCommandType type, string reply, CharacterEmotion emotion, CharacterGesture? gesture)
        {
            return new VoiceCommand { Type = type, Reply = reply, Cue = BehaviourCue.Make(emotion, emotion == CharacterEmotion.Neutral ? 0f : 0.7f, gesture) };
        }

        private static bool Has(string text, params string[] phrases)
        {
            foreach (string p in phrases) if (text.Contains(p)) return true;
            return false;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '\'') sb.Append(c);
                else if (char.IsWhiteSpace(c) || c == ',' || c == '.' || c == '!' || c == '?' || c == '-') sb.Append(' ');
            }
            // Collapse spaces and drop the wake word so "Mini, come here" still matches.
            string collapsed = string.Join(" ", sb.ToString().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.StartsWith("hey mini ")) collapsed = collapsed.Substring(9);
            else if (collapsed.StartsWith("mini ")) collapsed = collapsed.Substring(5);
            if (collapsed.EndsWith(" mini")) collapsed = collapsed.Substring(0, collapsed.Length - 5);
            return collapsed.Trim();
        }
    }
}
