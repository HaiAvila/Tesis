using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class DirectorHomeMenuController : MonoBehaviour
{
    const int ButtonFontSize = 28;
    const float ButtonWidth = 360f;
    const float ButtonHeight = 72f;
    const float Spacing = 20f;

    void Start()
    {
        Font uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        if (FindObjectOfType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var layoutObject = new GameObject("ButtonLayout", typeof(RectTransform), typeof(VerticalLayoutGroup));
        layoutObject.transform.SetParent(canvasObject.transform, false);
        var layoutRect = (RectTransform)layoutObject.transform;
        layoutRect.anchorMin = new Vector2(0.5f, 0.5f);
        layoutRect.anchorMax = new Vector2(0.5f, 0.5f);
        layoutRect.pivot = new Vector2(0.5f, 0.5f);
        layoutRect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight * 2 + Spacing);

        VerticalLayoutGroup layout = layoutObject.GetComponent<VerticalLayoutGroup>();
        layout.spacing = Spacing;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        CreateButton(layoutRect, uiFont, "Ensayo Remoto", OnRemoteRehearsalSelected);
        CreateButton(layoutRect, uiFont, "Inspección de Escenas", OnSceneInspectionSelected);
    }

    void CreateButton(RectTransform parent, Font font, string label, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 1f);
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = font;
        text.text = label;
        text.fontSize = ButtonFontSize;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    void OnRemoteRehearsalSelected()
    {
        Debug.Log("Director Home Menu: 'Ensayo Remoto' pressed - not implemented yet.");
    }

    void OnSceneInspectionSelected()
    {
        Debug.Log("Director Home Menu: 'Inspección de Escenas' pressed - not implemented yet.");
    }
}
