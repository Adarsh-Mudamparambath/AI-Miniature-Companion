using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Mini.UI;

namespace Mini.Mobile
{
    /// <summary>
    /// Loading screen (first scene of the phone build): shows Mini's glow and a thin progress bar while
    /// the main scene loads in the background, then fades out over the main menu.
    /// </summary>
    public class MobileBoot : MonoBehaviour
    {
        [SerializeField] private string mainScene = "Mini_Phone_AR";
        [Tooltip("Keep the loading screen up at least this long so it never just flashes.")]
        [SerializeField] private float minimumSeconds = 1.6f;

        private RectTransform barFill;
        private CanvasGroup group;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            BuildUi();
        }

        private IEnumerator Start()
        {
            float started = Time.unscaledTime;
            AsyncOperation load = SceneManager.LoadSceneAsync(mainScene);
            if (load == null)
            {
                Debug.LogError("[Mini] Scene '" + mainScene + "' is not in the build.");
                yield break;
            }
            load.allowSceneActivation = false;

            float shown = 0f;
            while (true)
            {
                float target = Mathf.Clamp01(load.progress / 0.9f);
                float timeShare = Mathf.Clamp01((Time.unscaledTime - started) / minimumSeconds);
                shown = Mathf.MoveTowards(shown, Mathf.Min(target, timeShare), Time.unscaledDeltaTime * 1.5f);
                SetProgress(shown);
                if (shown >= 0.999f && load.progress >= 0.9f) break;
                yield return null;
            }

            // Survive the scene switch so the loading screen can fade out over the menu.
            transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
            load.allowSceneActivation = true;
            while (!load.isDone) yield return null;
            yield return null;
            yield return MiniUi.Fade(group, 0f, 0.45f);
            Destroy(gameObject);
        }

        private void SetProgress(float t)
        {
            if (barFill != null) barFill.anchorMax = new Vector2(Mathf.Max(0.02f, t), 1f);
        }

        private void BuildUi()
        {
            Canvas canvas = MiniUi.CreateCanvas("MiniLoading", transform, 100);
            group = canvas.GetComponent<CanvasGroup>();
            MiniUi.Background(canvas.transform);
            RectTransform safe = MiniUi.SafeArea(canvas.transform);

            Image glow = MiniUi.Glow(safe, 520f);
            MiniUi.Place(glow.rectTransform, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 520f));

            Text title = MiniUi.Label(safe, "Mini", 150, MiniUi.Text, FontStyle.Bold);
            MiniUi.Place(title.rectTransform, new Vector2(0.5f, 0.43f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 190f));

            // Thin rounded progress track near the bottom.
            Image track = MiniUi.Rounded(safe, "Track", new Color(1f, 1f, 1f, 0.12f), 4f);
            MiniUi.PlaceWide(track.rectTransform, 0f, 0f, 260f, 8f, 260f);
            Image fill = MiniUi.Rounded(track.transform, "Fill", MiniUi.Accent, 4f);
            barFill = fill.rectTransform;
            barFill.anchorMin = Vector2.zero;
            barFill.anchorMax = new Vector2(0.02f, 1f);
            barFill.offsetMin = barFill.offsetMax = Vector2.zero;

            Text loading = MiniUi.Label(safe, "Waking Mini up…", 30, MiniUi.TextDim);
            MiniUi.PlaceWide(loading.rectTransform, 0f, 0f, 300f, 50f, 120f);
        }
    }
}
