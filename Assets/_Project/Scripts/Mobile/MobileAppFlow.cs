using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using Mini.App;
using Mini.UI;

namespace Mini.Mobile
{
    /// <summary>
    /// Phone app flow: Main menu → AR session (place Mini, then talk) → Leave → Main menu.
    /// The AR session, and with it the camera, only runs between Start and Leave.
    /// Mini only starts talking once it has been placed (MiniSettings.talkAfterPlacement).
    /// The Android back button leaves the session, or quits from the menu.
    /// </summary>
    [DisallowMultipleComponent]
    public class MobileAppFlow : MonoBehaviour
    {
        [SerializeField] private MiniCompanionApp app;
        [SerializeField] private ARSession session;
        [SerializeField] private MobileTouchControls controls;
        [SerializeField] private CompanionHud hud;

        private enum FlowScreen { Menu, Session }

        private FlowScreen current;
        private CanvasGroup menu;
        private bool busy;

        private void Awake()
        {
            BuildMenu();
            if (session != null) session.enabled = false;
        }

        private void Start()
        {
            if (controls != null)
            {
                controls.LeavePressed += Leave;
                controls.SetVisible(false);
            }
            if (hud != null) hud.SetGuidanceVisible(false);
            current = FlowScreen.Menu;
        }

        private void OnDestroy()
        {
            if (controls != null) controls.LeavePressed -= Leave;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current; // Android back arrives as Escape
            if (kb == null || !kb.escapeKey.wasPressedThisFrame || busy) return;
            if (current == FlowScreen.Session) Leave();
            else Exit();
        }

        public void StartSession()
        {
            if (busy || current == FlowScreen.Session) return;
            StartCoroutine(StartRoutine());
        }

        public void Leave()
        {
            if (busy || current == FlowScreen.Menu) return;
            StartCoroutine(LeaveRoutine());
        }

        public void Exit()
        {
            Debug.Log("[Mini] Exit");
            Application.Quit();
        }

        private IEnumerator StartRoutine()
        {
            busy = true;
            current = FlowScreen.Session;
            if (session != null) session.enabled = true; // camera starts here (permission prompt on first run)
            if (app != null) app.BeginSession();
            if (controls != null) controls.SetVisible(true);
            if (hud != null) hud.SetGuidanceVisible(true);
            yield return MiniUi.Fade(menu, 0f, 0.35f);
            menu.blocksRaycasts = false;
            menu.gameObject.SetActive(false);
            busy = false;
        }

        private IEnumerator LeaveRoutine()
        {
            busy = true;
            current = FlowScreen.Menu;
            menu.gameObject.SetActive(true);
            menu.blocksRaycasts = true;
            yield return MiniUi.Fade(menu, 1f, 0.3f);
            if (app != null) app.EndSession();
            if (controls != null) controls.SetVisible(false);
            if (hud != null) hud.SetGuidanceVisible(false);
            if (session != null)
            {
                session.Reset();          // forget planes, so the next session starts clean
                session.enabled = false;  // camera off
            }
            busy = false;
        }

        // ------------------------------------------------------------ menu UI

        private void BuildMenu()
        {
            Canvas canvas = MiniUi.CreateCanvas("MiniMainMenu", transform, 50);
            menu = canvas.GetComponent<CanvasGroup>();
            MiniUi.Background(canvas.transform);
            RectTransform safe = MiniUi.SafeArea(canvas.transform);

            Image glow = MiniUi.Glow(safe, 520f);
            MiniUi.Place(glow.rectTransform, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 520f));

            Text title = MiniUi.Label(safe, "Mini", 150, MiniUi.Text, FontStyle.Bold);
            MiniUi.Place(title.rectTransform, new Vector2(0.5f, 0.43f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 190f));

            Text subtitle = MiniUi.Label(safe, "your tiny AI companion", 40, MiniUi.TextDim);
            MiniUi.Place(subtitle.rectTransform, new Vector2(0.5f, 0.43f), new Vector2(0.5f, 1f), new Vector2(0f, -100f), new Vector2(900f, 60f));

            Text unused;
            Button start = MiniUi.Button(safe, "Start", MiniUi.ButtonStyle.Primary, MiniUi.ButtonHeight, StartSession, out unused);
            MiniUi.PlaceWide((RectTransform)start.transform, 0f, 0f, 260f, MiniUi.ButtonHeight, 150f);

            Button exit = MiniUi.Button(safe, "Exit", MiniUi.ButtonStyle.Ghost, MiniUi.ButtonHeight, Exit, out unused);
            MiniUi.PlaceWide((RectTransform)exit.transform, 0f, 0f, 96f, MiniUi.ButtonHeight, 150f);

            Text hint = MiniUi.Label(safe, "Start opens the camera so you can place Mini in your room.", 28, MiniUi.TextDim);
            MiniUi.PlaceWide(hint.rectTransform, 0f, 0f, 420f, 50f, 120f);

            menu.alpha = 1f;
        }
    }
}
