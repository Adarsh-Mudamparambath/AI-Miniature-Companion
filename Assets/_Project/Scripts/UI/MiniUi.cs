using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Mini.UI
{
    /// <summary>
    /// Mini's small design system for screen UI, built in code (no art assets needed):
    /// one dark navy palette, one cyan accent (Mini's status light), rounded "pill" shapes,
    /// frosted-glass panels for use over the camera, and generous spacing.
    /// Layout units are a 1080 x 1920 portrait reference canvas.
    /// </summary>
    public static class MiniUi
    {
        // ------------------------------------------------------------ tokens

        public static readonly Color BackgroundTop = Hex(0x0E1428);
        public static readonly Color BackgroundBottom = Hex(0x05070F);
        public static readonly Color Accent = Hex(0x46E3F2);
        public static readonly Color AccentText = Hex(0x04121A);
        public static readonly Color Text = new Color(1f, 1f, 1f, 0.96f);
        public static readonly Color TextDim = new Color(1f, 1f, 1f, 0.58f);
        public static readonly Color Glass = new Color(0.06f, 0.08f, 0.14f, 0.62f);
        public static readonly Color GlassBorder = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Ghost = new Color(1f, 1f, 1f, 0.08f);

        public const float ButtonHeight = 132f;
        public const float SmallButtonHeight = 104f;
        public const float Margin = 56f;

        public static Font Font
        {
            get
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return font;
            }
        }

        public enum ButtonStyle { Primary, Glass, Ghost }

        // ------------------------------------------------------------ canvases and layout

        public static Canvas CreateCanvas(string name, Transform parent, int sortingOrder)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster), typeof(CanvasGroup));
            if (parent != null) go.transform.SetParent(parent, false);
            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        /// <summary>A full-screen child that keeps content inside the screen's safe area (notches, gesture bar).</summary>
        public static RectTransform SafeArea(Transform parent)
        {
            RectTransform rect = Rect("SafeArea", parent);
            Stretch(rect);
            rect.gameObject.AddComponent<SafeAreaFitter>();
            return rect;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Anchor to a point of the parent (0..1) with a size and an offset.</summary>
        public static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 offset, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
        }

        /// <summary>Stretch horizontally inside margins, anchored at a vertical position.</summary>
        public static void PlaceWide(RectTransform rect, float anchorY, float pivotY, float offsetY, float height, float margin)
        {
            rect.anchorMin = new Vector2(0f, anchorY);
            rect.anchorMax = new Vector2(1f, anchorY);
            rect.pivot = new Vector2(0.5f, pivotY);
            rect.anchoredPosition = new Vector2(0f, offsetY);
            rect.sizeDelta = new Vector2(-2f * margin, height);
        }

        // ------------------------------------------------------------ elements

        public static Image Background(Transform parent)
        {
            Image img = Image(parent, "Background", GradientSprite, Color.white);
            Stretch(img.rectTransform);
            img.raycastTarget = true; // blocks taps from reaching the camera view behind
            return img;
        }

        public static Image Image(Transform parent, string name, Sprite sprite, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>A rounded rectangle with the given corner radius (in reference pixels).</summary>
        public static Image Rounded(Transform parent, string name, Color color, float radius)
        {
            Image img = Image(parent, name, RoundedSprite, color);
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = RoundedRadiusPx / Mathf.Max(1f, radius);
            return img;
        }

        /// <summary>Frosted panel for use over the camera feed: dark glass with a hairline border.</summary>
        public static Image GlassPanel(Transform parent, string name, float radius)
        {
            Image border = Rounded(parent, name, GlassBorder, radius);
            Image fill = Rounded(border.transform, "Fill", Glass, radius - 2f);
            RectTransform f = fill.rectTransform;
            Stretch(f);
            f.offsetMin = new Vector2(2f, 2f);
            f.offsetMax = new Vector2(-2f, -2f);
            return border;
        }

        public static Text Label(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal,
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text t = go.GetComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.lineSpacing = 1.1f;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string label, ButtonStyle style, float height, UnityAction onClick,
            out Text text)
        {
            Color fill = style == ButtonStyle.Primary ? Accent : style == ButtonStyle.Ghost ? Ghost : Glass;
            Image bg = style == ButtonStyle.Glass ? GlassPanel(parent, label, height * 0.5f) : Rounded(parent, label, fill, height * 0.5f);
            bg.raycastTarget = true;
            if (style == ButtonStyle.Glass) bg.transform.GetChild(0).GetComponent<Image>().raycastTarget = false;

            Button button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.gameObject.AddComponent<PressScale>();
            if (onClick != null) button.onClick.AddListener(onClick);

            text = Label(bg.transform, label, Mathf.RoundToInt(height * 0.32f),
                style == ButtonStyle.Primary ? AccentText : Text, FontStyle.Bold);
            Stretch(text.rectTransform);
            return button;
        }

        /// <summary>A soft, glowing orb in Mini's accent colour (the "eye" on the menu and loading screens).</summary>
        public static Image Glow(Transform parent, float size)
        {
            Image halo = Image(parent, "Glow", GlowSprite, new Color(Accent.r, Accent.g, Accent.b, 0.55f));
            halo.rectTransform.sizeDelta = new Vector2(size, size);
            Image core = Image(halo.transform, "Core", CircleSprite, Accent);
            core.rectTransform.sizeDelta = new Vector2(size * 0.22f, size * 0.22f);
            halo.gameObject.AddComponent<Breathe>();
            return halo;
        }

        public static IEnumerator Fade(CanvasGroup group, float to, float seconds)
        {
            if (group == null) yield break;
            float from = group.alpha;
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                group.alpha = Mathf.Lerp(from, to, Smooth(t / seconds));
                yield return null;
            }
            group.alpha = to;
        }

        public static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // ------------------------------------------------------------ generated sprites

        private const int RoundedSize = 128;
        private const float RoundedRadiusPx = 64f;

        private static Font font;
        private static Sprite roundedSprite, circleSprite, glowSprite, gradientSprite;

        public static Sprite RoundedSprite
        {
            get
            {
                if (roundedSprite == null)
                {
                    Texture2D tex = MakeTexture(RoundedSize, RoundedSize, (x, y) =>
                    {
                        float half = RoundedSize * 0.5f;
                        float dx = Mathf.Max(Mathf.Abs(x - half + 0.5f) - (half - RoundedRadiusPx), 0f);
                        float dy = Mathf.Max(Mathf.Abs(y - half + 0.5f) - (half - RoundedRadiusPx), 0f);
                        float d = Mathf.Sqrt(dx * dx + dy * dy) - RoundedRadiusPx;
                        return new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
                    });
                    float b = RoundedRadiusPx;
                    roundedSprite = Sprite.Create(tex, new Rect(0, 0, RoundedSize, RoundedSize), new Vector2(0.5f, 0.5f),
                        100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
                }
                return roundedSprite;
            }
        }

        public static Sprite CircleSprite
        {
            get
            {
                if (circleSprite == null)
                {
                    const int n = 128;
                    Texture2D tex = MakeTexture(n, n, (x, y) =>
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) - n * 0.5f + 1f;
                        return new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
                    });
                    circleSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
                }
                return circleSprite;
            }
        }

        public static Sprite GlowSprite
        {
            get
            {
                if (glowSprite == null)
                {
                    const int n = 256;
                    Texture2D tex = MakeTexture(n, n, (x, y) =>
                    {
                        float r = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) / (n * 0.5f);
                        float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2.4f);
                        return new Color(1f, 1f, 1f, a);
                    });
                    glowSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
                }
                return glowSprite;
            }
        }

        public static Sprite GradientSprite
        {
            get
            {
                if (gradientSprite == null)
                {
                    const int h = 256;
                    Texture2D tex = MakeTexture(4, h, (x, y) => Color.Lerp(BackgroundBottom, BackgroundTop, Smooth(y / (h - 1f))));
                    gradientSprite = Sprite.Create(tex, new Rect(0, 0, 4, h), new Vector2(0.5f, 0.5f));
                }
                return gradientSprite;
            }
        }

        private static Texture2D MakeTexture(int w, int h, Func<int, int, Color> pixel)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color[] px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = pixel(x, y);
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Color Hex(int rgb)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }
    }

    /// <summary>Keeps a full-screen RectTransform inside Screen.safeArea.</summary>
    public class SafeAreaFitter : MonoBehaviour
    {
        private Rect applied;

        private void Update()
        {
            Rect safe = Screen.safeArea;
            if (safe == applied || Screen.width <= 0 || Screen.height <= 0) return;
            applied = safe;
            RectTransform rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            rect.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }

    /// <summary>Gentle "breathing" pulse (scale + alpha) for glowing elements.</summary>
    public class Breathe : MonoBehaviour
    {
        public float speed = 1.4f;
        public float amount = 0.06f;
        private Graphic graphic;
        private float baseAlpha;

        private void Awake()
        {
            graphic = GetComponent<Graphic>();
            if (graphic != null) baseAlpha = graphic.color.a;
        }

        private void Update()
        {
            float s = Mathf.Sin(Time.unscaledTime * speed);
            transform.localScale = Vector3.one * (1f + amount * s);
            if (graphic != null)
            {
                Color c = graphic.color;
                c.a = baseAlpha * (0.85f + 0.15f * s);
                graphic.color = c;
            }
        }
    }

    /// <summary>Buttons shrink slightly while pressed.</summary>
    public class PressScale : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IPointerUpHandler
    {
        private float target = 1f;

        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { target = 0.95f; }
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) { target = 1f; }
        private void OnDisable() { target = 1f; transform.localScale = Vector3.one; }

        private void Update()
        {
            float s = Mathf.Lerp(transform.localScale.x, target, 1f - Mathf.Exp(-25f * Time.unscaledDeltaTime));
            transform.localScale = new Vector3(s, s, 1f);
        }
    }
}
