using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UI;
using Mini.MR;
using Mini.UI;

namespace Mini.App
{
    /// <summary>
    /// Quest app flow: Loading → Main menu → Session (place Mini, then talk) → Menu.
    /// Both screens are one floating panel in front of the user, aimed at with a ray from the right
    /// controller and clicked with the right trigger. The loading screen also checks that the Mini
    /// server answers, so a missing server shows up before you try to talk.
    /// The LEFT controller's menu button (Escape in the Editor) goes back to the menu.
    /// Mini only appears and talks after it has been placed (PlacementManager.hideUntilPlaced and
    /// MiniSettings.talkAfterPlacement on), and placement input is off while the menu is open.
    /// </summary>
    [DisallowMultipleComponent]
    public class QuestAppFlow : MonoBehaviour
    {
        [SerializeField] private MiniCompanionApp app;
        [SerializeField] private PlacementManager placement;
        [SerializeField] private CompanionHud hud;
        [Tooltip("The user's head (CenterEyeAnchor).")]
        [SerializeField] private Transform userHead;
        [Tooltip("Ray origin for the menu (RightControllerAnchor).")]
        [SerializeField] private Transform pointer;

        [Header("Layout")]
        [SerializeField] private float distance = 1.15f;
        [SerializeField] private float dropBelowEyes = 0.08f;
        [Tooltip("Keep the loading screen up at least this long so it never just flashes.")]
        [SerializeField] private float minimumLoadingSeconds = 1.8f;
        [SerializeField] private float healthTimeoutSeconds = 3f;

        private const float CanvasScale = 0.0008f; // 1000 x 760 units → 0.8 m x 0.61 m
        private static readonly Vector2 CanvasSize = new Vector2(1000f, 760f);

        private enum FlowScreen { Loading, Menu, Session }

        private FlowScreen current = FlowScreen.Loading;
        private bool busy;

        private Transform panel;
        private CanvasGroup panelGroup, loadingGroup, menuGroup;
        private RectTransform barFill, cursor;
        private Text loadingText, serverText;
        private Button startButton, exitButton;
        private Button hovered;
        private readonly System.Collections.Generic.Dictionary<Button, Color> baseColors =
            new System.Collections.Generic.Dictionary<Button, Color>();
        private LineRenderer laser;
        private Vector3 panelTarget;
        private float snapUntil;

        private InputAction click, back;

        private void Awake()
        {
            if (userHead == null && Camera.main != null) userHead = Camera.main.transform;

            click = new InputAction("MenuClick", InputActionType.Button);
            click.AddBinding("<XRController>{RightHand}/{TriggerButton}");
            click.AddBinding("<Mouse>/leftButton");
            back = new InputAction("MenuBack", InputActionType.Button);
            back.AddBinding("<XRController>{LeftHand}/{MenuButton}");
            back.AddBinding("<Keyboard>/escape");

            BuildPanel();
            BuildLaser();
        }

        private void OnEnable() { click.Enable(); back.Enable(); }
        private void OnDisable() { click.Disable(); back.Disable(); }
        private void OnDestroy() { click.Dispose(); back.Dispose(); }

        private void Start()
        {
            if (placement != null)
            {
                placement.InputEnabled = false;
                if (placement.IsPlacing) placement.ResetPlacement();
            }
            if (hud != null) hud.SetGuidanceVisible(false);
            if (app != null && app.Settings != null && !app.Settings.talkAfterPlacement)
                Debug.LogWarning("[Mini] QuestAppFlow: turn on MiniSettings.talkAfterPlacement, or Mini talks behind the menu.", this);

            StartCoroutine(LoadingRoutine());
        }

        private void Update()
        {
            if (current == FlowScreen.Session)
            {
                if (!busy && BackPressed()) StartCoroutine(LeaveRoutine());
                return;
            }

            FollowHead();
            UpdatePointer();

            if (current == FlowScreen.Menu && !busy && BackPressed()) Exit();
        }

        // ------------------------------------------------------------ flow

        private IEnumerator LoadingRoutine()
        {
            busy = true;
            SnapPanel();
            float started = Time.unscaledTime;

            float shown = 0f;
            loadingText.text = "Waking Mini up…";
            bool? serverOk = null;
            StartCoroutine(CheckServer(ok => serverOk = ok));

            while (true)
            {
                float target = serverOk.HasValue ? 1f : 0.7f;
                float timeShare = Mathf.Clamp01((Time.unscaledTime - started) / minimumLoadingSeconds);
                shown = Mathf.MoveTowards(shown, Mathf.Min(target, timeShare), Time.unscaledDeltaTime * 1.2f);
                SetProgress(shown);
                if (Time.unscaledTime - started > 0.8f && !serverOk.HasValue) loadingText.text = "Connecting to Mini's server…";
                if (shown >= 0.999f && serverOk.HasValue) break;
                yield return null;
            }

            ShowServerStatus(serverOk.Value);
            yield return MiniUi.Fade(loadingGroup, 0f, 0.3f);
            loadingGroup.gameObject.SetActive(false);
            menuGroup.gameObject.SetActive(true);
            menuGroup.alpha = 0f;
            current = FlowScreen.Menu;
            yield return MiniUi.Fade(menuGroup, 1f, 0.35f);
            busy = false;
        }

        public void StartSession()
        {
            if (busy || current != FlowScreen.Menu) return;
            StartCoroutine(StartRoutine());
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
            SetHovered(null);
            SetPointerVisible(false);
            yield return MiniUi.Fade(panelGroup, 0f, 0.3f);
            panel.gameObject.SetActive(false);

            // A frame after the click, so the same trigger press can't also confirm placement.
            yield return null;
            if (placement != null) placement.InputEnabled = true;
            if (hud != null) hud.SetGuidanceVisible(true);
            if (app != null) app.BeginSession();
            Debug.Log("[Mini] Session started");
            busy = false;
        }

        private IEnumerator LeaveRoutine()
        {
            busy = true;
            if (app != null) app.EndSession();
            if (placement != null) placement.InputEnabled = false;
            if (hud != null) hud.SetGuidanceVisible(false);
            Debug.Log("[Mini] Back to menu");

            panel.gameObject.SetActive(true);
            panelGroup.alpha = 0f;
            SnapPanel();
            current = FlowScreen.Menu;
            StartCoroutine(CheckServer(ShowServerStatus));
            yield return MiniUi.Fade(panelGroup, 1f, 0.3f);
            busy = false;
        }

        // ------------------------------------------------------------ server check

        private IEnumerator CheckServer(System.Action<bool> done)
        {
            MiniSettings settings = app != null ? app.Settings : null;
            if (settings == null || settings.serviceMode != ServiceMode.Backend)
            {
                done(true); // offline mode needs no server
                yield break;
            }

            string url = settings.backendUrl.TrimEnd('/') + "/v1/health";
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = Mathf.Max(1, Mathf.RoundToInt(healthTimeoutSeconds));
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success;
                Debug.Log("[Mini] Server check " + url + " → " + (ok ? "OK" : req.error));
                done(ok);
            }
        }

        private void ShowServerStatus(bool ok)
        {
            if (serverText == null) return;
            MiniSettings settings = app != null ? app.Settings : null;
            if (settings != null && settings.serviceMode != ServiceMode.Backend)
            {
                serverText.text = "Offline demo mode";
                serverText.color = MiniUi.TextDim;
            }
            else if (ok)
            {
                serverText.text = "Server connected";
                serverText.color = MiniUi.Accent;
            }
            else
            {
                serverText.text = "Can't reach Mini's server: check the USB cable and run start_mini_server.bat.\n" +
                                  "Mini will use its simple offline voice until then.";
                serverText.color = new Color(1f, 0.72f, 0.45f, 1f);
            }
        }

        // ------------------------------------------------------------ pointing

        private static bool UseTrackedPointer
        {
            get { return !Application.isEditor || UnityEngine.XR.XRSettings.isDeviceActive; }
        }

        private bool TryGetRay(out Ray ray)
        {
            if (UseTrackedPointer && pointer != null)
            {
                ray = new Ray(pointer.position, pointer.forward);
                return true;
            }
            Camera cam = Camera.main;
            if (cam != null && Mouse.current != null)
            {
                ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
                return true;
            }
            ray = default(Ray);
            return false;
        }

        private bool ClickPressed()
        {
            return click.WasPressedThisFrame() ||
                   OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        }

        private bool BackPressed()
        {
            return back.WasPressedThisFrame() || OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch);
        }

        private void UpdatePointer()
        {
            Ray ray;
            if (!panel.gameObject.activeSelf || !TryGetRay(out ray))
            {
                SetPointerVisible(false);
                return;
            }

            Plane plane = new Plane(-panel.forward, panel.position);
            float enter;
            Vector3 end = ray.origin + ray.direction * 3f;
            Button hit = null;
            bool onPanel = false;

            if (plane.Raycast(ray, out enter) && enter < 5f)
            {
                Vector3 point = ray.GetPoint(enter);
                RectTransform rect = (RectTransform)panel;
                Vector2 local = rect.InverseTransformPoint(point);
                if (rect.rect.Contains(local))
                {
                    onPanel = true;
                    end = point;
                    cursor.anchoredPosition = local;
                    if (current == FlowScreen.Menu && !busy)
                    {
                        if (Inside(startButton, point)) hit = startButton;
                        else if (Inside(exitButton, point)) hit = exitButton;
                    }
                }
            }

            SetHovered(hit);
            cursor.gameObject.SetActive(onPanel);
            if (laser != null)
            {
                laser.enabled = UseTrackedPointer;
                laser.SetPosition(0, ray.origin);
                laser.SetPosition(1, end);
            }

            if (hit != null && ClickPressed())
            {
                Debug.Log("[Mini] Menu: " + hit.name);
                hit.onClick.Invoke();
            }
        }

        private static bool Inside(Button button, Vector3 worldPoint)
        {
            RectTransform rect = (RectTransform)button.transform;
            return rect.rect.Contains((Vector2)rect.InverseTransformPoint(worldPoint));
        }

        private void SetHovered(Button button)
        {
            if (hovered == button) return;
            if (hovered != null) Highlight(hovered, false);
            hovered = button;
            if (hovered != null)
            {
                Highlight(hovered, true);
                OVRInput.SetControllerVibration(0.4f, 0.15f, OVRInput.Controller.RTouch);
                StartCoroutine(StopVibration());
            }
        }

        private void Highlight(Button button, bool on)
        {
            button.transform.localScale = Vector3.one * (on ? 1.05f : 1f);
            Color baseColor;
            if (!baseColors.TryGetValue(button, out baseColor)) return;
            Color c = on ? Color.Lerp(baseColor, Color.white, 0.25f) : baseColor;
            if (on) c.a = Mathf.Max(baseColor.a, 0.22f);
            ((Image)button.targetGraphic).color = c;
        }

        private IEnumerator StopVibration()
        {
            yield return new WaitForSecondsRealtime(0.04f);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }

        private void SetPointerVisible(bool visible)
        {
            if (laser != null) laser.enabled = visible;
            if (cursor != null) cursor.gameObject.SetActive(visible);
        }

        // ------------------------------------------------------------ placement of the panel

        private void SnapPanel()
        {
            snapUntil = Time.unscaledTime + 0.6f; // head tracking may not be ready in the first frames
            panelTarget = TargetPosition();
            panel.position = panelTarget;
            FaceUser();
        }

        private Vector3 TargetPosition()
        {
            if (userHead == null) return transform.position + Vector3.forward * distance;
            Vector3 forward = userHead.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            return userHead.position + forward.normalized * distance + Vector3.down * dropBelowEyes;
        }

        /// <summary>Stays put while you look around it; recentres once it drifts well out of view.</summary>
        private void FollowHead()
        {
            if (userHead == null || !panel.gameObject.activeSelf) return;

            if (Time.unscaledTime < snapUntil)
            {
                panelTarget = TargetPosition();
                panel.position = panelTarget;
            }
            else
            {
                Vector3 toPanel = panelTarget - userHead.position;
                Vector3 forward = userHead.forward;
                toPanel.y = 0f;
                forward.y = 0f;
                float distanceNow = toPanel.magnitude;
                if (Vector3.Angle(forward, toPanel) > 40f || distanceNow > distance * 1.8f || distanceNow < distance * 0.5f)
                    panelTarget = TargetPosition();
                panel.position = Vector3.Lerp(panel.position, panelTarget, 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime));
            }
            FaceUser();
        }

        private void FaceUser()
        {
            if (userHead == null) return;
            Vector3 dir = panel.position - userHead.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-4f) panel.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // ------------------------------------------------------------ UI

        private void SetProgress(float t)
        {
            if (barFill != null) barFill.anchorMax = new Vector2(Mathf.Max(0.02f, t), 1f);
        }

        private void BuildPanel()
        {
            GameObject go = new GameObject("MiniQuestMenu", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
            go.transform.SetParent(transform, false);
            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            panel = go.transform;
            ((RectTransform)panel).sizeDelta = CanvasSize;
            panel.localScale = Vector3.one * CanvasScale;
            panelGroup = go.GetComponent<CanvasGroup>();

            // Card: hairline border around an almost opaque navy fill, so it reads well over passthrough.
            Image border = MiniUi.Rounded(panel, "Card", MiniUi.GlassBorder, 56f);
            MiniUi.Stretch(border.rectTransform);
            Image fill = MiniUi.Rounded(border.transform, "Fill", new Color(0.055f, 0.078f, 0.16f, 0.94f), 54f);
            MiniUi.Stretch(fill.rectTransform);
            fill.rectTransform.offsetMin = new Vector2(3f, 3f);
            fill.rectTransform.offsetMax = new Vector2(-3f, -3f);

            Image glow = MiniUi.Glow(panel, 300f);
            Center(glow.rectTransform, 225f, new Vector2(300f, 300f));

            // Loading
            loadingGroup = Group("Loading");
            Text loadingTitle = MiniUi.Label(loadingGroup.transform, "Mini", 120, MiniUi.Text, FontStyle.Bold);
            Center(loadingTitle.rectTransform, 40f, new Vector2(800f, 150f));

            Image track = MiniUi.Rounded(loadingGroup.transform, "Track", new Color(1f, 1f, 1f, 0.12f), 5f);
            Center(track.rectTransform, -120f, new Vector2(520f, 10f));
            Image barImage = MiniUi.Rounded(track.transform, "Fill", MiniUi.Accent, 5f);
            barFill = barImage.rectTransform;
            barFill.anchorMin = Vector2.zero;
            barFill.anchorMax = new Vector2(0.02f, 1f);
            barFill.offsetMin = barFill.offsetMax = Vector2.zero;

            loadingText = MiniUi.Label(loadingGroup.transform, "Waking Mini up…", 32, MiniUi.TextDim);
            Center(loadingText.rectTransform, -185f, new Vector2(800f, 50f));

            // Menu
            menuGroup = Group("Menu");
            Text title = MiniUi.Label(menuGroup.transform, "Mini", 120, MiniUi.Text, FontStyle.Bold);
            Center(title.rectTransform, 50f, new Vector2(800f, 150f));
            Text subtitle = MiniUi.Label(menuGroup.transform, "your tiny AI companion", 34, MiniUi.TextDim);
            Center(subtitle.rectTransform, -25f, new Vector2(800f, 50f));

            Text unused;
            startButton = MiniUi.Button(menuGroup.transform, "Start", MiniUi.ButtonStyle.Primary, 104f, StartSession, out unused);
            Center((RectTransform)startButton.transform, -120f, new Vector2(520f, 104f));
            exitButton = MiniUi.Button(menuGroup.transform, "Exit", MiniUi.ButtonStyle.Ghost, 104f, Exit, out unused);
            Center((RectTransform)exitButton.transform, -240f, new Vector2(520f, 104f));
            // The ray drives hover/press here, not the EventSystem: drop the touch press effect, keep the colours.
            foreach (Button b in new[] { startButton, exitButton })
            {
                PressScale press = b.GetComponent<PressScale>();
                if (press != null) Destroy(press);
                b.transition = Selectable.Transition.None;
                baseColors[b] = ((Image)b.targetGraphic).color;
            }

            serverText = MiniUi.Label(menuGroup.transform, string.Empty, 24, MiniUi.TextDim);
            Center(serverText.rectTransform, -335f, new Vector2(900f, 70f));

            menuGroup.gameObject.SetActive(false);

            // Pointer dot, drawn on top of everything.
            Image dot = MiniUi.Image(panel, "Cursor", MiniUi.CircleSprite, Color.white);
            cursor = dot.rectTransform;
            cursor.sizeDelta = new Vector2(18f, 18f);
            cursor.gameObject.SetActive(false);
        }

        private CanvasGroup Group(string name)
        {
            RectTransform rect = MiniUi.Rect(name, panel);
            MiniUi.Stretch(rect);
            CanvasGroup group = rect.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            return group;
        }

        private static void Center(RectTransform rect, float y, Vector2 size)
        {
            MiniUi.Place(rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), size);
        }

        private void BuildLaser()
        {
            Shader shader = Shader.Find("UI/Default"); // always in the build (UGUI)
            if (shader == null) return;
            GameObject go = new GameObject("MenuLaser", typeof(LineRenderer));
            go.transform.SetParent(transform, false);
            laser = go.GetComponent<LineRenderer>();
            laser.material = new Material(shader);
            laser.useWorldSpace = true;
            laser.positionCount = 2;
            laser.startWidth = 0.004f;
            laser.endWidth = 0.002f;
            laser.startColor = new Color(MiniUi.Accent.r, MiniUi.Accent.g, MiniUi.Accent.b, 0.9f);
            laser.endColor = new Color(MiniUi.Accent.r, MiniUi.Accent.g, MiniUi.Accent.b, 0.25f);
            laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            laser.receiveShadows = false;
            laser.enabled = false;
        }
    }
}
