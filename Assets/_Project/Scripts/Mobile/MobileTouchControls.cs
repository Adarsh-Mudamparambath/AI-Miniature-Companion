using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Mini.Controls;
using Mini.MR;
using Mini.UI;
using Mini.Voice;

namespace Mini.Mobile
{
    /// <summary>
    /// Touch-screen controls for the phone AR build. Builds its own screen-space buttons at runtime:
    /// "Move Mini", a hold-to-talk button and a microphone toggle. While placing, a ring follows the
    /// centre of the screen (or your finger) on detected planes; a tap places Mini there.
    /// Runs before the app and placement code so each frame's input is ready when they read it.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public class MobileTouchControls : MiniControlsBase
    {
        [SerializeField] private PlacementManager placement;
        [SerializeField] private VoiceManager voice;
        [Tooltip("Camera used for touch rays (the AR camera). Defaults to Camera.main.")]
        [SerializeField] private Camera arCamera;
        [Tooltip("Longest finger movement (pixels) that still counts as a tap.")]
        [SerializeField] private float tapSlop = 40f;
        [SerializeField] private float tapMaxSeconds = 0.6f;

        private bool placeToggle, confirm, micToggle, talkDown, talkUp;
        private bool pendingPlaceToggle, pendingMicToggle;

        private bool pointerWasDown;
        private bool pointerStartedOnUi;
        private Vector2 pointerStart;
        private float pointerStartTime;
        private Vector2 pointerPosition;
        private bool pointerDown;

        private GameObject placeButton;
        private GameObject talkRoot;
        private GameObject micButton;
        private CanvasGroup group;
        private bool visible = true;

        /// <summary>The user tapped Leave (back to the main menu).</summary>
        public event System.Action LeavePressed;

        /// <summary>Show or hide all controls (hidden on the main menu; input is ignored while hidden).</summary>
        public void SetVisible(bool show)
        {
            visible = show;
            if (group != null)
            {
                group.alpha = show ? 1f : 0f;
                group.interactable = group.blocksRaycasts = show;
            }
            pendingPlaceToggle = pendingMicToggle = false;
            pointerWasDown = false;
        }

        private Text micLabel;
        private HoldButton talkButton;
        private readonly List<RaycastResult> uiHits = new List<RaycastResult>();

        public override bool PlaceTogglePressed { get { return placeToggle; } }
        public override bool ConfirmPressed { get { return confirm; } }
        public override bool MicTogglePressed { get { return micToggle; } }
        public override bool TalkPressed { get { return talkDown; } }
        public override bool TalkReleased { get { return talkUp; } }

        public override string PlacementHint
        {
            get
            {
                if (ARSession.state == ARSessionState.Unsupported)
                    return "This phone doesn't support AR (ARCore / ARKit), so I can't be placed in your room.";
                if (ARSession.state < ARSessionState.SessionTracking)
                    return "Starting the camera… Move your phone slowly to look around.";
                return "Point your phone at the floor or a table and move it slowly.\n" +
                       "When the ring appears, tap the screen to place me.";
            }
        }

        public override string MicOffHint { get { return "Tap the microphone button to turn it off."; } }
        public override string PermissionHint { get { return "Allow it in your phone's Settings > Apps > Mini > Permissions."; } }

        public override bool TryGetPointerRay(out Ray ray)
        {
            ray = default(Ray);
            Camera cam = arCamera != null ? arCamera : Camera.main;
            if (cam == null) return false;

            Vector2 screen = pointerDown && !pointerStartedOnUi
                ? pointerPosition
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            ray = cam.ScreenPointToRay(screen);
            return true;
        }

        private void Awake()
        {
            BuildUi();
        }

        private void Update()
        {
            placeToggle = visible && pendingPlaceToggle;
            micToggle = visible && pendingMicToggle;
            pendingPlaceToggle = pendingMicToggle = false;

            talkDown = talkButton != null && talkButton.PressedSinceLastRead;
            talkUp = talkButton != null && talkButton.ReleasedSinceLastRead;
            if (talkButton != null) talkButton.ClearEvents();

            if (!visible) { talkDown = talkUp = false; confirm = false; return; }
            ReadPointer();
            RefreshUi();
        }

        /// <summary>Turns a short tap on the camera view (not on a button) into ConfirmPressed.</summary>
        private void ReadPointer()
        {
            confirm = false;
            Pointer pointer = Pointer.current;
            pointerDown = pointer != null && pointer.press.isPressed;
            if (pointerDown) pointerPosition = pointer.position.ReadValue();

            if (pointerDown && !pointerWasDown)
            {
                pointerStart = pointerPosition;
                pointerStartTime = Time.unscaledTime;
                pointerStartedOnUi = IsOverUi(pointerPosition);
            }
            else if (!pointerDown && pointerWasDown && !pointerStartedOnUi)
            {
                bool shortPress = Time.unscaledTime - pointerStartTime <= tapMaxSeconds;
                bool still = (pointerPosition - pointerStart).sqrMagnitude <= tapSlop * tapSlop;
                confirm = shortPress && still;
            }
            pointerWasDown = pointerDown;
        }

        private bool IsOverUi(Vector2 screenPosition)
        {
            EventSystem es = EventSystem.current;
            if (es == null) return false;
            PointerEventData data = new PointerEventData(es) { position = screenPosition };
            uiHits.Clear();
            es.RaycastAll(data, uiHits);
            return uiHits.Count > 0;
        }

        private void RefreshUi()
        {
            // Placing: only Leave (tap the camera view to place). Placed: Move, talk and mic.
            bool live = placement == null || (placement.HasBeenPlaced && !placement.IsPlacing);
            SetActive(placeButton, live);
            SetActive(talkRoot, live);
            SetActive(micButton, live);
            if (micLabel != null && voice != null)
            {
                micLabel.text = voice.Status == MicrophoneStatus.Muted ? "Mic off" : "Mic on";
            }
        }

        private static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            Canvas canvas = MiniUi.CreateCanvas("MiniTouchControls", transform, 20);
            group = canvas.GetComponent<CanvasGroup>();
            RectTransform safe = MiniUi.SafeArea(canvas.transform);
            Text unused;

            // Top-left: Leave (back to the menu).
            Button leave = MiniUi.Button(safe, "Leave", MiniUi.ButtonStyle.Glass, MiniUi.SmallButtonHeight,
                () => { if (LeavePressed != null) LeavePressed(); }, out unused);
            MiniUi.Place((RectTransform)leave.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(MiniUi.Margin, -48f), new Vector2(220f, MiniUi.SmallButtonHeight));

            // Bottom row: Move · Talk · Mic.
            const float rowY = 96f;
            Button place = MiniUi.Button(safe, "Move", MiniUi.ButtonStyle.Glass, MiniUi.SmallButtonHeight,
                () => pendingPlaceToggle = true, out unused);
            MiniUi.Place((RectTransform)place.transform, new Vector2(0f, 0f), new Vector2(0f, 0.5f),
                new Vector2(MiniUi.Margin, rowY + 110f), new Vector2(230f, MiniUi.SmallButtonHeight));
            placeButton = place.gameObject;

            Button mic = MiniUi.Button(safe, "Mic on", MiniUi.ButtonStyle.Glass, MiniUi.SmallButtonHeight,
                () => pendingMicToggle = true, out micLabel);
            MiniUi.Place((RectTransform)mic.transform, new Vector2(1f, 0f), new Vector2(1f, 0.5f),
                new Vector2(-MiniUi.Margin, rowY + 110f), new Vector2(230f, MiniUi.SmallButtonHeight));
            micButton = mic.gameObject;

            // Centre: a round accent button with a soft ring; hold to talk.
            RectTransform talk = MiniUi.Rect("Talk", safe);
            MiniUi.Place(talk, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, rowY), new Vector2(220f, 260f));
            Image ring = MiniUi.Image(talk, "Ring", MiniUi.CircleSprite, new Color(1f, 1f, 1f, 0.22f));
            MiniUi.Place(ring.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(220f, 220f));
            Image core = MiniUi.Image(ring.transform, "Button", MiniUi.CircleSprite, MiniUi.Accent);
            MiniUi.Place(core.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(176f, 176f));
            core.raycastTarget = true;
            core.gameObject.AddComponent<PressScale>();
            talkButton = core.gameObject.AddComponent<HoldButton>();
            Text talkText = MiniUi.Label(core.transform, "Talk", 40, MiniUi.AccentText, FontStyle.Bold);
            MiniUi.Stretch(talkText.rectTransform);
            Text caption = MiniUi.Label(talk, "hold", 26, MiniUi.TextDim);
            MiniUi.Place(caption.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(220f, 34f));
            talkRoot = talk.gameObject;
        }
    }
}
