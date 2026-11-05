using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// A short world-space message ("Iniciando...", "Inicio fijado aquí"): placed once 1.5 m in
// front of the head at eye level and left there in the world - not head-locked - then it
// fades out and destroys itself. It belongs to the active scene, so a scene change takes it
// along too. On the UI layer, no raycast targets.
public static class FloatingMessage
{
    const float Distance = 1.5f;
    const float HoldSeconds = 0.5f;
    const float FadeSeconds = 1f;
    const float WorldScale = 0.002f;
    const int FontSize = 64;
    const float MinWidth = 620f;
    const float Height = 140f;
    const float HorizontalPadding = 50f;
    const float LongWidth = 1100f;
    const float LongDrop = 0.25f;
    const int LongFontSize = 42;
    static readonly Color TextColor = new Color(1f, 1f, 1f, 1f);
    static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.12f, 0.75f);

    static Font font;

    public static void Show(string text) => Show(text, HoldSeconds, 0f, 0f);

    // Something to read: a smaller font, wrapped, held for this long, under eye level.
    public static void ShowLong(string text, float seconds) => Show(text, seconds, LongWidth, LongDrop);

    static void Show(string text, float holdSeconds, float wrapWidth, float drop)
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        if (font == null)
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        Vector3 forward = head.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        var messageObject = new GameObject("FloatingMessage", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Image), typeof(FadeOut));
        SceneManager.MoveGameObjectToScene(messageObject, SceneManager.GetActiveScene());

        Canvas canvas = messageObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;

        var rect = (RectTransform)messageObject.transform;
        rect.sizeDelta = new Vector2(MinWidth, Height);
        rect.SetPositionAndRotation(head.transform.position + forward * Distance + Vector3.down * drop, Quaternion.LookRotation(forward, Vector3.up));
        rect.localScale = Vector3.one * WorldScale;

        Image background = messageObject.GetComponent<Image>();
        background.color = BackgroundColor;
        background.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(rect, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text label = textObject.GetComponent<Text>();
        label.font = font;
        label.text = text;
        label.fontSize = wrapWidth > 0f ? LongFontSize : FontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = TextColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;

        // One line, and the box grows to fit it: a fixed width cut longer messages ("Te
        // regresamos al inicio") down to their first line.
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        rect.sizeDelta = new Vector2(Mathf.Max(MinWidth, label.preferredWidth + HorizontalPadding * 2f), Height);

        if (wrapWidth > 0f)
        {
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            rect.sizeDelta = new Vector2(wrapWidth, Height);
            textRect.offsetMin = new Vector2(HorizontalPadding, 0f);
            textRect.offsetMax = new Vector2(-HorizontalPadding, 0f);
            rect.sizeDelta = new Vector2(wrapWidth, Mathf.Max(Height, label.preferredHeight + HorizontalPadding));
        }

        messageObject.GetComponent<FadeOut>().holdSeconds = holdSeconds;

        XRUISetup.MoveToUILayer(messageObject);
    }

    // Holds, fades the CanvasGroup out, then destroys the message.
    class FadeOut : MonoBehaviour
    {
        public float holdSeconds = HoldSeconds;

        CanvasGroup group;
        float elapsed;

        void Awake()
        {
            group = GetComponent<CanvasGroup>();
        }

        void Update()
        {
            elapsed += Time.deltaTime;
            if (elapsed < holdSeconds)
                return;

            float t = (elapsed - holdSeconds) / FadeSeconds;
            group.alpha = 1f - Mathf.Clamp01(t);
            if (t >= 1f)
                Destroy(gameObject);
        }
    }
}
