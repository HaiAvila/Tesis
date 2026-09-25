using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneSelectController : MonoBehaviour
{
    public static readonly Dictionary<int, string> SceneNameByNumber = new Dictionary<int, string>
    {
        { 1, "Scene1_mountain" },
        { 2, "Scene2_spaceship" },
        { 3, "Scene3_apartment" },
    };

    const int HeaderFontSize = 32;
    const int ButtonFontSize = 22;
    const float ButtonHeight = 64f;
    const float Spacing = 10f;

    Font uiFont;

    void Start()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("SceneSelectController: no ShotListDatabase instance found in the scene (expected the persisted DB object from Bootstrap).");
            return;
        }

        SortedDictionary<int, List<Shot>> shotsByScene = database.GetShotsGroupedByScene();

        RectTransform content = BuildScrollingCanvas();
        BuildShotList(content, shotsByScene);
    }

    RectTransform BuildScrollingCanvas()
    {
        var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

        var scrollObject = new GameObject("ShotScrollView", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scrollObject.transform.SetParent(canvasObject.transform, false);
        var scrollRect = (RectTransform)scrollObject.transform;
        scrollRect.anchorMin = Vector2.zero;
        scrollRect.anchorMax = Vector2.one;
        scrollRect.offsetMin = Vector2.zero;
        scrollRect.offsetMax = Vector2.zero;
        scrollObject.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 1f);

        var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewportObject.transform.SetParent(scrollObject.transform, false);
        var viewportRect = (RectTransform)viewportObject.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewportObject.GetComponent<Image>().color = Color.white;
        viewportObject.GetComponent<Mask>().showMaskGraphic = false;

        var contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentObject.transform.SetParent(viewportObject.transform, false);
        var contentRect = (RectTransform)contentObject.transform;
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;

        VerticalLayoutGroup layoutGroup = contentObject.GetComponent<VerticalLayoutGroup>();
        layoutGroup.childForceExpandWidth = true;
        layoutGroup.childForceExpandHeight = false;
        layoutGroup.childControlWidth = true;
        layoutGroup.childControlHeight = true;
        layoutGroup.spacing = Spacing;
        layoutGroup.padding = new RectOffset(20, 20, 20, 20);

        contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = scrollObject.GetComponent<ScrollRect>();
        scroll.viewport = viewportRect;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;

        return contentRect;
    }

    void BuildShotList(RectTransform content, SortedDictionary<int, List<Shot>> shotsByScene)
    {
        foreach (var scene in shotsByScene)
        {
            string sceneName = SceneNameByNumber.TryGetValue(scene.Key, out string name) ? name : $"Scene {scene.Key}";

            CreateHeader(content, sceneName);

            foreach (Shot shot in scene.Value)
                CreateShotButton(content, shot, sceneName);
        }
    }

    void CreateHeader(RectTransform parent, string text)
    {
        var headerObject = new GameObject($"Header_{text}", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        headerObject.transform.SetParent(parent, false);

        Text label = headerObject.GetComponent<Text>();
        label.font = uiFont;
        label.text = text;
        label.fontSize = HeaderFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;

        headerObject.GetComponent<LayoutElement>().preferredHeight = HeaderFontSize + 24f;
    }

    void CreateShotButton(RectTransform parent, Shot shot, string sceneName)
    {
        var buttonObject = new GameObject($"Shot_{shot.ShotNumber}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 1f);
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16, 4);
        textRect.offsetMax = new Vector2(-16, -4);

        Text label = textObject.GetComponent<Text>();
        label.font = uiFont;
        label.text = shot.Description;
        label.fontSize = ButtonFontSize;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        Shot capturedShot = shot;
        string capturedSceneName = sceneName;
        buttonObject.GetComponent<Button>().onClick.AddListener(() => OnShotSelected(capturedShot, capturedSceneName));
    }

    void OnShotSelected(Shot shot, string sceneName)
    {
        Debug.Log($"Shot selected: scene {shot.SceneNumber} ({sceneName}), shot {shot.ShotNumber} - {shot.Description}. Loading {sceneName}...");

        // TEMPORARY diagnostic - remove once the actor-sync-on-pause-menu issue is confirmed fixed.
        Debug.Log($"IsInspecting={Bootstrap.IsInspecting}, RehearsalSessionSync.Instance is null={RehearsalSessionSync.Instance == null}");

        // Inspecting a past recording is a solo review, not a live rehearsal - don't
        // sync/teleport the actor for it.
        if (!Bootstrap.IsInspecting && RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SelectShot(shot.ShotNumber);

        Bootstrap.CurrentShotNumber = shot.ShotNumber;
        SceneManager.LoadSceneAsync(sceneName);
    }
}
