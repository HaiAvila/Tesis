using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// A wheel of options, like a game's quick menu: one glass segment per option with its icon, and
// in the middle the name and a line about what's being pointed at. Pointing works with the
// controller ray (the trigger chooses) or by pushing the left joystick towards a segment (its
// click chooses). The middle closes it. Up to about 8 options read well.
public class RadialMenu : MonoBehaviour
{
    public class Item
    {
        public string Icon;
        public string Label;
        public string Description;
        public Action OnSelected;
    }

    const float CanvasSize = 1000f;
    const float WorldScale = 0.0012f;          // 1.2 m across at 1.5 m
    const int TextureSize = 512;
    const float OuterRadius = 0.48f;           // fractions of the texture
    const float InnerRadius = 0.21f;
    const float GapDegrees = 1.6f;
    const float CenterRadius = 190f;           // canvas units
    const float IconSize = 76f;
    const float StickThreshold = 0.6f;

    static readonly Dictionary<int, (Sprite glass, Sprite fill)> segmentSprites = new Dictionary<int, (Sprite, Sprite)>();

    readonly List<Item> items = new List<Item>();
    readonly List<Image> highlights = new List<Image>();
    readonly List<TextMeshProUGUI> icons = new List<TextMeshProUGUI>();
    TextMeshProUGUI centerTitle, centerDescription;
    string idleTitle, idleDescription;
    Action onClose;
    int hovered = -1;
    InputAction stick, stickClick;

    public static RadialMenu Create(Transform head, float distance, List<Item> options, string title, string description, Action close)
    {
        Vector3 forward = head.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        var root = new GameObject("RadialMenu", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(RadialMenu));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        var rect = (RectTransform)root.transform;
        rect.sizeDelta = new Vector2(CanvasSize, CanvasSize);
        rect.SetPositionAndRotation(head.position + forward * distance, Quaternion.LookRotation(forward, Vector3.up));
        rect.localScale = Vector3.one * WorldScale;

        RadialMenu menu = root.GetComponent<RadialMenu>();
        menu.Build(options, title, description, close);
        return menu;
    }

    void Build(List<Item> options, string title, string description, Action close)
    {
        items.AddRange(options);
        idleTitle = title;
        idleDescription = description;
        onClose = close;
        var rect = (RectTransform)transform;
        int count = Mathf.Max(1, items.Count);
        (Sprite glass, Sprite fill) sprites = SegmentSprites(count);
        float step = 360f / count;
        float iconRadius = (OuterRadius + InnerRadius) / 2f * CanvasSize;

        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var segment = new GameObject($"Segment_{items[i].Label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(RadialSegment));
            segment.transform.SetParent(rect, false);
            var segmentRect = (RectTransform)segment.transform;
            GlassUI.Stretch(segmentRect);
            segmentRect.localRotation = Quaternion.Euler(0f, 0f, -i * step);

            Image image = segment.GetComponent<Image>();
            image.sprite = sprites.glass;
            image.alphaHitTestMinimumThreshold = 0.1f; // only the segment itself catches the ray
            Button button = segment.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => Choose(index));
            segment.GetComponent<RadialSegment>().Init(this, index);

            var highlightObject = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
            highlightObject.transform.SetParent(segmentRect, false);
            GlassUI.Stretch((RectTransform)highlightObject.transform);
            Image highlight = highlightObject.GetComponent<Image>();
            highlight.sprite = sprites.fill;
            highlight.color = Color.clear;
            highlight.raycastTarget = false;
            highlights.Add(highlight);

            // Icons stay upright: on the wheel, not on the rotated segment.
            float angle = i * step * Mathf.Deg2Rad;
            TextMeshProUGUI icon = GlassUI.IconLabel(rect, items[i].Icon, IconSize);
            var iconRect = (RectTransform)icon.transform;
            iconRect.sizeDelta = new Vector2(IconSize * 1.4f, IconSize * 1.4f);
            iconRect.anchoredPosition = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * iconRadius;
            icons.Add(icon);
        }

        // The middle: what's pointed at, and a press closes the menu.
        var center = new GameObject("Center", typeof(RectTransform), typeof(Image), typeof(Button));
        center.transform.SetParent(rect, false);
        var centerRect = (RectTransform)center.transform;
        centerRect.sizeDelta = new Vector2(CenterRadius * 2f, CenterRadius * 2f);
        Image centerImage = center.GetComponent<Image>();
        centerImage.sprite = GlassUI.GlassCircleSprite;
        centerImage.alphaHitTestMinimumThreshold = 0.1f;
        Button centerButton = center.GetComponent<Button>();
        centerButton.transition = Selectable.Transition.None;
        centerButton.onClick.AddListener(() => onClose?.Invoke());

        centerTitle = GlassUI.Label(centerRect, idleTitle, 40f, true);
        var titleRect = (RectTransform)centerTitle.transform;
        titleRect.sizeDelta = new Vector2(CenterRadius * 1.6f, 56f);
        titleRect.anchoredPosition = new Vector2(0f, 58f);

        centerDescription = GlassUI.Label(centerRect, idleDescription, 24f, false, GlassUI.SubTextColor);
        var descriptionRect = (RectTransform)centerDescription.transform;
        descriptionRect.sizeDelta = new Vector2(CenterRadius * 1.5f, 130f);
        descriptionRect.anchoredPosition = new Vector2(0f, -40f);
        centerDescription.alignment = TextAlignmentOptions.Top;

        XRUISetup.MoveToUILayer(gameObject);
        XRUISetup.EnableRayInteraction(gameObject);
        XRUISetup.EnsureEventSystem();

        stick = new InputAction("RadialStick", InputActionType.Value, expectedControlType: "Vector2");
        stick.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        stick.AddBinding("<XRSimulatedController>{LeftHand}/primary2DAxis");
        stick.Enable();
        stickClick = new InputAction("RadialStickClick", InputActionType.Button);
        stickClick.AddBinding("<XRController>{LeftHand}/{primary2DAxisClick}");
        stickClick.AddBinding("<XRSimulatedController>{LeftHand}/primary2DAxisClick");
        stickClick.performed += _ => { if (hovered >= 0) Choose(hovered); };
        stickClick.Enable();
    }

    void OnDestroy()
    {
        stick?.Dispose();
        stickClick?.Dispose();
    }

    void Update()
    {
        Vector2 direction = stick != null ? stick.ReadValue<Vector2>() : Vector2.zero;
        if (direction.magnitude < StickThreshold || items.Count == 0)
            return;

        float angle = Mathf.Repeat(Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg, 360f);
        float step = 360f / items.Count;
        Hover(Mathf.FloorToInt(Mathf.Repeat(angle + step / 2f, 360f) / step) % items.Count);
    }

    public void Hover(int index)
    {
        hovered = index;
        for (int i = 0; i < highlights.Count; i++)
        {
            highlights[i].color = i == index ? new Color(GlassUI.Accent.r, GlassUI.Accent.g, GlassUI.Accent.b, 0.55f) : Color.clear;
            icons[i].transform.localScale = Vector3.one * (i == index ? 1.15f : 1f);
        }

        bool any = index >= 0 && index < items.Count;
        centerTitle.text = any ? items[index].Label : idleTitle;
        centerDescription.text = any ? items[index].Description : idleDescription;
    }

    public void Unhover(int index)
    {
        if (hovered == index)
            Hover(-1);
    }

    void Choose(int index)
    {
        if (index >= 0 && index < items.Count)
            items[index].OnSelected?.Invoke();
    }

    // One segment per option, pointing up (rotated into place): glass, and a plain white copy of
    // the same shape for the highlight. Readable textures, so the ray only hits the shape.
    static (Sprite glass, Sprite fill) SegmentSprites(int count)
    {
        if (segmentSprites.TryGetValue(count, out (Sprite glass, Sprite fill) cached))
            return cached;

        float half = (360f / count - GapDegrees) / 2f;
        var glass = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var fill = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var glassPixels = new Color[TextureSize * TextureSize];
        var fillPixels = new Color[TextureSize * TextureSize];
        float outer = OuterRadius * TextureSize, inner = InnerRadius * TextureSize;
        float centerPixel = TextureSize / 2f;

        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                var p = new Vector2(x + 0.5f - centerPixel, y + 0.5f - centerPixel);
                float r = p.magnitude;
                float angle = Mathf.Abs(Mathf.Atan2(p.x, p.y) * Mathf.Rad2Deg);
                // Distance to the segment's edges, in pixels (positive inside).
                float toRing = Mathf.Min(outer - r, r - inner);
                float toSide = (half - angle) * Mathf.Deg2Rad * r;
                float inside = Mathf.Min(toRing, toSide);
                float coverage = Mathf.Clamp01(inside + 0.5f);
                if (coverage <= 0f)
                {
                    glassPixels[y * TextureSize + x] = Color.clear;
                    fillPixels[y * TextureSize + x] = Color.clear;
                    continue;
                }

                fillPixels[y * TextureSize + x] = new Color(1f, 1f, 1f, coverage);

                float outward = Mathf.InverseLerp(inner, outer, r);
                Color tint = Color.Lerp(new Color(0.07f, 0.08f, 0.11f, 0.7f), new Color(0.16f, 0.18f, 0.23f, 0.64f), outward);
                float rim = Mathf.Clamp01(1f - Mathf.Abs(inside - 1.2f) / 1.6f) * 0.32f;
                Color color = Color.Lerp(tint, new Color(1f, 1f, 1f, 0.9f), rim);
                color.a = Mathf.Max(color.a, rim) * coverage;
                glassPixels[y * TextureSize + x] = color;
            }
        }

        glass.SetPixels(glassPixels);
        glass.Apply();
        fill.SetPixels(fillPixels);
        fill.Apply();
        DontDestroyOnLoad(glass);
        DontDestroyOnLoad(fill);
        var rect = new Rect(0, 0, TextureSize, TextureSize);
        var sprites = (Sprite.Create(glass, rect, new Vector2(0.5f, 0.5f), 100f), Sprite.Create(fill, rect, new Vector2(0.5f, 0.5f), 100f));
        segmentSprites[count] = sprites;
        return sprites;
    }
}

// Tells the wheel which segment the ray is on.
public class RadialSegment : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    RadialMenu menu;
    int index;

    public void Init(RadialMenu owner, int segmentIndex)
    {
        menu = owner;
        index = segmentIndex;
    }

    public void OnPointerEnter(PointerEventData eventData) => menu.Hover(index);
    public void OnPointerExit(PointerEventData eventData) => menu.Unhover(index);
}
