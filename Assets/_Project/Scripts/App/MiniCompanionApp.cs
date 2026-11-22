using System;
using UnityEngine;
using Mini.Character;
using Mini.Controls;
using Mini.Conversation;
using Mini.MR;
using Mini.Services;
using Mini.Services.Backend;
using Mini.Services.Offline;
using Mini.Voice;

namespace Mini.App
{
    /// <summary>
    /// Composition root (the "GameManager"): picks the service implementations from <see cref="MiniSettings"/>,
    /// hands them to the systems that need them, and routes the few controller buttons. This is the only
    /// place that knows concrete service classes, so swapping a provider is a one-line change here.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class MiniCompanionApp : MonoBehaviour
    {
        [SerializeField] private MiniSettings settings;
        [SerializeField] private Transform userHead;
        [SerializeField] private ConversationManager conversation;
        [SerializeField] private VoiceManager voice;
        [SerializeField] private CharacterManager character;
        [SerializeField] private PlacementManager placement;
        [SerializeField] private CompanionHud hud;
        [SerializeField] private MiniControllerInput controls;

        public MiniSettings Settings { get { return settings; } }
        public ConversationManager Conversation { get { return conversation; } }

        private void Awake()
        {
            if (settings == null)
            {
                Debug.LogWarning("[Mini] No MiniSettings assigned; using defaults (Offline mode).", this);
                settings = ScriptableObject.CreateInstance<MiniSettings>();
            }

            if (userHead == null && Camera.main != null) userHead = Camera.main.transform;

            ISpeechToTextService stt;
            IAIConversationService ai;
            ITextToSpeechService tts;
            BuildServices(out stt, out ai, out tts);

            if (voice != null) voice.Configure(settings.voiceActivity, settings.bargeInMultiplier);
            if (character != null) character.SetUserHead(userHead);
            if (placement != null)
            {
                placement.SetUserHead(userHead);
                placement.SetDefaultSurfaceHeight(settings.defaultSurfaceHeight);
            }
            if (conversation != null) conversation.Initialize(settings, stt, ai, tts);
            if (hud != null) hud.Bind(settings, conversation, voice, placement, userHead);
        }

        private void Start()
        {
            if (placement != null && settings.startInPlacementMode) placement.BeginPlacement();
        }

        private void BuildServices(out ISpeechToTextService stt, out IAIConversationService ai, out ITextToSpeechService tts)
        {
            ProceduralVoiceSynthesizer offlineVoice = new ProceduralVoiceSynthesizer();
            RuleBasedConversationService offlineBrain = new RuleBasedConversationService();

            if (settings.serviceMode == ServiceMode.Backend)
            {
                BackendClient client = new BackendClient(settings.backendUrl, settings.requestTimeoutSeconds);
                if (!client.IsConfigured)
                {
                    Debug.LogError("[Mini] Backend mode selected but backendUrl is not a valid http(s) URL. Falling back to Offline mode.");
                }
                else
                {
                    string sessionId = Guid.NewGuid().ToString("N"); // per app run, not tied to the user
                    stt = new BackendSpeechToText(client);
                    ai = new FallbackConversationService(new BackendConversationService(client, sessionId), offlineBrain);
                    tts = new FallbackTextToSpeech(new BackendTextToSpeech(client, settings.ttsVoice), offlineVoice);
                    return;
                }
            }

            stt = new ScriptedSpeechToText();
            ai = offlineBrain;
            tts = offlineVoice;
        }

        private void Update()
        {
            if (controls == null) return;

            if (controls.MicTogglePressed && voice != null) voice.ToggleMute();
            if (controls.TalkPressed && voice != null) voice.BeginPushToTalk();
            if (controls.TalkReleased && voice != null) voice.EndPushToTalk();
        }
    }
}
