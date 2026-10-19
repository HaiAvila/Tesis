using System;
using System.Collections.Generic;
using UnityEngine;
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
    const float SessionIconWidth = 48f;

    // World Space so it's visible in the headset (Screen Space Overlay isn't drawn in VR) and
    // reachable by the controller ray. 1200x900 canvas units at this scale is about
    // 1.9m x 1.4m, 2m in front of the head - large enough that the existing font sizes stay
    // readable at that distance. Placement is a starting point, meant to be tuned.
    const float CanvasWidth = 1200f;
    const float CanvasHeight = 900f;
    const float CanvasWorldScale = 0.0016f;
    const float CanvasDistance = 2f;

    static readonly Color ShotButtonColor = new Color(0.2f, 0.2f, 0.2f, 1f);
    // Set apart from the single shots below it. Placeholder, not tuned.
    static readonly Color FullSceneButtonColor = new Color(0.14f, 0.28f, 0.34f, 1f);

    static readonly Color PreferidaOnColor = new Color(0.85f, 0.7f, 0.15f, 1f);
    static readonly Color PreferidaOffColor = new Color(0.75f, 0.75f, 0.78f, 1f);
    static readonly Color FavoritaOnColor = new Color(1f, 0.2f, 0.25f, 1f);
    static readonly Color FavoritaOffColor = new Color(0.75f, 0.75f, 0.78f, 1f);

    Font uiFont;
    ShotListDatabase database;
    RectTransform canvasRect;
    RectTransform contentRect;
    SortedDictionary<int, List<Shot>> shotsByScene;

    void Start()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("SceneSelectController: no ShotListDatabase instance found in the scene (expected the persisted DB object from Bootstrap).");
            return;
        }

        shotsByScene = database.GetShotsGroupedByScene();

        contentRect = BuildScrollingCanvas();
        BuildShotList(contentRect, shotsByScene);

        StartCoroutine(PlaceAgainOnceTracked());
    }

    // The head pose may not be tracked yet on the frame this scene starts (it was just loaded
    // together with MenuBackdrop), which would place the panel at the rig origin - so it's
    // placed again a couple of frames later, once the camera has a real pose.
    System.Collections.IEnumerator PlaceAgainOnceTracked()
    {
        yield return null;
        yield return null;
        PlaceInFrontOfHead();
    }

    // Yaw-only, same as the pause menu, so the panel stays upright regardless of where the
    // director happens to be looking up or down.
    void PlaceInFrontOfHead()
    {
        Camera headCamera = Camera.main;
        if (headCamera == null || canvasRect == null)
            return;

        Transform head = headCamera.transform;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        canvasRect.SetPositionAndRotation(head.position + forward * CanvasDistance, Quaternion.LookRotation(forward, Vector3.up));
    }

    RectTransform BuildScrollingCanvas()
    {
        var canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
        canvasRect.localScale = Vector3.one * CanvasWorldScale;
        PlaceInFrontOfHead();

        XRUISetup.EnableRayInteraction(canvasObject);
        XRUISetup.EnsureEventSystem();

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
        // Ensayar Solo: single shots only, and only those with an approved reference to
        // practice against (see ApprovedReferences - every shot, until the server answers).
        bool solo = Bootstrap.Mode == Bootstrap.AppMode.Solo;

        // "Escena completa" - the whole scene as one continuous take - heads each scene group.
        // Director only, Ensayo Remoto. Never Solo.
#if DIRECTOR_BUILD
        bool showFullScene = Bootstrap.Mode == Bootstrap.AppMode.Remote;
#else
        bool showFullScene = false;
#endif

        foreach (var scene in shotsByScene)
        {
            string sceneName = SceneNameByNumber.TryGetValue(scene.Key, out string name) ? name : $"Scene {scene.Key}";

            List<Shot> shots = scene.Value;
            if (solo)
                shots = shots.FindAll(shot => ApprovedReferences.HasApprovedReference(scene.Key, shot.ShotNumber));
            if (shots.Count == 0)
                continue;

            CreateHeader(content, sceneName);

            if (showFullScene)
                CreateFullSceneButton(content, scene.Key, sceneName, shots);

            foreach (Shot shot in shots)
                CreateShotButton(content, shot, sceneName);
        }
    }

    void CreateFullSceneButton(RectTransform parent, int sceneNumber, string sceneName, List<Shot> shots)
    {
        string label = $"Escena completa — shots {shots[0].ShotNumber} a {shots[shots.Count - 1].ShotNumber}, en una sola toma";
        CreateListButton(parent, $"FullScene_{sceneNumber}", label, FullSceneButtonColor, () => OnFullSceneSelected(sceneNumber, sceneName));
    }

    void OnFullSceneSelected(int sceneNumber, string sceneName)
    {
        Debug.Log($"[Escena] Escena completa selected: scene {sceneNumber} ({sceneName}). Loading {sceneName}...");
        ProceedToTake(sceneNumber, RehearsalSessionState.FullSceneShot, sceneName, null);
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
        Shot capturedShot = shot;
        string capturedSceneName = sceneName;
        CreateListButton(parent, $"Shot_{shot.ShotNumber}", shot.Description, ShotButtonColor, () => OnShotSelected(capturedShot, capturedSceneName));
    }

    void CreateListButton(RectTransform parent, string objectName, string text, Color color, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = color;
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
        label.text = text;
        label.fontSize = ButtonFontSize;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    void OnShotSelected(Shot shot, string sceneName)
    {
        Debug.Log($"Shot selected: scene {shot.SceneNumber} ({sceneName}), shot {shot.ShotNumber} - {shot.Description}. Loading {sceneName}...");

        // Only Inspección de Escenas (or solo actor practice) needs a session picker - a
        // live rehearsal never has past sessions to choose from. A shot with 0 or 1
        // sessions keeps loading directly, exactly as before.
        if (Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        {
            List<RecordingSession> sessions = database.GetSessionsForShot(shot.ShotNumber);
            if (sessions.Count > 1)
            {
                ShowSessionList(shot, sceneName);
                return;
            }
        }

        ProceedToSession(shot, sceneName, null);
    }

    // sessionId null means "no specific pick" - RecordingPlaybackController falls back to
    // the shot's most recent session, same as the original single-session behavior.
    void ProceedToSession(Shot shot, string sceneName, int? sessionId)
    {
        ProceedToTake(shot.SceneNumber, shot.ShotNumber, sceneName, sessionId);
    }

    // shotNumber is a shot, or RehearsalSessionState.FullSceneShot for the whole scene.
    void ProceedToTake(int sceneNumber, int shotNumber, string sceneName, int? sessionId)
    {
        // Inspecting a past recording is a solo review, not a live rehearsal - don't
        // sync/teleport the actor for it.
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote && RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SelectTake(sceneNumber, shotNumber);

        Bootstrap.SelectTake(sceneNumber, shotNumber);
        Bootstrap.CurrentSessionId = sessionId;
        SceneManager.LoadSceneAsync(sceneName);
    }

    void ShowSessionList(Shot shot, string sceneName)
    {
        List<RecordingSession> sessions = database.GetSessionsForShot(shot.ShotNumber);

        ClearContent();
        CreateBackButton(contentRect, () =>
        {
            ClearContent();
            BuildShotList(contentRect, shotsByScene);
        });
        CreateHeader(contentRect, $"Shot {shot.ShotNumber}: {shot.Description}");

        foreach (RecordingSession session in sessions)
            CreateSessionRow(contentRect, shot, sceneName, session);
    }

    void ClearContent()
    {
        for (int i = contentRect.childCount - 1; i >= 0; i--)
            Destroy(contentRect.GetChild(i).gameObject);
    }

    void CreateBackButton(RectTransform parent, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject("BackButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
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
        label.text = "← Volver";
        label.fontSize = ButtonFontSize;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    // A row per saved session: a wide info button (date/time, duration - clicking it
    // proceeds to load exactly this session) plus two small, independently clickable icon
    // toggles - preferida (star, exclusive per shot) and favorita (heart, a non-exclusive
    // bookmark). Neither icon click bubbles into the info button's click since each is its
    // own separate Button/Graphic under Unity's UI raycasting.
    void CreateSessionRow(RectTransform parent, Shot shot, string sceneName, RecordingSession session)
    {
        var rowObject = new GameObject($"Session_{session.Id}", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        rowObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = Spacing;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = true;

        Shot capturedShot = shot;
        RecordingSession capturedSession = session;

        var infoButtonObject = new GameObject("Info", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        infoButtonObject.transform.SetParent(rowObject.transform, false);
        infoButtonObject.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 1f);
        LayoutElement infoLayoutElement = infoButtonObject.GetComponent<LayoutElement>();
        infoLayoutElement.flexibleWidth = 1f;
        infoLayoutElement.preferredHeight = ButtonHeight;

        var infoTextObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        infoTextObject.transform.SetParent(infoButtonObject.transform, false);
        var infoTextRect = (RectTransform)infoTextObject.transform;
        infoTextRect.anchorMin = Vector2.zero;
        infoTextRect.anchorMax = Vector2.one;
        infoTextRect.offsetMin = new Vector2(16, 4);
        infoTextRect.offsetMax = new Vector2(-16, -4);

        Text infoText = infoTextObject.GetComponent<Text>();
        infoText.font = uiFont;
        infoText.text = FormatSessionLabel(session);
        infoText.fontSize = ButtonFontSize;
        infoText.color = Color.white;
        infoText.alignment = TextAnchor.MiddleLeft;

        infoButtonObject.GetComponent<Button>().onClick.AddListener(() => ProceedToSession(capturedShot, sceneName, capturedSession.Id));

        // Star and heart are sprites (UIIcons), not glyphs - see UIIcons for why.
        CreateSessionIconButton(rowObject.transform, "Preferida",
            UIIcons.Star(session.EsPreferida),
            session.EsPreferida ? PreferidaOnColor : PreferidaOffColor,
            out Button preferidaButton);
        preferidaButton.onClick.AddListener(() => OnPreferidaToggled(capturedShot, sceneName, capturedSession));

        Image favoritaIcon = CreateSessionIconButton(rowObject.transform, "Favorita",
            UIIcons.Heart(session.EsFavorita),
            session.EsFavorita ? FavoritaOnColor : FavoritaOffColor,
            out Button favoritaButton);
        favoritaButton.onClick.AddListener(() => OnFavoritaToggled(capturedSession, favoritaIcon));
    }

    Image CreateSessionIconButton(Transform parent, string name, Sprite sprite, Color color, out Button button)
    {
        var buttonObject = new GameObject($"Icon_{name}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 1f);
        LayoutElement layoutElement = buttonObject.GetComponent<LayoutElement>();
        layoutElement.preferredWidth = SessionIconWidth;
        layoutElement.preferredHeight = ButtonHeight;

        button = buttonObject.GetComponent<Button>();
        return UIIcons.CreateIcon(buttonObject.transform, sprite, color);
    }

    // Exclusive - marking one preferida unmarks every other one for this shot, so the whole
    // list is rebuilt to keep every row's star in sync with the DB.
    void OnPreferidaToggled(Shot shot, string sceneName, RecordingSession session)
    {
        database.SetPreferida(session.Id, shot.ShotNumber);
        ShowSessionList(shot, sceneName);
    }

    // Non-exclusive - only this row's own heart changes.
    void OnFavoritaToggled(RecordingSession session, Image favoritaIcon)
    {
        bool newValue = !session.EsFavorita;
        database.SetFavorita(session.Id, newValue);
        session.EsFavorita = newValue;

        favoritaIcon.sprite = UIIcons.Heart(newValue);
        favoritaIcon.color = newValue ? FavoritaOnColor : FavoritaOffColor;
    }

    static string FormatSessionLabel(RecordingSession session)
    {
        string when = DateTime.TryParse(session.FechaHora, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : session.FechaHora;

        return $"{when} — {session.Duracion:F1}s";
    }
}
