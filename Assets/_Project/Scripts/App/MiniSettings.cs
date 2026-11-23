using UnityEngine;
using Mini.Voice;

namespace Mini.App
{
    public enum ServiceMode
    {
        /// <summary>No network: scripted recognition, rule-based replies, procedural robot voice.</summary>
        Offline,
        /// <summary>Real speech recognition, Claude replies and TTS through the Mini backend server.</summary>
        Backend
    }

    /// <summary>
    /// All tunable app settings in one asset. Contains no secrets: provider API keys live only on the
    /// backend server. The backend URL is not a secret.
    /// </summary>
    [CreateAssetMenu(menuName = "Mini/Settings", fileName = "MiniSettings")]
    public class MiniSettings : ScriptableObject
    {
        [Header("Services")]
        public ServiceMode serviceMode = ServiceMode.Offline;
        [Tooltip("Mini backend address, e.g. http://192.168.1.50:8787 on your home network.")]
        public string backendUrl = "http://192.168.1.50:8787";
        public int requestTimeoutSeconds = 25;
        [Tooltip("Voice name passed to the backend TTS provider.")]
        public string ttsVoice = "default";

        [Header("Conversation")]
        [Tooltip("Messages kept as session context (user + Mini). Session only, never saved.")]
        public int maxHistoryMessages = 16;
        public int maxReplyCharacters = 450;
        [Tooltip("Seconds of quiet before Mini falls asleep. Talking wakes it.")]
        public float sleepAfterSeconds = 180f;
        [Tooltip("Let the user interrupt Mini by talking over it.")]
        public bool allowBargeIn = true;
        [Tooltip("Speech threshold multiplier while Mini is talking (prevents Mini hearing itself).")]
        public float bargeInMultiplier = 2.5f;
        public float wakeUpSeconds = 1.4f;
        public float reactionSeconds = 1.2f;
        public float errorSeconds = 2.5f;

        [Header("Voice activity detection")]
        public VoiceActivitySettings voiceActivity = new VoiceActivitySettings();

        [Header("Placement")]
        public bool startInPlacementMode = true;
        [Tooltip("Mini stays silent (mic off) until it has been placed, and pauses while being moved.")]
        public bool talkAfterPlacement;
        [Tooltip("Initial surface height in metres (0 = floor with floor-level tracking).")]
        public float defaultSurfaceHeight;

        [Header("UI")]
        public bool showSubtitles = true;
        public bool showPrivacyNotice = true;
        public float privacyNoticeSeconds = 10f;
    }
}
