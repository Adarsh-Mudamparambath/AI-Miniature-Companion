using UnityEngine;
using UnityEngine.UI;
using Mini.Controls;
using Mini.Conversation;
using Mini.Core;
using Mini.MR;
using Mini.Voice;

namespace Mini.App
{
    /// <summary>
    /// Minimal world-space UI, built at runtime so no extra prefabs or packages are needed:
    /// - a status light above Mini (colour = what Mini is doing; cyan = the microphone is listening),
    /// - a short status line and optional subtitles under it, always facing the user,
    /// - a guidance panel in front of the user for placement help, the privacy notice and problems.
    /// It only reads state; it never drives the conversation.
    /// </summary>
    [DisallowMultipleComponent]
    public class CompanionHud : MonoBehaviour
    {
        [SerializeField] private Transform miniAnchor;
        [Tooltip("Material for the status light (an emissive URP material).")]
        [SerializeField] private Material lightMaterial;
        [Tooltip("Optional: Mini's chest light renderer, tinted with the same status colour.")]
        [SerializeField] private Renderer chestLight;
        [SerializeField] private float lightHeight = 0.34f;
        [SerializeField] private float labelHeight = 0.42f;
        [Tooltip("Size of the status text above Mini. Phones view Mini from closer, so they use a smaller label.")]
        [SerializeField] private float labelScale = 1f;
        [Tooltip("Show the guidance panel as a screen overlay (phone) instead of floating in front of the user (headset).")]
        [SerializeField] private bool screenSpacePanel;

        private static readonly Color IdleColor = new Color(0.25f, 0.45f, 0.9f);
        private static readonly Color ListeningColor = new Color(0.1f, 0.95f, 1f);
        private static readonly Color ProcessingColor = new Color(1f, 0.7f, 0.15f);
        private static readonly Color SpeakingColor = new Color(0.6f, 0.85f, 1f);
        private static readonly Color SleepingColor = new Color(0.35f, 0.25f, 0.6f);
        private static readonly Color ErrorColor = new Color(1f, 0.25f, 0.2f);
        private static readonly Color MutedColor = new Color(0.35f, 0.35f, 0.35f);

        private MiniSettings settings;
        private ConversationManager conversation;
        private VoiceManager voice;
        private PlacementManager placement;
        private Transform userHead;
        private MiniControlsBase controls;

        private Transform lightOrb;
        private Renderer lightRenderer;
        private MaterialPropertyBlock block;
        private Transform labelCanvas;
        private Text statusText;
        private Text subtitleText;
        private Transform panelCanvas;
        private Text panelText;
        private GameObject panelRoot;

        private string statusOverride = string.Empty;
        private float privacyUntil;
        private float subtitleUntil;
        private float calibrationUntil;
        private Color currentColor = IdleColor;
        private bool guidanceEnabled = true;

        public void Bind(MiniSettings appSettings, ConversationManager conv, VoiceManager voiceManager,
            PlacementManager placementManager, Transform head, MiniControlsBase controlScheme = null)
        {
            controls = controlScheme;
            settings = appSettings;
            conversation = conv;
            voice = voiceManager;
            placement = placementManager;
            userHead = head;

            BuildVisuals();

            if (conversation != null)
            {
                conversation.StatusMessage += s => statusOverride = s ?? string.Empty;
                conversation.Subtitle += HandleSubtitle;
            }
            if (placement != null) placement.SurfaceCalibrated += _ => calibrationUntil = Time.time + 2.5f;
            if (settings.showPrivacyNotice && !settings.talkAfterPlacement) privacyUntil = Time.time + settings.privacyNoticeSeconds;
        }

        /// <summary>Show the privacy notice now (e.g. when Mini starts listening after placement).</summary>
        public void ShowPrivacyNotice()
        {
            if (settings != null && settings.showPrivacyNotice) privacyUntil = Time.time + settings.privacyNoticeSeconds;
        }

        /// <summary>Hide or show the guidance panel overlay (hidden on the phone's main menu).</summary>
        public void SetGuidanceVisible(bool visible)
        {
            guidanceEnabled = visible;
            if (!visible && panelRoot != null) panelRoot.SetActive(false);
        }

        private void HandleSubtitle(string speaker, string text)
        {
            if (!settings.showSubtitles || subtitleText == null) return;
            // Offline mode cannot understand real speech: make it obvious the line is a practice phrase.
            if (speaker == "You" && settings.serviceMode == ServiceMode.Offline) speaker = "Practice phrase";
            subtitleText.text = speaker + ": " + text;
            subtitleUntil = Time.time + Mathf.Clamp(text.Length * 0.07f + 2f, 3f, 12f);
        }

        private void LateUpdate()
        {
            if (settings == null || miniAnchor == null) return;

            CompanionState state = conversation != null ? conversation.State : CompanionState.Idle;
            MicrophoneStatus mic = voice != null ? voice.Status : MicrophoneStatus.NotStarted;

            bool miniVisible = placement == null || !placement.IsHiddenUntilPlaced;
            if (lightOrb.gameObject.activeSelf != miniVisible) lightOrb.gameObject.SetActive(miniVisible);
            if (labelCanvas.gameObject.activeSelf != miniVisible) labelCanvas.gameObject.SetActive(miniVisible);

            UpdateLight(state, mic);
            UpdateLabel(state, mic);
            UpdatePanel(mic);
        }

        private void UpdateLight(CompanionState state, MicrophoneStatus mic)
        {
            Color target;
            float pulse = 1f;
            float t = Time.time;

            switch (state)
            {
                case CompanionState.Listening:
                    target = ListeningColor;
                    pulse = 1f + Mathf.Clamp01(voice != null ? voice.InputLevel * 12f : 0f);
                    break;
                case CompanionState.Processing: target = ProcessingColor; pulse = 0.8f + 0.4f * Mathf.Sin(t * 8f); break;
                case CompanionState.Speaking: target = SpeakingColor; break;
                case CompanionState.Sleeping: target = SleepingColor; pulse = 0.5f + 0.3f * Mathf.Sin(t * 1.2f); break;
                case CompanionState.Error: target = ErrorColor; break;
                default: target = IdleColor; pulse = 0.9f + 0.1f * Mathf.Sin(t * 2f); break;
            }
            if (mic == MicrophoneStatus.Muted || mic == MicrophoneStatus.PermissionDenied) target = MutedColor;

            currentColor = Color.Lerp(currentColor, target, 1f - Mathf.Exp(-10f * Time.deltaTime));
            Color emission = currentColor * (1.5f * pulse);

            lightOrb.position = miniAnchor.position + Vector3.up * lightHeight;
            lightOrb.localScale = Vector3.one * (0.016f * (state == CompanionState.Listening ? Mathf.Lerp(1f, 1.35f, pulse - 1f) : 1f));
            ApplyColor(lightRenderer, currentColor, emission);
            ApplyColor(chestLight, currentColor, emission);
        }

        private void ApplyColor(Renderer r, Color baseColor, Color emission)
        {
            if (r == null) return;
            r.GetPropertyBlock(block);
            block.SetColor("_BaseColor", baseColor);
            block.SetColor("_EmissionColor", emission);
            r.SetPropertyBlock(block);
        }

        private void UpdateLabel(CompanionState state, MicrophoneStatus mic)
        {
            labelCanvas.position = miniAnchor.position + Vector3.up * labelHeight;
            FaceUser(labelCanvas);

            string text;
            if (placement != null && placement.IsPlacing) text = string.Empty; // the guidance panel explains placing
            else if (!string.IsNullOrEmpty(statusOverride)) text = statusOverride;
            else if (mic == MicrophoneStatus.Muted) text = "Microphone off";
            else if (Time.time < calibrationUntil) text = "Table height set";
            else
            {
                switch (state)
                {
                    case CompanionState.Initialising: text = "Starting up…"; break;
                    case CompanionState.Listening: text = "Listening…"; break;
                    case CompanionState.Processing: text = "Thinking…"; break;
                    case CompanionState.Sleeping: text = "Sleeping · talk to wake me"; break;
                    case CompanionState.Error: text = "Something went wrong"; break;
                    default: text = string.Empty; break;
                }
            }
            statusText.text = text;

            if (subtitleText != null && Time.time > subtitleUntil) subtitleText.text = string.Empty;
        }

        private void UpdatePanel(MicrophoneStatus mic)
        {
            string message = string.Empty;

            string micOff = controls != null ? controls.MicOffHint : "Press B to turn the microphone off.";
            if (mic == MicrophoneStatus.PermissionDenied)
                message = "Microphone permission is off, so I can't hear you.\n" +
                          (controls != null ? controls.PermissionHint : "Allow it in the headset: Settings > Apps > Mini > Permissions.");
            else if (mic == MicrophoneStatus.NoDevice)
                message = "No microphone was found.";
            else if (placement != null && placement.IsPlacing)
                message = controls != null ? controls.PlacementHint :
                          "Point at the floor or a table and pull the RIGHT TRIGGER to place me.\n" +
                          "To put me on a table: touch the table with the controller and squeeze GRIP.\n" +
                          "Thumbstick up/down adjusts the height. Press A to move me again later.";
            else if (Time.time < privacyUntil)
                message = settings.serviceMode == ServiceMode.Backend
                    ? "I listen while my light is cyan. What you say is sent to the Mini server to be understood, and is not saved.\n" + micOff
                    : "Offline demo mode: I hear when you speak but use practice phrases instead of real speech recognition.\n" + micOff;

            bool show = guidanceEnabled && message.Length > 0;
            if (panelRoot.activeSelf != show) panelRoot.SetActive(show);
            if (!show) return;

            panelText.text = message;
            if (!screenSpacePanel && userHead != null)
            {
                Vector3 forward = userHead.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                Vector3 target = userHead.position + forward.normalized * 0.9f + Vector3.down * 0.22f;
                // Lazy follow: comfortable, never glued to the face.
                panelCanvas.position = Vector3.Lerp(panelCanvas.position, target, 1f - Mathf.Exp(-3f * Time.deltaTime));
                FaceUser(panelCanvas);
            }
        }

        private void FaceUser(Transform t)
        {
            if (userHead == null) return;
            Vector3 dir = t.position - userHead.position;
            if (dir.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        private void BuildVisuals()
        {
            block = new MaterialPropertyBlock();

            GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "MiniStatusLight";
            Destroy(orb.GetComponent<Collider>());
            lightRenderer = orb.GetComponent<Renderer>();
            if (lightMaterial != null) lightRenderer.sharedMaterial = lightMaterial;
            lightRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lightRenderer.receiveShadows = false;
            lightOrb = orb.transform;
            lightOrb.SetParent(transform, false);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            labelCanvas = CreateCanvas("MiniStatusLabel", new Vector2(520, 200), 0.0006f * labelScale).transform;
            statusText = CreateText(labelCanvas, font, 34, new Vector2(0, 50), new Vector2(520, 60), Color.white);
            subtitleText = CreateText(labelCanvas, font, 26, new Vector2(0, -35), new Vector2(520, 110), new Color(0.85f, 0.95f, 1f));

            GameObject panel;
            if (screenSpacePanel)
            {
                panel = CreateOverlayPanel();
                panelCanvas = panel.transform;
            }
            else
            {
                panel = CreateCanvas("MiniGuidancePanel", new Vector2(760, 270), 0.0008f);
                panelCanvas = panel.transform;
            }
            panelRoot = panel;
            Transform panelParent = screenSpacePanel ? panel.transform.GetChild(0).GetChild(0) : panelCanvas;
            if (!screenSpacePanel)
            {
                Image bg = new GameObject("Background", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                bg.transform.SetParent(panelCanvas, false);
                RectTransform bgRect = bg.rectTransform;
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;
                bg.color = new Color(0.05f, 0.08f, 0.12f, 0.85f);
                panelText = CreateText(panelCanvas, font, 28, Vector2.zero, new Vector2(720, 250), Color.white);
                if (userHead != null) panelCanvas.position = userHead.position + userHead.forward * 0.9f;
            }
            else
            {
                panelText = Mini.UI.MiniUi.Label(panelParent, string.Empty, 34, Mini.UI.MiniUi.Text);
                RectTransform tr = panelText.rectTransform;
                tr.anchorMin = Vector2.zero;
                tr.anchorMax = Vector2.one;
                tr.offsetMin = new Vector2(48, 28);
                tr.offsetMax = new Vector2(-48, -28);
                panelText.verticalOverflow = VerticalWrapMode.Truncate;
                panelText.resizeTextForBestFit = true;
                panelText.resizeTextMinSize = 24;
                panelText.resizeTextMaxSize = 34;
            }
            panelRoot.SetActive(false);
        }

        /// <summary>A screen-overlay canvas with a frosted-glass card near the top of the screen.</summary>
        private GameObject CreateOverlayPanel()
        {
            Canvas canvas = Mini.UI.MiniUi.CreateCanvas("MiniGuidancePanel", transform, 10);
            canvas.GetComponent<GraphicRaycaster>().enabled = false; // never blocks taps on the camera view
            RectTransform safe = Mini.UI.MiniUi.SafeArea(canvas.transform);
            Image card = Mini.UI.MiniUi.GlassPanel(safe, "Card", 44f);
            Mini.UI.MiniUi.PlaceWide(card.rectTransform, 1f, 1f, -200f, 250f, Mini.UI.MiniUi.Margin);
            // GetChild(0) of the canvas must be the text parent: move the card up the hierarchy order.
            safe.SetAsFirstSibling();
            return canvas.gameObject;
        }

        private GameObject CreateCanvas(string name, Vector2 size, float scale)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas));
            go.transform.SetParent(transform, false);
            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            go.transform.localScale = Vector3.one * scale;
            return go;
        }

        private static Text CreateText(Transform parent, Font font, int size, Vector2 position, Vector2 box, Color color)
        {
            GameObject go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            RectTransform rect = text.rectTransform;
            rect.anchoredPosition = position;
            rect.sizeDelta = box;
            return text;
        }
    }
}
