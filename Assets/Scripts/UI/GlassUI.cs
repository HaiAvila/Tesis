using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The app's glass look: dark, smoky glass with a light rim, cyan for what's on or under the ray,
// Inter for text, Material Symbols Rounded for icons. The glass is drawn, not blurred - a real
// background blur costs too much on the Quest - so its shapes are generated here once, in white
// and grey, at a resolution that stays sharp up close.
public static class GlassUI
{
    public static readonly Color Accent = new Color(0.12f, 0.71f, 0.92f, 1f);
    public static readonly Color AccentHover = new Color(0.12f, 0.71f, 0.92f, 0.32f);
    public static readonly Color TextColor = new Color(0.96f, 0.97f, 0.99f, 1f);
    public static readonly Color SubTextColor = new Color(0.72f, 0.77f, 0.85f, 1f);
    public static readonly Color Danger = new Color(0.95f, 0.38f, 0.38f, 1f);
    // Text on a red (Danger) button: light, so it reads on the red.
    public static readonly Color DangerText = new Color(1f, 0.86f, 0.86f, 1f);
    public static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.16f);

    // Material Symbols Rounded, by codepoint.
    public static class Icon
    {
        public const string Play = "", Pause = "", Replay = "", Repeat = "", Camera = "",
            Close = "", Conversation = "", NotesAudio = "", Delete = "", Mic = "",
            MicOff = "", Home = "", Scenes = "", Favorite = "", Calibrate = "",
            Notes = "", Export = "", Pods = "", Tutorial = "", Speed = "",
            Record = "", WifiOff = "", Movie = "", Save = "", PlayCircle = "",
            Check = "", Back = "", Previous = "", Next = "", Star = "";
    }

    static TMP_FontAsset regular, semiBold, icons;
    public static TMP_FontAsset Regular => regular != null ? regular : regular = WithIcons(Resources.Load<TMP_FontAsset>("UI/Inter-Regular SDF"));
    public static TMP_FontAsset SemiBold => semiBold != null ? semiBold : semiBold = WithIcons(Resources.Load<TMP_FontAsset>("UI/Inter-SemiBold SDF"));

    // Inter falls back to the icon font, so an icon's codepoint can sit inside ordinary text
    // (the preferred take's star in Scene Select).
    static TMP_FontAsset WithIcons(TMP_FontAsset font)
    {
        if (font == null || Icons == null)
            return font;
        if (font.fallbackFontAssetTable == null)
            font.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
        if (!font.fallbackFontAssetTable.Contains(Icons))
            font.fallbackFontAssetTable.Add(Icons);
        return font;
    }
    public static TMP_FontAsset Icons => icons != null ? icons : icons = Resources.Load<TMP_FontAsset>("UI/MaterialSymbolsRounded SDF");

    // --- generated shapes --------------------------------------------------------------------------

    const int ShapeSize = 128;
    const float ShapeRadius = 48f;      // the rounded rect's corner, in texture pixels
    const int ShapeBorder = 56;         // 9-slice border, a little past the corner

    static Sprite glassPanel, glassCircle, whiteCircle, whiteRounded, shadow;

    // A glass rounded rectangle, 9-sliced. Image.pixelsPerUnitMultiplier sets the corner (see Glass).
    public static Sprite GlassPanelSprite => glassPanel != null ? glassPanel : glassPanel = Shape("GlassPanel", false, true);
    public static Sprite GlassCircleSprite => glassCircle != null ? glassCircle : glassCircle = Shape("GlassCircle", true, true);
    public static Sprite WhiteCircleSprite => whiteCircle != null ? whiteCircle : whiteCircle = Shape("WhiteCircle", true, false);
    public static Sprite WhiteRoundedSprite => whiteRounded != null ? whiteRounded : whiteRounded = Shape("WhiteRounded", false, false);
    public static Sprite ShadowSprite => shadow != null ? shadow : shadow = Shadow();

    static Sprite Shape(string name, bool circle, bool glass)
    {
        var texture = new Texture2D(ShapeSize, ShapeSize, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp };
        float half = ShapeSize / 2f;
        float radius = circle ? half - 1f : ShapeRadius;
        var pixels = new Color[ShapeSize * ShapeSize];
        for (int y = 0; y < ShapeSize; y++)
        {
            for (int x = 0; x < ShapeSize; x++)
            {
                // Signed distance to the shape's edge (negative inside).
                var p = new Vector2(x + 0.5f - half, y + 0.5f - half);
                float distance;
                if (circle)
                {
                    distance = p.magnitude - radius;
                }
                else
                {
                    var q = new Vector2(Mathf.Abs(p.x) - (half - 1f - radius), Mathf.Abs(p.y) - (half - 1f - radius));
                    distance = Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
                }

                float coverage = Mathf.Clamp01(0.5f - distance);
                if (coverage <= 0f)
                {
                    pixels[y * ShapeSize + x] = Color.clear;
                    continue;
                }

                if (!glass)
                {
                    pixels[y * ShapeSize + x] = new Color(1f, 1f, 1f, coverage);
                    continue;
                }

                // Smoky glass, a touch lighter at the top, with a light rim that fades downwards.
                float height = (float)y / ShapeSize;  // 0 bottom, 1 top
                Color fill = Color.Lerp(new Color(0.06f, 0.07f, 0.1f, 0.66f), new Color(0.17f, 0.19f, 0.24f, 0.62f), height);
                float rim = Mathf.Clamp01(1f - Mathf.Abs(distance + 1.2f) / 1.4f);
                float rimStrength = Mathf.Lerp(0.12f, 0.42f, height);
                Color color = Color.Lerp(fill, new Color(1f, 1f, 1f, 0.9f), rim * rimStrength);
                color.a = Mathf.Max(color.a, rim * rimStrength) * coverage;
                pixels[y * ShapeSize + x] = color;
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        Vector4 border = circle ? Vector4.zero : new Vector4(ShapeBorder, ShapeBorder, ShapeBorder, ShapeBorder);
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, ShapeSize, ShapeSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.name = name;
        UnityEngine.Object.DontDestroyOnLoad(texture);
        return sprite;
    }

    // A soft dark halo, 9-sliced, under a panel so it reads over a bright sky.
    static Sprite Shadow()
    {
        const int size = 128, edge = 40;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Shadow", wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0f, Mathf.Max(edge - x, x - (size - 1 - edge)));
                float dy = Mathf.Max(0f, Mathf.Max(edge - y, y - (size - 1 - edge)));
                float t = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / edge);
                pixels[y * size + x] = new Color(0f, 0f, 0f, 0.45f * (1f - t) * (1f - t));
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
        UnityEngine.Object.DontDestroyOnLoad(texture);
        return sprite;
    }

    // --- building blocks -------------------------------------------------------------------------

    // Makes `image` a glass panel whose corners have `radius` canvas units.
    public static void Glass(Image image, float radius)
    {
        image.sprite = GlassPanelSprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = ShapeRadius / Mathf.Max(1f, radius);
        image.color = Color.white;
    }

    public static void Rounded(Image image, float radius, Color color)
    {
        image.sprite = WhiteRoundedSprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = ShapeRadius / Mathf.Max(1f, radius);
        image.color = color;
    }

    // A soft shadow behind `panel`, as its first child so it draws underneath everything else.
    public static void AddShadow(RectTransform panel, float spread)
    {
        var shadowObject = new GameObject("Shadow", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        shadowObject.transform.SetParent(panel, false);
        shadowObject.transform.SetAsFirstSibling();
        shadowObject.GetComponent<LayoutElement>().ignoreLayout = true;
        var rect = (RectTransform)shadowObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(-spread, -spread * 1.3f);
        rect.offsetMax = new Vector2(spread, spread * 0.7f);
        Image image = shadowObject.GetComponent<Image>();
        image.sprite = ShadowSprite;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 40f / Mathf.Max(1f, spread);
        image.raycastTarget = false;
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float size, bool semiBold = false, Color? color = null,
        TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(parent, false);
        var label = labelObject.GetComponent<TextMeshProUGUI>();
        label.font = semiBold ? SemiBold : Regular;
        label.text = text;
        label.fontSize = size;
        label.color = color ?? TextColor;
        label.alignment = alignment;
        label.raycastTarget = false;
        label.enableWordWrapping = true;
        return label;
    }

    public static TextMeshProUGUI IconLabel(Transform parent, string glyph, float size, Color? color = null)
    {
        TextMeshProUGUI label = Label(parent, glyph, size, false, color);
        label.font = Icons;
        label.enableWordWrapping = false;
        return label;
    }

    public static void Stretch(RectTransform rect, float inset = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    public enum ButtonStyle { Normal, Primary, Danger, Row, RowAccent, RowEmpty }

    static Color StyleTint(ButtonStyle style)
    {
        switch (style)
        {
            case ButtonStyle.Primary: return new Color(Accent.r, Accent.g, Accent.b, 0.92f);
            case ButtonStyle.Danger: return new Color(0.9f, 0.25f, 0.25f, 0.45f);
            case ButtonStyle.Row: return new Color(1f, 1f, 1f, 0.07f);
            case ButtonStyle.RowAccent: return new Color(Accent.r, Accent.g, Accent.b, 0.22f);
            case ButtonStyle.RowEmpty: return new Color(1f, 1f, 1f, 0.025f);
            default: return new Color(1f, 1f, 1f, 0.1f);
        }
    }

    // A panel's background: glass with rounded corners and a soft shadow under it.
    public static void SkinPanel(Image background, float radius, float shadowSpread = 30f)
    {
        Glass(background, radius);
        background.raycastTarget = false;
        AddShadow((RectTransform)background.transform.parent, shadowSpread);
    }

    // An existing Button (with its Image) restyled: a rounded, softly tinted shape, a cyan tint
    // under the ray, and no Unity color transition. Returns its RoundButton (Active = cyan).
    public static RoundButton SkinButton(GameObject buttonObject, float radius, ButtonStyle style = ButtonStyle.Normal)
    {
        Image image = buttonObject.GetComponent<Image>();
        Rounded(image, radius, StyleTint(style));
        Button button = buttonObject.GetComponent<Button>();
        if (button != null)
            button.transition = Selectable.Transition.None;

        RoundButton round = buttonObject.GetComponent<RoundButton>();
        if (round != null)
            return round;

        var overlayObject = new GameObject("State", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        overlayObject.transform.SetParent(buttonObject.transform, false);
        overlayObject.transform.SetAsFirstSibling();
        overlayObject.GetComponent<LayoutElement>().ignoreLayout = true;
        Stretch((RectTransform)overlayObject.transform);
        Image overlay = overlayObject.GetComponent<Image>();
        Rounded(overlay, radius, Color.clear);
        overlay.raycastTarget = false;

        round = buttonObject.AddComponent<RoundButton>();
        round.Init(overlay, null, false, 0f);
        round.HoverScale = 1.02f;
        foreach (TMP_Text text in buttonObject.GetComponentsInChildren<TMP_Text>(true))
        {
            if (style == ButtonStyle.Danger)
                text.color = DangerText;
            if (style == ButtonStyle.Primary)
                text.fontStyle |= FontStyles.Bold;
        }
        return round;
    }

    // A small glass chip fixed in the view (head-locked) at `offset` from the eyes, for the
    // things that stay up while you work: REC, the pods, the take, the connection. Its text and
    // icon can be changed afterwards. pivotX: 0 grows to the right of offset, 1 to the left.
    public const float ChipScale = 0.00032f;
    const float ChipHeight = 64f;
    // From one chip's centre to the next one's, stacked in a corner.
    public const float ChipSpacing = ChipHeight * ChipScale + 0.005f;

    public static HudChip HudChip(Transform head, string name, Vector3 offset, string glyph, string text, Color iconColor, float pivotX)
    {
        const float scale = ChipScale;   // 1 unit = 0.32 mm, at offset.z (0.5 m) from the eyes
        const float height = ChipHeight;
        const float iconSize = 34f;
        const float fontSize = 28f;
        const float padding = 20f;

        var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(Image));
        root.transform.SetParent(head, false);
        root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)root.transform;
        rect.pivot = new Vector2(pivotX, 0.5f);
        rect.localPosition = offset;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * scale;
        Image background = root.GetComponent<Image>();
        Glass(background, height / 2f);
        background.raycastTarget = false;

        HudChip chip = root.AddComponent<HudChip>();
        chip.Init(rect, glyph != null ? IconLabel(rect, glyph, iconSize, iconColor) : null, Label(rect, text, fontSize, true), height, padding, iconSize);
        XRUISetup.MoveToUILayer(root);
        return chip;
    }

    // A round glass button with an icon (a glyph, or a sprite for the star and heart) and a label
    // that appears on hover, above or below it.
    public static RoundButton RoundButton(Transform parent, float size, string glyph, string tooltip, Action onClick, bool tooltipBelow = false)
    {
        var buttonObject = new GameObject($"Button_{tooltip}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(RoundButton));
        buttonObject.transform.SetParent(parent, false);
        var rect = (RectTransform)buttonObject.transform;
        rect.sizeDelta = new Vector2(size, size);

        Image background = buttonObject.GetComponent<Image>();
        background.sprite = GlassCircleSprite;
        background.color = Color.white;

        Button button = buttonObject.GetComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.targetGraphic = background;
        if (onClick != null)
            button.onClick.AddListener(() => onClick());

        var overlayObject = new GameObject("State", typeof(RectTransform), typeof(Image));
        overlayObject.transform.SetParent(rect, false);
        Stretch((RectTransform)overlayObject.transform, 2f);
        Image overlay = overlayObject.GetComponent<Image>();
        overlay.sprite = WhiteCircleSprite;
        overlay.color = Color.clear;
        overlay.raycastTarget = false;

        RoundButton round = buttonObject.GetComponent<RoundButton>();
        round.Init(overlay, tooltip, tooltipBelow, size);
        if (glyph != null)
        {
            round.Icon = IconLabel(rect, glyph, size * 0.5f);
            Stretch((RectTransform)round.Icon.transform);
        }
        return round;
    }
}

// The state of a GlassUI.RoundButton: cyan when on (a toggle that's active), a cyan tint under the
// ray, and its label shown while hovered.
public class RoundButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    const float LabelGap = 12f;
    const float LabelHeight = 44f;
    const float LabelFontSize = 24f;
    const float LabelPadding = 18f;

    Image overlay;
    GameObject tooltip;
    bool hovered;
    bool active;

    public float HoverScale { get; set; } = 1.06f;

    public TextMeshProUGUI Icon { get; set; }
    public Image SpriteIcon { get; set; }

    public bool Active
    {
        get => active;
        set
        {
            active = value;
            Refresh();
        }
    }

    public void Init(Image stateOverlay, string label, bool below, float size)
    {
        overlay = stateOverlay;
        if (string.IsNullOrEmpty(label))
            return;

        // Its own canvas, sorted above the row, so neighbouring buttons don't cover it.
        tooltip = new GameObject("Tooltip", typeof(RectTransform), typeof(Canvas), typeof(Image), typeof(LayoutElement));
        tooltip.transform.SetParent(transform, false);
        tooltip.GetComponent<LayoutElement>().ignoreLayout = true;
        Canvas canvas = tooltip.GetComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = 20;

        TextMeshProUGUI text = GlassUI.Label(tooltip.transform, label, LabelFontSize, true);
        text.enableWordWrapping = false;
        float width = text.GetPreferredValues(label).x + LabelPadding * 2f;
        GlassUI.Stretch((RectTransform)text.transform);

        var rect = (RectTransform)tooltip.transform;
        rect.sizeDelta = new Vector2(width, LabelHeight);
        float offset = size / 2f + LabelGap + LabelHeight / 2f;
        rect.anchoredPosition = new Vector2(0f, below ? -offset : offset);
        Image background = tooltip.GetComponent<Image>();
        GlassUI.Glass(background, LabelHeight / 2f);
        background.raycastTarget = false;
        tooltip.SetActive(false);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovered = true;
        Refresh();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovered = false;
        Refresh();
    }

    void OnDisable()
    {
        hovered = false;
        Refresh();
    }

    void Refresh()
    {
        if (overlay != null)
            overlay.color = active ? GlassUI.Accent : hovered ? GlassUI.AccentHover : Color.clear;
        if (tooltip != null)
            tooltip.SetActive(hovered && isActiveAndEnabled);
        transform.localScale = Vector3.one * (hovered ? HoverScale : 1f);
    }
}

// A GlassUI.HudChip: sizes itself to its text.
public class HudChip : MonoBehaviour
{
    RectTransform rect;
    TextMeshProUGUI icon, label;
    float height, padding, iconSize;

    public TextMeshProUGUI Icon => icon;
    public TextMeshProUGUI Label => label;

    public void Init(RectTransform chipRect, TextMeshProUGUI iconLabel, TextMeshProUGUI textLabel, float chipHeight, float chipPadding, float chipIconSize)
    {
        rect = chipRect;
        icon = iconLabel;
        label = textLabel;
        height = chipHeight;
        padding = chipPadding;
        iconSize = chipIconSize;
        label.enableWordWrapping = false;
        label.alignment = TextAlignmentOptions.Left;
        SetText(label.text);
    }

    public void SetText(string text)
    {
        label.text = text;
        float iconWidth = icon != null ? iconSize + 10f : 0f;
        float width = padding * 2f + iconWidth + label.GetPreferredValues(text).x;
        rect.sizeDelta = new Vector2(width, height);

        var labelRect = (RectTransform)label.transform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(padding + iconWidth, 0f);
        labelRect.offsetMax = new Vector2(-padding, 0f);

        if (icon != null)
        {
            var iconRect = (RectTransform)icon.transform;
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(iconSize, iconSize);
            iconRect.anchoredPosition = new Vector2(padding, 0f);
        }
    }
}
