using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). A simple world-space list of every
// session marked favorita, across every scene/shot - opened from the pause menu's
// "Favoritos" entry (director-only), reachable from any content scene, including mid
// Ensayo Remoto. Picking one jumps straight into Inspección de Escenas for that exact
// session - any running rehearsal is interrupted, which is the point of a direct shortcut.
public class FavoritesPanelController : MonoBehaviour
{
    const float PanelDistance = 1.5f;
    const float PanelWorldScale = 0.0025f;
    const int HeaderFontSize = 30;
    const int RowFontSize = 20;
    const float RowHeight = 70f;
    const float JumpButtonWidth = 160f;
    const float UnfavoriteButtonWidth = 64f;
    const float PanelWidth = 900f;
    const float PanelHeight = 700f;
    const float Padding = 40f;
    const float Spacing = 10f;

    static readonly Color BackgroundColor = new Color(0.1f, 0.1f, 0.14f, 0.9f);
    static readonly Color RowColor = new Color(0.2f, 0.2f, 0.2f, 1f);
    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.9f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);

    // Same heart color as SceneSelectController and RecordingPlaybackController - every
    // favorita is filled/red here, since this screen only ever lists favorited sessions.
    static readonly Color FavoritaColor = new Color(1f, 0.2f, 0.25f, 1f);

    Font uiFont;
    GameObject panelObject;

    void Awake()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Belonged to the scene that just unloaded - already gone, just drop the reference.
        panelObject = null;
    }

    public void Show()
    {
        PanelFocusManager.CloseModal(panelObject);
        if (panelObject != null)
            Destroy(panelObject);

        Camera headCamera = Camera.main;
        if (headCamera == null)
        {
            Debug.LogWarning("FavoritesPanelController: no Camera.main found, cannot show Favoritos.");
            return;
        }

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("FavoritesPanelController: no ShotListDatabase instance found.");
            return;
        }

        List<RecordingSession> favorites = database.GetFavoriteSessions();

        Transform head = headCamera.transform;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 panelPosition = head.position + forward * PanelDistance;
        Quaternion panelRotation = Quaternion.LookRotation(forward, Vector3.up);

        panelObject = new GameObject("FavoritesPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        RectTransform canvasRect = (RectTransform)panelObject.transform;
        canvasRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        canvasRect.position = panelPosition;
        canvasRect.rotation = panelRotation;
        canvasRect.localScale = Vector3.one * PanelWorldScale;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasRect, false);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        GlassUI.SkinPanel(background.GetComponent<Image>(), 34f);

        var rootObject = new GameObject("Root", typeof(RectTransform), typeof(VerticalLayoutGroup));
        rootObject.transform.SetParent(canvasRect, false);
        RectTransform rootRect = (RectTransform)rootObject.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = new Vector2(Padding, Padding);
        rootRect.offsetMax = new Vector2(-Padding, -Padding);

        VerticalLayoutGroup rootLayout = rootObject.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = Spacing;
        rootLayout.childAlignment = TextAnchor.UpperCenter;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        BuildHeaderRow(rootRect);
        RectTransform listContent = BuildScrollingList(rootRect);

        if (favorites.Count == 0)
            CreateEmptyMessage(listContent);
        else
            foreach (RecordingSession session in favorites)
                CreateFavoriteRow(listContent, database, session);

        XRUISetup.MoveToUILayer(panelObject);

        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();

        PanelFocusManager.OpenModal(panelObject, "Favoritos");
    }

    void BuildHeaderRow(RectTransform parent)
    {
        var rowObject = new GameObject("Header", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        // flexibleHeight 0: childForceExpandHeight below would otherwise make the header share
        // the panel's spare height with the list.
        LayoutElement headerLayout = rowObject.GetComponent<LayoutElement>();
        headerLayout.preferredHeight = HeaderFontSize + 20f;
        headerLayout.flexibleHeight = 0f;

        HorizontalLayoutGroup layout = rowObject.GetComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        var titleObject = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        titleObject.transform.SetParent(rowObject.transform, false);
        titleObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

        TMP_Text title = titleObject.GetComponent<TextMeshProUGUI>();
        title.font = GlassUI.Regular;
        title.text = "Favoritos";
        title.fontSize = HeaderFontSize;
        title.fontStyle = FontStyles.Bold;
        title.color = TextColor;
        title.alignment = TextAlignmentOptions.Left;

        var closeButtonObject = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        closeButtonObject.transform.SetParent(rowObject.transform, false);
        GlassUI.SkinButton(closeButtonObject, (HeaderFontSize + 20f) / 2f);
        LayoutElement closeLayout = closeButtonObject.GetComponent<LayoutElement>();
        closeLayout.preferredWidth = HeaderFontSize + 20f;
        closeLayout.preferredHeight = HeaderFontSize + 20f;

        var closeTextObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        closeTextObject.transform.SetParent(closeButtonObject.transform, false);
        RectTransform closeTextRect = (RectTransform)closeTextObject.transform;
        closeTextRect.anchorMin = Vector2.zero;
        closeTextRect.anchorMax = Vector2.one;
        closeTextRect.offsetMin = Vector2.zero;
        closeTextRect.offsetMax = Vector2.zero;

        TMP_Text closeText = closeTextObject.GetComponent<TextMeshProUGUI>();
        closeText.font = GlassUI.Icons;
        closeText.text = GlassUI.Icon.Close;
        closeText.fontSize = HeaderFontSize;
        closeText.fontStyle = FontStyles.Bold;
        closeText.color = TextColor;
        closeText.alignment = TextAlignmentOptions.Center;

        closeButtonObject.GetComponent<Button>().onClick.AddListener(Close);
    }

    RectTransform BuildScrollingList(RectTransform parent)
    {
        var scrollObject = new GameObject("ScrollView", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
        scrollObject.transform.SetParent(parent, false);
        scrollObject.GetComponent<Image>().color = Color.clear;
        scrollObject.GetComponent<LayoutElement>().flexibleHeight = 1f;

        var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewportObject.transform.SetParent(scrollObject.transform, false);
        RectTransform viewportRect = (RectTransform)viewportObject.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewportObject.GetComponent<Image>().color = Color.white;
        viewportObject.GetComponent<Mask>().showMaskGraphic = false;

        var contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentObject.transform.SetParent(viewportObject.transform, false);
        RectTransform contentRect = (RectTransform)contentObject.transform;
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;

        VerticalLayoutGroup contentLayout = contentObject.GetComponent<VerticalLayoutGroup>();
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.spacing = Spacing;

        contentObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        ScrollRect scroll = scrollObject.GetComponent<ScrollRect>();
        scroll.viewport = viewportRect;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;

        return contentRect;
    }

    void CreateEmptyMessage(RectTransform parent)
    {
        var textObject = new GameObject("EmptyMessage", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        textObject.transform.SetParent(parent, false);
        textObject.GetComponent<LayoutElement>().preferredHeight = RowHeight;

        TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = GlassUI.Regular;
        text.text = "Aún no tienes favoritos.";
        text.fontSize = RowFontSize;
        text.color = TextColor;
        text.alignment = TextAlignmentOptions.Center;
    }

    void CreateFavoriteRow(RectTransform parent, ShotListDatabase database, RecordingSession session)
    {
        // A scene recording (shot 0) has no Shots row - its label is the scene's.
        Shot shot = session.IsScene ? null : database.GetShot(session.ShotNumber);
        string sceneLabel = session.SceneNumber > 0 ? $"Escena {session.SceneNumber}" : "Escena ?";
        string shotLabel = session.IsScene ? "Escena completa"
            : shot != null ? $"Shot {shot.ShotNumber}: {shot.Description}" : $"Shot {session.ShotNumber}";
        string when = FormatSessionDateTime(session.FechaHora);

        var rowObject = new GameObject($"Favorite_{session.Id}", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        // The rounded background is a child outside the layout: on the row itself, its sliced
        // sprite's own size would set the row's height.
        var rowBackground = new GameObject("Background", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        rowBackground.transform.SetParent(rowObject.transform, false);
        rowBackground.GetComponent<LayoutElement>().ignoreLayout = true;
        GlassUI.Stretch((RectTransform)rowBackground.transform);
        GlassUI.Rounded(rowBackground.GetComponent<Image>(), 18f, new Color(1f, 1f, 1f, 0.07f));
        rowBackground.GetComponent<Image>().raycastTarget = false;
        // At least RowHeight, taller when a long description wraps.
        rowObject.GetComponent<LayoutElement>().minHeight = RowHeight;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = Spacing;
        rowLayout.padding = new RectOffset(16, 16, 8, 8);
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        labelObject.transform.SetParent(rowObject.transform, false);
        labelObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

        TMP_Text label = labelObject.GetComponent<TextMeshProUGUI>();
        label.font = GlassUI.Regular;
        label.text = $"{sceneLabel} — {shotLabel}\n{when}";
        label.fontSize = RowFontSize;
        label.color = TextColor;
        label.alignment = TextAlignmentOptions.Left;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;

        var jumpButtonObject = new GameObject("JumpButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        jumpButtonObject.transform.SetParent(rowObject.transform, false);
        GlassUI.SkinButton(jumpButtonObject, (RowHeight - 12f) / 2f, GlassUI.ButtonStyle.Primary);
        LayoutElement jumpLayout = jumpButtonObject.GetComponent<LayoutElement>();
        jumpLayout.preferredWidth = JumpButtonWidth;
        jumpLayout.minWidth = JumpButtonWidth;   // a long label must not squeeze the buttons
        jumpLayout.preferredHeight = RowHeight - 12f;

        var jumpTextObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        jumpTextObject.transform.SetParent(jumpButtonObject.transform, false);
        RectTransform jumpTextRect = (RectTransform)jumpTextObject.transform;
        jumpTextRect.anchorMin = Vector2.zero;
        jumpTextRect.anchorMax = Vector2.one;
        jumpTextRect.offsetMin = Vector2.zero;
        jumpTextRect.offsetMax = Vector2.zero;

        TMP_Text jumpText = jumpTextObject.GetComponent<TextMeshProUGUI>();
        jumpText.font = GlassUI.Regular;
        jumpText.text = "Ver";
        jumpText.fontSize = RowFontSize;
        jumpText.fontStyle = FontStyles.Bold;
        jumpText.color = TextColor;
        jumpText.alignment = TextAlignmentOptions.Center;

        var unfavoriteButtonObject = new GameObject("UnfavoriteButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        unfavoriteButtonObject.transform.SetParent(rowObject.transform, false);
        GlassUI.SkinButton(unfavoriteButtonObject, (RowHeight - 12f) / 2f);
        LayoutElement unfavoriteLayout = unfavoriteButtonObject.GetComponent<LayoutElement>();
        unfavoriteLayout.preferredWidth = UnfavoriteButtonWidth;
        unfavoriteLayout.minWidth = UnfavoriteButtonWidth;
        unfavoriteLayout.preferredHeight = RowHeight - 12f;

        // Filled heart - every row here is already a favorite, clicking removes it. A sprite,
        // not a glyph - see UIIcons for why.
        UIIcons.CreateIcon(unfavoriteButtonObject.transform, UIIcons.Heart(true), FavoritaColor);

        RecordingSession capturedSession = session;
        jumpButtonObject.GetComponent<Button>().onClick.AddListener(() => JumpToSession(capturedSession));
        unfavoriteButtonObject.GetComponent<Button>().onClick.AddListener(() => OnUnfavoriteClicked(capturedSession));
    }

    // Removes the session from favorita and rebuilds the whole list - it no longer belongs
    // in a screen that only ever shows favorited sessions.
    void OnUnfavoriteClicked(RecordingSession session)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("FavoritesPanelController: no ShotListDatabase instance found, cannot remove from favorites.");
            return;
        }

        database.SetFavorita(session.Id, false);
        Show();
    }

    // The take comes from the session itself (scene_number + shot_number), so a scene
    // recording opens as the whole scene.
    void JumpToSession(RecordingSession session)
    {
        if (!SceneSelectController.SceneNameByNumber.TryGetValue(session.SceneNumber, out string sceneName))
        {
            Debug.LogError($"FavoritesPanelController: could not resolve a scene for session {session.Id} (scene {session.SceneNumber}, shot {session.ShotNumber}).");
            return;
        }

        Close();

        // Favoritos is reachable mid Ensayo Remoto, and jumping to a session leaves the
        // rehearsal for Inspección de Escenas - so it ends the synced session (sending the
        // actor home). Must run before the mode switches to Inspect; it's a no-op otherwise.
#if DIRECTOR_BUILD
        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.EndRemoteRehearsal();
#endif

        Bootstrap.SetMode(Bootstrap.AppMode.Inspect, "Favoritos → Ver");
        Bootstrap.SelectTake(session.SceneNumber, session.ShotNumber);
        Bootstrap.CurrentSessionId = session.Id;
        SceneManager.LoadSceneAsync(sceneName);
    }

    void Close()
    {
        PanelFocusManager.CloseModal(panelObject);
        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;
    }

    static string FormatSessionDateTime(string fechaHora)
    {
        return DateTime.TryParse(fechaHora, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : fechaHora;
    }
}
