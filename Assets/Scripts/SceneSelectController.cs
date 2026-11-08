using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The shot list, grouped by scene. In Inspección and Ensayo Remoto each row also says what's
// recorded for it (takes, the latest date, a preferida, notes), and in Inspección a shot without
// takes is dimmed. Choosing a shot in Inspección always shows its takes first - with what each
// one holds, and a way to delete it - plus "Ver el set sin toma"; a shot without any goes
// straight into the set.
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
    static readonly Color EmptyShotButtonColor = new Color(0.13f, 0.13f, 0.14f, 1f);
    static readonly Color EmptyTextColor = new Color(0.55f, 0.55f, 0.58f, 1f);
    static readonly Color InfoTextColor = new Color(0.8f, 0.82f, 0.86f, 1f);
    static readonly Color DeleteIconColor = new Color(0.9f, 0.45f, 0.45f, 1f);
    static readonly string[] Months = { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" };
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
    Dictionary<(int scene, int shot), TakeSummary> summaries = new Dictionary<(int, int), TakeSummary>();

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
        GlassUI.Glass(scrollObject.GetComponent<Image>(), 40f);
        GlassUI.AddShadow(canvasRect, 40f);

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
        // Director only: Ensayo Remoto (rehearse/record it) and Inspección (its recordings).
        // Never Solo.
#if DIRECTOR_BUILD
        bool showFullScene = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Inspect;
#else
        bool showFullScene = false;
#endif
        bool showInfo = showFullScene; // what's recorded lives on the director's device
        bool inspecting = Bootstrap.Mode == Bootstrap.AppMode.Inspect;
        summaries = showInfo ? database.GetTakeSummaries() : new Dictionary<(int, int), TakeSummary>();

        foreach (var scene in shotsByScene)
        {
            string sceneName = SceneNameByNumber.TryGetValue(scene.Key, out string name) ? name : $"Scene {scene.Key}";

            List<Shot> shots = scene.Value;
            if (solo)
                shots = shots.FindAll(shot => ApprovedReferences.HasApprovedReference(scene.Key, shot.ShotNumber));
            if (shots.Count == 0)
                continue;

            int sceneTakes = 0;
            foreach (var entry in summaries)
            {
                if (entry.Key.scene == scene.Key)
                    sceneTakes += entry.Value.Takes;
            }
            CreateHeader(content, $"Escena {scene.Key} · {sceneName}", showInfo ? Plural(sceneTakes, "toma", "tomas", "Sin tomas") : null);

            if (showFullScene)
                CreateFullSceneButton(content, scene.Key, sceneName, shots, inspecting);

            foreach (Shot shot in shots)
                CreateShotButton(content, shot, sceneName, showInfo, inspecting);
        }
    }

    void CreateFullSceneButton(RectTransform parent, int sceneNumber, string sceneName, List<Shot> shots, bool inspecting)
    {
        string label = $"Escena completa — shots {shots[0].ShotNumber} a {shots[shots.Count - 1].ShotNumber}, en una sola toma";
        TakeSummary summary = Summary(sceneNumber, RehearsalSessionState.FullSceneShot);
        CreateListButton(parent, $"FullScene_{sceneNumber}", label, FullSceneButtonColor, () => OnFullSceneSelected(sceneNumber, sceneName),
            InfoText(summary, "grabación", "grabaciones", "Sin grabaciones", false), inspecting && summary.Takes == 0);
    }

    TakeSummary Summary(int scene, int shot) => summaries.TryGetValue((scene, shot), out TakeSummary summary) ? summary : new TakeSummary();

    // "3 tomas ★ · 2 notas · 5 nov", "Sin tomas · 1 nota".
    static string InfoText(TakeSummary summary, string one, string many, string none, bool star)
    {
        string text = Plural(summary.Takes, one, many, none);
        if (star && summary.HasPreferida)
            text += " " + GlassUI.Icon.Star;
        if (summary.Notes > 0)
            text += " · " + Plural(summary.Notes, "nota", "notas", "");
        if (summary.Takes > 0 && DateTime.TryParse(summary.Latest, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime latest))
            text += $" · {latest.Day} {Months[latest.Month - 1]}";
        return text;
    }

    static string Plural(int count, string one, string many, string none) =>
        count == 0 ? none : count == 1 ? $"1 {one}" : $"{count} {many}";

    void OnFullSceneSelected(int sceneNumber, string sceneName)
    {
        Debug.Log($"[Escena] Escena completa selected: scene {sceneNumber} ({sceneName}). Loading {sceneName}...");

        // Inspección: same rule as a shot - the list when there's any recording, else the set.
        if (Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        {
            if (database.GetSceneSessions(sceneNumber).Count > 0)
                ShowSceneSessionList(sceneNumber, sceneName);
            else
                EnterWithoutTake(sceneNumber, RehearsalSessionState.FullSceneShot, sceneName);
            return;
        }

        ProceedToTake(sceneNumber, RehearsalSessionState.FullSceneShot, sceneName, null);
    }

    // rightText: a second, right-aligned text, e.g. a scene's take count.
    void CreateHeader(RectTransform parent, string text, string rightText = null)
    {
        var headerObject = new GameObject($"Header_{text}", typeof(RectTransform), typeof(LayoutElement));
        headerObject.transform.SetParent(parent, false);
        // A shot's description can take two lines.
        headerObject.GetComponent<LayoutElement>().preferredHeight = text.Length > 50 ? HeaderFontSize * 2 + 32f : HeaderFontSize + 24f;

        TMP_Text label = CreateText((RectTransform)headerObject.transform, text, HeaderFontSize, Color.white, TextAlignmentOptions.Left, 0f, rightText != null ? 0.7f : 1f);
        label.fontStyle = FontStyles.Bold;
        if (rightText != null)
            CreateText((RectTransform)headerObject.transform, rightText, ButtonFontSize, InfoTextColor, TextAlignmentOptions.Right, 0.7f, 1f);
    }

    void CreateShotButton(RectTransform parent, Shot shot, string sceneName, bool showInfo, bool inspecting)
    {
        Shot capturedShot = shot;
        string capturedSceneName = sceneName;
        TakeSummary summary = Summary(shot.SceneNumber, shot.ShotNumber);
        CreateListButton(parent, $"Shot_{shot.ShotNumber}", $"Shot {shot.ShotNumber} · {shot.Description}", ShotButtonColor,
            () => OnShotSelected(capturedShot, capturedSceneName),
            showInfo ? InfoText(summary, "toma", "tomas", "Sin tomas", true) : null,
            inspecting && summary.Takes == 0);
    }

    // rightText: what's recorded for it, right-aligned. dim: nothing recorded (Inspección).
    void CreateListButton(RectTransform parent, string objectName, string text, Color color, UnityEngine.Events.UnityAction onClick, string rightText = null, bool dim = false)
    {
        var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        GlassUI.SkinButton(buttonObject, 18f, dim ? GlassUI.ButtonStyle.RowEmpty : color == FullSceneButtonColor ? GlassUI.ButtonStyle.RowAccent : GlassUI.ButtonStyle.Row);
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var rect = (RectTransform)buttonObject.transform;
        CreateText(rect, text, ButtonFontSize, dim ? EmptyTextColor : Color.white, TextAlignmentOptions.Left, 0f, rightText != null ? 0.68f : 1f);
        if (rightText != null)
            CreateText(rect, rightText, ButtonFontSize, dim ? EmptyTextColor : InfoTextColor, TextAlignmentOptions.Right, 0.68f, 1f);

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    // A text across part of `parent`'s width (from and to: fractions of it), with a margin.
    TMP_Text CreateText(RectTransform parent, string text, int size, Color color, TextAlignmentOptions alignment, float from, float to)
    {
        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = new Vector2(from, 0f);
        textRect.anchorMax = new Vector2(to, 1f);
        textRect.offsetMin = new Vector2(16, 4);
        textRect.offsetMax = new Vector2(-16, -4);

        TMP_Text label = textObject.GetComponent<TextMeshProUGUI>();
        label.font = GlassUI.Regular;
        label.text = text;
        label.fontSize = size;
        label.color = color;
        label.alignment = alignment;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    void OnShotSelected(Shot shot, string sceneName)
    {
        Debug.Log($"Shot selected: scene {shot.SceneNumber} ({sceneName}), shot {shot.ShotNumber} - {shot.Description}. Loading {sceneName}...");

        // Only Inspección de Escenas needs a take picker - a live rehearsal never has past takes
        // to choose from. Always shown when there's any, so it's clear what opens; without any,
        // straight into the set.
        if (Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        {
            if (database.GetSessionsForShot(shot.ShotNumber).Count > 0)
                ShowSessionList(shot, sceneName);
            else
                EnterWithoutTake(shot.SceneNumber, shot.ShotNumber, sceneName);
            return;
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

    // Inspección of a shot (or whole scene) without opening a take: walk the set, leave notes.
    void EnterWithoutTake(int sceneNumber, int shotNumber, string sceneName)
    {
        Bootstrap.InspectWithoutTake = true;
        ProceedToTake(sceneNumber, shotNumber, sceneName, null);
    }

    void BackToShotList()
    {
        ClearContent();
        BuildShotList(contentRect, shotsByScene);
    }

    void ShowSessionList(Shot shot, string sceneName)
    {
        List<RecordingSession> sessions = database.GetSessionsForShot(shot.ShotNumber);
        if (sessions.Count == 0)
        {
            BackToShotList();
            return;
        }

        ClearContent();
        CreateBackButton(contentRect, BackToShotList);
        CreateHeader(contentRect, $"Shot {shot.ShotNumber}: {shot.Description}", Plural(sessions.Count, "toma", "tomas", "Sin tomas"));

        foreach (RecordingSession session in sessions)
        {
            RecordingSession capturedSession = session;
            CreateSessionRow(contentRect, session,
                () => ProceedToSession(shot, sceneName, capturedSession.Id),
                () => OnPreferidaToggled(shot, sceneName, capturedSession),
                () => ShowSessionList(shot, sceneName));
        }

        CreateListButton(contentRect, "WithoutTake", "Ver el set sin toma", FullSceneButtonColor,
            () => EnterWithoutTake(shot.SceneNumber, shot.ShotNumber, sceneName));
    }

    // Inspección: the scene's full-scene recordings, apart from its single-shot takes. Heart
    // only, no star.
    void ShowSceneSessionList(int sceneNumber, string sceneName)
    {
        List<RecordingSession> sessions = database.GetSceneSessions(sceneNumber);
        if (sessions.Count == 0)
        {
            BackToShotList();
            return;
        }

        ClearContent();
        CreateBackButton(contentRect, BackToShotList);
        CreateHeader(contentRect, $"Escena {sceneNumber} completa", Plural(sessions.Count, "grabación", "grabaciones", "Sin grabaciones"));

        foreach (RecordingSession session in sessions)
        {
            RecordingSession capturedSession = session;
            CreateSessionRow(contentRect, session,
                () => ProceedToTake(sceneNumber, RehearsalSessionState.FullSceneShot, sceneName, capturedSession.Id),
                null,
                () => ShowSceneSessionList(sceneNumber, sceneName));
        }

        CreateListButton(contentRect, "WithoutTake", "Ver el set sin toma", FullSceneButtonColor,
            () => EnterWithoutTake(sceneNumber, RehearsalSessionState.FullSceneShot, sceneName));
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
        GlassUI.SkinButton(buttonObject, 18f, GlassUI.ButtonStyle.Row);
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(buttonObject.transform, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16, 4);
        textRect.offsetMax = new Vector2(-16, -4);

        TMP_Text label = textObject.GetComponent<TextMeshProUGUI>();
        label.font = GlassUI.Regular;
        label.text = "Volver";
        label.fontSize = ButtonFontSize;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Left;

        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
    }

    // A row per saved session: a wide info button (date/time, duration - clicking it
    // proceeds to load exactly this session) plus two small, independently clickable icon
    // toggles - preferida (star, exclusive per shot) and favorita (heart, a non-exclusive
    // bookmark). Neither icon click bubbles into the info button's click since each is its
    // own separate Button/Graphic under Unity's UI raycasting.
    //
    // onPreferida null = no star: a full-scene recording can't be preferida yet (approving it
    // needs slicing it into per-shot references first).
    // onDeleted: after a take is deleted from here (the list is built again).
    void CreateSessionRow(RectTransform parent, RecordingSession session, UnityEngine.Events.UnityAction onOpen, UnityEngine.Events.UnityAction onPreferida, Action onDeleted)
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

        RecordingSession capturedSession = session;

        var infoButtonObject = new GameObject("Info", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        infoButtonObject.transform.SetParent(rowObject.transform, false);
        GlassUI.SkinButton(infoButtonObject, 18f, GlassUI.ButtonStyle.Row);
        LayoutElement infoLayoutElement = infoButtonObject.GetComponent<LayoutElement>();
        infoLayoutElement.flexibleWidth = 1f;
        infoLayoutElement.preferredHeight = ButtonHeight;

        var infoTextObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        infoTextObject.transform.SetParent(infoButtonObject.transform, false);
        var infoTextRect = (RectTransform)infoTextObject.transform;
        infoTextRect.anchorMin = Vector2.zero;
        infoTextRect.anchorMax = Vector2.one;
        infoTextRect.offsetMin = new Vector2(16, 4);
        infoTextRect.offsetMax = new Vector2(-16, -4);

        TMP_Text infoText = infoTextObject.GetComponent<TextMeshProUGUI>();
        infoText.font = GlassUI.Regular;
        infoText.text = FormatSessionLabel(session);
        if (session.IsScene)
        {
            int marked = database.GetBoundariesForSession(session.SessionGuid).Count;
            infoText.text += marked == 1 ? " — 1 shot marcado" : $" — {marked} shots marcados";
        }
        infoText.fontSize = ButtonFontSize;
        infoText.color = Color.white;
        infoText.alignment = TextAlignmentOptions.Left;

        infoButtonObject.GetComponent<Button>().onClick.AddListener(onOpen);

        // Star and heart are sprites (UIIcons), not glyphs - see UIIcons for why.
        if (onPreferida != null)
        {
            CreateSessionIconButton(rowObject.transform, "Preferida",
                UIIcons.Star(session.EsPreferida),
                session.EsPreferida ? PreferidaOnColor : PreferidaOffColor,
                out Button preferidaButton);
            preferidaButton.onClick.AddListener(onPreferida);
        }

        Image favoritaIcon = CreateSessionIconButton(rowObject.transform, "Favorita",
            UIIcons.Heart(session.EsFavorita),
            session.EsFavorita ? FavoritaOnColor : FavoritaOffColor,
            out Button favoritaButton);
        favoritaButton.onClick.AddListener(() => OnFavoritaToggled(capturedSession, favoritaIcon));

#if DIRECTOR_BUILD
        CreateSessionIconButton(rowObject.transform, "Borrar", UIIcons.Trash(), DeleteIconColor, out Button deleteButton);
        deleteButton.onClick.AddListener(() =>
        {
            if (VoiceNotesPanel.Instance == null)
                return;
            VoiceNotesPanel.Instance.Confirm($"¿Borrar la toma del {FormatSessionDate(capturedSession)} y sus notas? No se puede deshacer.", () =>
            {
                database.DeleteSession(capturedSession);
                FloatingMessage.Show("Toma borrada");
                onDeleted?.Invoke();
            });
        });
#endif
    }

    Image CreateSessionIconButton(Transform parent, string name, Sprite sprite, Color color, out Button button)
    {
        var buttonObject = new GameObject($"Icon_{name}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        GlassUI.SkinButton(buttonObject, SessionIconWidth / 2f);
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

    static string FormatSessionDate(RecordingSession session)
    {
        return DateTime.TryParse(session.FechaHora, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : session.FechaHora;
    }

    // "05/11/2026 16:20 — 12.4 s · Con cuerpo · Con audio · 2 notas": what opening it will show.
    string FormatSessionLabel(RecordingSession session)
    {
        string label = $"{FormatSessionDate(session)} — {session.Duracion:F1} s · {(session.AvatarLayout != null ? "Con cuerpo" : "Sin cuerpo")}";
        if (System.IO.File.Exists(TakeAudioRecorder.TrackPath(session.SessionGuid, TakeAudioRecorder.DirectorTrack))
            || System.IO.File.Exists(TakeAudioRecorder.TrackPath(session.SessionGuid, TakeAudioRecorder.ActorTrack)))
            label += " · Con audio";
        int notes = database.GetVoiceNotesOfTake(session.SessionGuid).Count;
        if (notes > 0)
            label += " · " + Plural(notes, "nota", "notas", "");
        return label;
    }
}
