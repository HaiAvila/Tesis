using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). When a content scene loads while
// Bootstrap.IsInspecting is true, loads the most recent recorded session for the current
// shot and plays it back on a placeholder capsule at the original relative speed between
// samples (not instant, not accelerated), with a small transport panel (play/pause,
// restart, loop, scrub slider, time label). Never touches the director's own camera/rig -
// the capsule is a separate object.
//
// The panel is NOT parented to the camera (that causes VR motion sickness) and is NOT
// fixed in the world either. It's body-locked with a lag: it only re-anchors itself,
// smoothly, once the director strays far enough (position or angle) from where it last
// settled - see the Follow* constants and Update().
//
// If the session also has a CamaraTrack (the director's viewfinder, recorded on the same
// ticks as the actor), it plays back from the same playbackTime/slider as the ghost - no
// second timer. A small marker shows the recorded camera in the scene, and a separate
// camera copies the recorded pose/focal length into a RenderTexture shown on a monitor
// panel that follows the playback panel. The director's own head camera and XR Origin are
// never moved along the recorded path. Sessions without a camera track (older recordings,
// or "Ver replay" of an unsaved buffer) play the ghost only, exactly as before.
public class RecordingPlaybackController : MonoBehaviour
{
    public static RecordingPlaybackController Instance { get; private set; }

    const float CapsuleHeight = 1.8f;
    const float CapsuleRadius = 0.25f;
    const int IconFontSize = 36;
    const int TimeLabelFontSize = 16;
    const float ButtonSize = 60f;
    const float ButtonSpacing = 10f;
    const float SliderHeight = 10f;
    const float SliderHandleSize = 18f;
    const float PanelPadding = 14f;
    const float RowSpacing = 8f;
    const float PanelWorldScale = 0.0022f;

    // Body-locked-with-lag follow behavior: re-anchor only past these thresholds, and
    // smoothly transition over FollowTransitionDuration rather than snapping.
    const float FollowDistanceThreshold = 1.5f;
    const float FollowAngleThreshold = 45f;
    const float FollowTransitionDuration = 0.4f;
    const float FollowForwardOffset = 0.8f;
    const float FollowVerticalOffset = 0.35f;

    static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.1f, 0.45f);
    static readonly Color ButtonColor = new Color(0.3f, 0.3f, 0.36f, 0.55f);
    static readonly Color ButtonActiveColor = new Color(0.25f, 0.55f, 0.3f, 0.75f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 0.95f);
    static readonly Color CapsuleColor = new Color(1f, 0.75f, 0.1f, 1f);
    static readonly Color SliderBackgroundColor = new Color(0.05f, 0.05f, 0.08f, 0.6f);
    static readonly Color SliderFillColor = new Color(0.6f, 0.6f, 0.68f, 0.8f);
    static readonly Color SliderHandleColor = new Color(0.95f, 0.95f, 0.98f, 0.95f);

    // Same star/heart colors as SceneSelectController and FavoritesPanelController - the
    // visual language for preferida/favorita is consistent everywhere it appears.
    static readonly Color PreferidaOnColor = new Color(0.85f, 0.7f, 0.15f, 1f);
    static readonly Color PreferidaOffColor = new Color(0.4f, 0.4f, 0.4f, 1f);
    static readonly Color FavoritaOnColor = new Color(0.85f, 0.25f, 0.35f, 1f);
    static readonly Color FavoritaOffColor = new Color(0.4f, 0.4f, 0.4f, 1f);

    // Recorded-camera playback. The monitor and the scene marker live on their own project
    // layer, excluded from the playback camera's culling mask - otherwise that camera would
    // see the monitor showing its own feed (the recursive tunnel the viewfinder had) and the
    // marker sitting right on top of its own lens.
    const string MonitorLayerName = "PlaybackMonitor";
    const int MonitorTextureWidth = 640;
    const int MonitorTextureHeight = 480;
    const float MonitorWidthMeters = 0.4f;
    const int MonitorMessageFontSize = 36;
    // Placeholder placement relative to the playback panel (right and up, so it doesn't
    // cover the ghost straight ahead) - meant to be tuned visually.
    static readonly Vector3 MonitorOffsetFromPanel = new Vector3(0.45f, 0.3f, 0f);
    static readonly Color MonitorBackgroundColor = new Color(0.03f, 0.03f, 0.04f, 0.95f);
    static readonly Color MarkerBodyColor = new Color(0.15f, 0.55f, 0.95f, 1f);
    static readonly Vector3 MarkerBodySize = new Vector3(0.14f, 0.1f, 0.18f);
    static readonly Vector3 MarkerPointerSize = new Vector3(0.015f, 0.015f, 0.5f);

    GameObject capsule;
    GameObject panelObject;
    Text loopText;
    Text timeText;
    Text preferidaIcon;
    Text favoritaIcon;
    Slider scrubSlider;
    Font uiFont;

    // The DB-backed session currently playing, if any - null when reviewing an in-memory
    // buffer that hasn't been saved yet ("Ver replay" before Guardar/Descartar), in which
    // case there's no session row to mark preferida/favorita on, so those icons are hidden.
    RecordingSession currentSession;

#if DIRECTOR_BUILD
    InputAction playPauseAction;
#endif

    List<Metric> samples;
    List<CameraTrackSample> cameraSamples;
    GameObject cameraMarker;
    Camera playbackCamera;
    RenderTexture monitorRenderTexture;
    GameObject monitorObject;
    RawImage monitorFeed;
    GameObject monitorInactiveMessage;
    Text monitorToggleText;
    bool monitorEnabled;
    bool currentVisorActive;
    float totalDuration;
    float playbackTime;
    bool isPlaying;
    bool isLooping;
    bool isScrubbing;
    bool wasPlayingBeforeScrub;
    Coroutine playbackCoroutine;

    Vector3 anchorPosition;
    Quaternion anchorRotation;
    Vector3 transitionStartPosition;
    Quaternion transitionStartRotation;
    float transitionElapsed;
    bool isTransitioning;

    // Set only when playing back an in-memory buffer (post-recording "Ver replay" review,
    // not the usual DB-driven Inspección de Escenas flow) - adds a close button to the
    // panel and is invoked when it's pressed, instead of relying on a scene change to exit.
    Action onManualClose;

    void Awake()
    {
        Instance = this;
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        SceneManager.sceneLoaded += OnSceneLoaded;

#if DIRECTOR_BUILD
        // Same standard OpenXR path convention used elsewhere in this project (Menu,
        // recording shortcut), plus the XR Device Simulator's equivalent for in-Editor
        // testing. Left controller Primary Button - confirmed unused anywhere else in
        // this project's input actions.
        playPauseAction = new InputAction("PlaybackPlayPause", InputActionType.Button);
        playPauseAction.AddBinding("<XRController>{LeftHand}/{primaryButton}");
        playPauseAction.AddBinding("<XRSimulatedController>{LeftHand}/primaryButton");
        playPauseAction.performed += OnPlayPausePressed;
        playPauseAction.Enable();
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;

#if DIRECTOR_BUILD
        playPauseAction.performed -= OnPlayPausePressed;
        playPauseAction.Disable();
        playPauseAction.Dispose();
#endif
    }

#if DIRECTOR_BUILD
    void OnPlayPausePressed(InputAction.CallbackContext context)
    {
        // Only while an inspection playback session is actually loaded - this same
        // physical button means something else (or nothing) on every other screen.
        if (panelObject == null)
            return;

        OnPlayPauseClicked();
    }
#endif

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CleanUp();

        if (!Bootstrap.IsInspecting || !Bootstrap.IsContentScene(scene.name))
            return;

        LoadAndStart(Bootstrap.CurrentShotNumber);
    }

    void CleanUp()
    {
        if (playbackCoroutine != null)
            StopCoroutine(playbackCoroutine);
        playbackCoroutine = null;

        if (capsule != null)
            Destroy(capsule);
        if (panelObject != null)
            Destroy(panelObject);
        if (cameraMarker != null)
            Destroy(cameraMarker);
        if (playbackCamera != null)
            Destroy(playbackCamera.gameObject);
        if (monitorObject != null)
            Destroy(monitorObject);
        if (monitorRenderTexture != null)
            monitorRenderTexture.Release();

        capsule = null;
        panelObject = null;
        cameraSamples = null;
        cameraMarker = null;
        playbackCamera = null;
        monitorRenderTexture = null;
        monitorObject = null;
        monitorFeed = null;
        monitorInactiveMessage = null;
        monitorToggleText = null;
        monitorEnabled = false;
        currentVisorActive = false;
        loopText = null;
        timeText = null;
        preferidaIcon = null;
        favoritaIcon = null;
        scrubSlider = null;
        samples = null;
        currentSession = null;
        totalDuration = 0f;
        playbackTime = 0f;
        isPlaying = false;
        isLooping = false;
        isScrubbing = false;
        isTransitioning = false;
        onManualClose = null;

#if DIRECTOR_BUILD
        if (DirectorFreeFlyCamera.Instance != null)
            DirectorFreeFlyCamera.Instance.DisableFly();
#endif
    }

    void LoadAndStart(int shotNumber)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("RecordingPlaybackController: no ShotListDatabase instance found.");
            return;
        }

        RecordingSession session;
        if (Bootstrap.CurrentSessionId.HasValue)
        {
            session = database.GetSession(Bootstrap.CurrentSessionId.Value);
            Bootstrap.CurrentSessionId = null;
        }
        else
        {
            session = database.GetMostRecentSession(shotNumber);
        }

        if (session == null)
        {
            Debug.LogWarning($"RecordingPlaybackController: no recorded session found for shot {shotNumber}.");
            return;
        }

        List<Metric> dbSamples = database.GetMetricsForSession(session.Id);
        if (dbSamples.Count == 0)
        {
            Debug.LogWarning($"RecordingPlaybackController: session {session.Id} for shot {shotNumber} has no samples.");
            return;
        }

        BeginPlayback(dbSamples, null, session, database.GetCameraTrackForSession(session.Id));
    }

    // Plays back an in-memory buffer directly - e.g. "Ver replay" right after stopping a
    // recording, before it's saved (or discarded). Same playback system, no DB involved.
    // onClosed fires when the panel's close button is pressed, so the caller (e.g. the
    // save/discard decision popup) knows to reappear.
    public void StartPlaybackFromBuffer(List<Metric> bufferSamples, Action onClosed)
    {
        if (bufferSamples == null || bufferSamples.Count == 0)
        {
            Debug.LogWarning("RecordingPlaybackController: buffer is empty, nothing to replay.");
            onClosed?.Invoke();
            return;
        }

        CleanUp();
        BeginPlayback(bufferSamples, onClosed);
    }

    void BeginPlayback(List<Metric> samplesToPlay, Action onClosed, RecordingSession session = null, List<CameraTrackSample> cameraSamplesToPlay = null)
    {
        samples = samplesToPlay;
        totalDuration = samples[samples.Count - 1].TiempoRelativo;
        onManualClose = onClosed;
        currentSession = session;
        cameraSamples = cameraSamplesToPlay;
        monitorEnabled = true;

        BuildCapsule();
        BuildPanel();

        // Ghost only (the original behavior) when there's no camera track.
        if (HasCameraTrack)
        {
            BuildCameraMarker();
            BuildPlaybackCamera();
            BuildMonitor();
        }

        ApplySampleAt(0f);
        UpdateTimeText();
        SetPlaying(true);

#if DIRECTOR_BUILD
        if (DirectorFreeFlyCamera.Instance != null)
            DirectorFreeFlyCamera.Instance.EnableFly();
#endif

        playbackCoroutine = StartCoroutine(PlaybackLoop());
    }

    void Update()
    {
        if (panelObject == null)
            return;

        Camera headCamera = Camera.main;
        if (headCamera == null)
            return;

        ComputeDesiredAnchor(headCamera.transform, out Vector3 desiredPosition, out Quaternion desiredRotation);

        if (!isTransitioning)
        {
            float distance = Vector3.Distance(desiredPosition, anchorPosition);
            float angle = Quaternion.Angle(desiredRotation, anchorRotation);

            if (distance > FollowDistanceThreshold || angle > FollowAngleThreshold)
            {
                transitionStartPosition = panelObject.transform.position;
                transitionStartRotation = panelObject.transform.rotation;
                anchorPosition = desiredPosition;
                anchorRotation = desiredRotation;
                transitionElapsed = 0f;
                isTransitioning = true;
            }
        }

        if (isTransitioning)
        {
            transitionElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(transitionElapsed / FollowTransitionDuration);

            panelObject.transform.SetPositionAndRotation(
                Vector3.Lerp(transitionStartPosition, anchorPosition, t),
                Quaternion.Slerp(transitionStartRotation, anchorRotation, t));

            if (t >= 1f)
                isTransitioning = false;
        }

        UpdateMonitorPose();
    }

    // The monitor rides on the playback panel's own body-locked-with-lag anchor, at a fixed
    // offset in the panel's frame - so it follows the director exactly the way the panel
    // does, with no separate follow logic to drift out of step.
    void UpdateMonitorPose()
    {
        if (monitorObject == null)
            return;

        Transform panelTransform = panelObject.transform;
        monitorObject.transform.SetPositionAndRotation(
            panelTransform.position + panelTransform.rotation * MonitorOffsetFromPanel,
            panelTransform.rotation);
    }

    // Yaw-only (flattened) so looking up/down doesn't drag the panel into the floor or
    // fling it into the ceiling - it stays a fixed height below eye level regardless.
    static void ComputeDesiredAnchor(Transform head, out Vector3 position, out Quaternion rotation)
    {
        Vector3 flatForward = head.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f)
            flatForward = Vector3.forward;
        flatForward.Normalize();

        position = head.position + flatForward * FollowForwardOffset + Vector3.down * FollowVerticalOffset;
        rotation = Quaternion.LookRotation(flatForward, Vector3.up);
    }

    void BuildCapsule()
    {
        capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "PlaybackCapsule";
        capsule.transform.localScale = new Vector3(CapsuleRadius * 2f, CapsuleHeight * 0.5f, CapsuleRadius * 2f);
        Destroy(capsule.GetComponent<Collider>());
        capsule.GetComponent<Renderer>().material.color = CapsuleColor;
    }

    void BuildPanel()
    {
        Camera headCamera = Camera.main;
        Transform head = headCamera != null ? headCamera.transform : transform;
        ComputeDesiredAnchor(head, out anchorPosition, out anchorRotation);
        isTransitioning = false;

        float buttonCount = 2f; // restart + loop
        if (currentSession != null)
            buttonCount += 2f; // preferida + favorita - only when a saved session (not an unsaved buffer) is playing
        if (onManualClose != null)
            buttonCount += 1f; // close
        if (HasCameraTrack)
            buttonCount += 1f; // monitor toggle
        float panelWidth = ButtonSize * buttonCount + ButtonSpacing * (buttonCount - 1f) + PanelPadding * 2f;
        float panelHeight = ButtonSize + RowSpacing + SliderHeight + RowSpacing + (TimeLabelFontSize + 6f) + PanelPadding * 2f;

        panelObject = new GameObject("PlaybackPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        RectTransform canvasRect = (RectTransform)panelObject.transform;
        canvasRect.sizeDelta = new Vector2(panelWidth, panelHeight);
        canvasRect.position = anchorPosition;
        canvasRect.rotation = anchorRotation;
        canvasRect.localScale = Vector3.one * PanelWorldScale;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasRect, false);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        background.GetComponent<Image>().color = BackgroundColor;

        var rootLayoutObject = new GameObject("Root", typeof(RectTransform), typeof(VerticalLayoutGroup));
        rootLayoutObject.transform.SetParent(canvasRect, false);
        RectTransform rootRect = (RectTransform)rootLayoutObject.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = new Vector2(PanelPadding, PanelPadding);
        rootRect.offsetMax = new Vector2(-PanelPadding, -PanelPadding);

        VerticalLayoutGroup rootLayout = rootLayoutObject.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = RowSpacing;
        rootLayout.childAlignment = TextAnchor.UpperCenter;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        BuildButtonRow(rootRect);
        BuildSlider(rootRect);
        BuildTimeText(rootRect);

        XRUISetup.MoveToUILayer(panelObject);

        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();
    }

    void BuildButtonRow(RectTransform parent)
    {
        var rowObject = new GameObject("ButtonRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        rowObject.GetComponent<LayoutElement>().preferredHeight = ButtonSize;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = ButtonSpacing;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = false;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        // Symbols instead of labels, per design direction - real icon sprites come later.
        // Play/Pause has no UI button - it's the physical controller shortcut only.
        CreateIconButton(rowObject.transform, "↺", OnRestartClicked, ButtonColor); // ↺
        loopText = CreateIconButton(rowObject.transform, "∞", OnLoopToggleClicked, ButtonColor); // ∞ (loop toggle; color indicates on/off)

        // Only present for a DB-backed session - an unsaved buffer ("Ver replay" before
        // Guardar/Descartar) has no session row to mark. Same star/heart language as the
        // session list and Favoritos; clicking preferida always (re-)assigns it - it's
        // exclusive per shot, there's no "unset" from here, same as the session list.
        if (currentSession != null)
        {
            preferidaIcon = CreateIconButton(rowObject.transform, currentSession.EsPreferida ? "★" : "☆", OnPreferidaClicked, ButtonColor);
            preferidaIcon.color = currentSession.EsPreferida ? PreferidaOnColor : PreferidaOffColor;

            favoritaIcon = CreateIconButton(rowObject.transform, currentSession.EsFavorita ? "♥" : "♡", OnFavoritaClicked, ButtonColor);
            favoritaIcon.color = currentSession.EsFavorita ? FavoritaOnColor : FavoritaOffColor;
        }

        // Shows/hides the recorded-camera monitor (on by default; color indicates on/off,
        // same as the loop toggle). Only present when the session has a camera track.
        if (HasCameraTrack)
            monitorToggleText = CreateIconButton(rowObject.transform, "▣", OnMonitorToggleClicked, ButtonActiveColor);

        // Only present when reviewing an in-memory buffer (no scene change to exit through).
        if (onManualClose != null)
            CreateIconButton(rowObject.transform, "✕", OnCloseClicked, ButtonColor);
    }

    void OnMonitorToggleClicked()
    {
        monitorEnabled = !monitorEnabled;
        RefreshMonitorState();

        if (monitorToggleText == null)
            return;

        Image toggleButtonImage = monitorToggleText.transform.parent.GetComponent<Image>();
        if (toggleButtonImage != null)
            toggleButtonImage.color = monitorEnabled ? ButtonActiveColor : ButtonColor;
    }

    void OnPreferidaClicked()
    {
        if (currentSession == null)
            return;

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("RecordingPlaybackController: no ShotListDatabase instance found, cannot mark preferida.");
            return;
        }

        database.SetPreferida(currentSession.Id, currentSession.ShotNumber);
        currentSession.EsPreferida = true;

        if (preferidaIcon != null)
        {
            preferidaIcon.text = "★";
            preferidaIcon.color = PreferidaOnColor;
        }
    }

    void OnFavoritaClicked()
    {
        if (currentSession == null)
            return;

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("RecordingPlaybackController: no ShotListDatabase instance found, cannot mark favorita.");
            return;
        }

        bool newValue = !currentSession.EsFavorita;
        database.SetFavorita(currentSession.Id, newValue);
        currentSession.EsFavorita = newValue;

        if (favoritaIcon != null)
        {
            favoritaIcon.text = newValue ? "♥" : "♡";
            favoritaIcon.color = newValue ? FavoritaOnColor : FavoritaOffColor;
        }
    }

    Text CreateIconButton(Transform parent, string symbol, UnityEngine.Events.UnityAction onClick, Color color)
    {
        var buttonObject = new GameObject($"Button_{symbol}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        RectTransform buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.sizeDelta = new Vector2(ButtonSize, ButtonSize);
        buttonObject.GetComponent<Image>().color = color;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.text = symbol;
        text.fontSize = IconFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = TextColor;
        text.alignment = TextAnchor.MiddleCenter;

        return text;
    }

    void BuildSlider(RectTransform parent)
    {
        var sliderObject = new GameObject("ScrubSlider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        sliderObject.transform.SetParent(parent, false);
        sliderObject.GetComponent<LayoutElement>().preferredHeight = SliderHandleSize;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(sliderObject.transform, false);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = new Vector2(0f, 0.5f);
        backgroundRect.anchorMax = new Vector2(1f, 0.5f);
        backgroundRect.sizeDelta = new Vector2(0f, SliderHeight);
        background.GetComponent<Image>().color = SliderBackgroundColor;

        var fillArea = new GameObject("Fill Area", typeof(RectTransform));
        fillArea.transform.SetParent(sliderObject.transform, false);
        RectTransform fillAreaRect = (RectTransform)fillArea.transform;
        fillAreaRect.anchorMin = new Vector2(0f, 0.5f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.5f);
        fillAreaRect.sizeDelta = new Vector2(0f, SliderHeight);

        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fillRect = (RectTransform)fill.transform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = new Vector2(0f, 1f);
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = SliderFillColor;

        var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleArea.transform.SetParent(sliderObject.transform, false);
        RectTransform handleAreaRect = (RectTransform)handleArea.transform;
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.offsetMin = Vector2.zero;
        handleAreaRect.offsetMax = Vector2.zero;

        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        RectTransform handleRect = (RectTransform)handle.transform;
        handleRect.sizeDelta = new Vector2(SliderHandleSize, SliderHandleSize);
        handle.GetComponent<Image>().color = SliderHandleColor;

        scrubSlider = sliderObject.GetComponent<Slider>();
        scrubSlider.targetGraphic = handle.GetComponent<Image>();
        scrubSlider.fillRect = fillRect;
        scrubSlider.handleRect = handleRect;
        scrubSlider.direction = Slider.Direction.LeftToRight;
        scrubSlider.minValue = 0f;
        scrubSlider.maxValue = Mathf.Max(totalDuration, 0.01f);
        scrubSlider.wholeNumbers = false;
        scrubSlider.SetValueWithoutNotify(0f);
        scrubSlider.onValueChanged.AddListener(OnSliderValueChanged);

        // A plain sibling MonoBehaviour implementing IPointerDownHandler/IPointerUpHandler
        // next to the Slider component turned out not to reliably receive events here -
        // EventTrigger is Unity's own documented mechanism for exactly this ("detect
        // drag start/end on a Slider") and sidesteps whatever the ambiguity was.
        EventTrigger trigger = sliderObject.AddComponent<EventTrigger>();

        var pointerDownEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        pointerDownEntry.callback.AddListener(_ => OnScrubStart());
        trigger.triggers.Add(pointerDownEntry);

        var pointerUpEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
        pointerUpEntry.callback.AddListener(_ => OnScrubEnd());
        trigger.triggers.Add(pointerUpEntry);
    }

    void BuildTimeText(RectTransform parent)
    {
        var textObject = new GameObject("TimeText", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        textObject.transform.SetParent(parent, false);
        textObject.GetComponent<LayoutElement>().preferredHeight = TimeLabelFontSize + 6f;

        timeText = textObject.GetComponent<Text>();
        timeText.font = uiFont;
        timeText.fontSize = TimeLabelFontSize;
        timeText.color = TextColor;
        timeText.alignment = TextAnchor.MiddleCenter;
    }

    void OnPlayPauseClicked()
    {
        if (!isPlaying && playbackTime >= totalDuration)
            playbackTime = 0f;

        SetPlaying(!isPlaying);
    }

    void OnRestartClicked()
    {
        playbackTime = 0f;
        ApplySampleAt(playbackTime);
        UpdateSliderAndTimeText();
        SetPlaying(true);
    }

    void OnCloseClicked()
    {
        Action callback = onManualClose;
        CleanUp();
        callback?.Invoke();
    }

    void OnLoopToggleClicked()
    {
        isLooping = !isLooping;
        if (loopText == null)
            return;

        Image loopButtonImage = loopText.transform.parent.GetComponent<Image>();
        if (loopButtonImage != null)
            loopButtonImage.color = isLooping ? ButtonActiveColor : ButtonColor;
    }

    void OnScrubStart()
    {
        wasPlayingBeforeScrub = isPlaying;
        isScrubbing = true;
        SetPlaying(false);
    }

    void OnScrubEnd()
    {
        isScrubbing = false;
        if (wasPlayingBeforeScrub)
            SetPlaying(true);
    }

    void OnSliderValueChanged(float value)
    {
        if (!isScrubbing)
            return;

        playbackTime = value;
        ApplySampleAt(playbackTime);
        UpdateTimeText();
    }

    void SetPlaying(bool playing)
    {
        isPlaying = playing;
    }

    IEnumerator PlaybackLoop()
    {
        while (true)
        {
            if (isPlaying && samples != null)
            {
                playbackTime += Time.deltaTime;

                if (playbackTime >= totalDuration)
                {
                    if (isLooping)
                    {
                        playbackTime = 0f;
                    }
                    else
                    {
                        playbackTime = totalDuration;
                        SetPlaying(false);
                    }
                }

                ApplySampleAt(playbackTime);
                UpdateSliderAndTimeText();
            }

            yield return null;
        }
    }

    void UpdateSliderAndTimeText()
    {
        if (scrubSlider != null)
            scrubSlider.SetValueWithoutNotify(playbackTime);
        UpdateTimeText();
    }

    void UpdateTimeText()
    {
        if (timeText != null)
            timeText.text = $"{playbackTime:F1}s / {totalDuration:F1}s";
    }

    // The single place playback time turns into poses - called from the playback loop, the
    // slider and restart alike, so the ghost and the recorded camera always share one time.
    void ApplySampleAt(float time)
    {
        ApplyCameraSampleAt(time);

        if (samples == null || samples.Count == 0 || capsule == null)
            return;

        Metric first = samples[0];
        if (time <= first.TiempoRelativo)
        {
            SetCapsuleTransform(first);
            return;
        }

        Metric last = samples[samples.Count - 1];
        if (time >= last.TiempoRelativo)
        {
            SetCapsuleTransform(last);
            return;
        }

        for (int i = 0; i < samples.Count - 1; i++)
        {
            Metric a = samples[i];
            Metric b = samples[i + 1];
            if (time < a.TiempoRelativo || time > b.TiempoRelativo)
                continue;

            float span = b.TiempoRelativo - a.TiempoRelativo;
            float t = span > 0f ? (time - a.TiempoRelativo) / span : 0f;

            Vector3 position = Vector3.Lerp(
                new Vector3(a.PosX, a.PosY, a.PosZ),
                new Vector3(b.PosX, b.PosY, b.PosZ), t);
            Quaternion rotation = Quaternion.Slerp(
                new Quaternion(a.RotX, a.RotY, a.RotZ, a.RotW),
                new Quaternion(b.RotX, b.RotY, b.RotZ, b.RotW), t);

            capsule.transform.SetPositionAndRotation(position, rotation);
            return;
        }
    }

    void SetCapsuleTransform(Metric sample)
    {
        capsule.transform.SetPositionAndRotation(
            new Vector3(sample.PosX, sample.PosY, sample.PosZ),
            new Quaternion(sample.RotX, sample.RotY, sample.RotZ, sample.RotW));
    }

    bool HasCameraTrack => cameraSamples != null && cameraSamples.Count > 0;

    // Same interpolation as the ghost: Lerp position, Slerp rotation, plus Lerp focal_mm.
    // visor_activo is a flag, not a quantity, so it isn't blended - it takes the value of
    // the sample the segment starts from.
    void ApplyCameraSampleAt(float time)
    {
        if (!HasCameraTrack || playbackCamera == null)
            return;

        GetCameraSampleAt(time, out Vector3 position, out Quaternion rotation, out float focalMM, out bool visorActive);

        playbackCamera.transform.SetPositionAndRotation(position, rotation);
        playbackCamera.focalLength = Mathf.Max(focalMM, 1f);

        if (cameraMarker != null)
        {
            cameraMarker.transform.SetPositionAndRotation(position, rotation);
            cameraMarker.SetActive(visorActive);
        }

        currentVisorActive = visorActive;
        RefreshMonitorState();
    }

    void GetCameraSampleAt(float time, out Vector3 position, out Quaternion rotation, out float focalMM, out bool visorActive)
    {
        CameraTrackSample first = cameraSamples[0];
        CameraTrackSample last = cameraSamples[cameraSamples.Count - 1];
        CameraTrackSample a = first;
        CameraTrackSample b = first;
        float t = 0f;

        if (time >= last.TiempoRelativo)
        {
            a = last;
            b = last;
        }
        else if (time > first.TiempoRelativo)
        {
            for (int i = 0; i < cameraSamples.Count - 1; i++)
            {
                if (time < cameraSamples[i].TiempoRelativo || time > cameraSamples[i + 1].TiempoRelativo)
                    continue;

                a = cameraSamples[i];
                b = cameraSamples[i + 1];
                float span = b.TiempoRelativo - a.TiempoRelativo;
                t = span > 0f ? (time - a.TiempoRelativo) / span : 0f;
                break;
            }
        }

        position = Vector3.Lerp(new Vector3(a.PosX, a.PosY, a.PosZ), new Vector3(b.PosX, b.PosY, b.PosZ), t);
        rotation = Quaternion.Slerp(
            new Quaternion(a.RotX, a.RotY, a.RotZ, a.RotW),
            new Quaternion(b.RotX, b.RotY, b.RotZ, b.RotW), t);
        focalMM = Mathf.Lerp(a.FocalMM, b.FocalMM, t);
        visorActive = t >= 1f ? b.VisorActivo : a.VisorActivo;
    }

    // Monitor visibility follows the toggle; image vs "Visor no activo" follows the recorded
    // flag. The playback camera only renders when its image is actually on screen.
    void RefreshMonitorState()
    {
        if (monitorObject != null)
            monitorObject.SetActive(monitorEnabled);
        if (monitorFeed != null)
            monitorFeed.enabled = currentVisorActive;
        if (monitorInactiveMessage != null)
            monitorInactiveMessage.SetActive(!currentVisorActive);
        if (playbackCamera != null)
            playbackCamera.enabled = monitorEnabled && currentVisorActive;
    }

    static void ApplyLayer(GameObject obj, int layer)
    {
        if (layer >= 0)
            obj.layer = layer;
    }

    // A small box for the camera body plus a long thin box sticking out the front to show
    // where it's pointing. No colliders. On the monitor layer so the playback camera, which
    // sits exactly where this marker is, doesn't render it.
    void BuildCameraMarker()
    {
        int monitorLayer = LayerMask.NameToLayer(MonitorLayerName);

        cameraMarker = new GameObject("RecordedCameraMarker");
        ApplyLayer(cameraMarker, monitorLayer);

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(cameraMarker.transform, false);
        body.transform.localScale = MarkerBodySize;
        ApplyLayer(body, monitorLayer);

        GameObject pointer = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pointer.name = "Direction";
        pointer.transform.SetParent(cameraMarker.transform, false);
        pointer.transform.localScale = MarkerPointerSize;
        pointer.transform.localPosition = new Vector3(0f, 0f, (MarkerBodySize.z + MarkerPointerSize.z) * 0.5f);
        ApplyLayer(pointer, monitorLayer);

        foreach (GameObject part in new[] { body, pointer })
        {
            Destroy(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().material.color = MarkerBodyColor;
        }

        cameraMarker.SetActive(false);
    }

    // A standalone world camera (never parented to the director's rig) with the same
    // physical camera setup as the viewfinder - 36x24mm full-frame sensor, horizontal gate
    // fit, driven by focalLength - so focal_mm turns into the same FOV the director saw.
    void BuildPlaybackCamera()
    {
        Camera mainCamera = Camera.main;
        int monitorLayer = LayerMask.NameToLayer(MonitorLayerName);
        if (monitorLayer < 0)
            Debug.LogWarning($"RecordingPlaybackController: layer '{MonitorLayerName}' not found - the playback camera will see its own monitor (recursive feedback).");

        monitorRenderTexture = new RenderTexture(MonitorTextureWidth, MonitorTextureHeight, 16);
        monitorRenderTexture.Create();

        var cameraObject = new GameObject("RecordedCameraPlayback", typeof(Camera));
        playbackCamera = cameraObject.GetComponent<Camera>();

        if (mainCamera != null)
        {
            playbackCamera.clearFlags = mainCamera.clearFlags;
            playbackCamera.backgroundColor = mainCamera.backgroundColor;
            playbackCamera.nearClipPlane = mainCamera.nearClipPlane;
            playbackCamera.farClipPlane = mainCamera.farClipPlane;
            playbackCamera.cullingMask = mainCamera.cullingMask;
        }

        if (monitorLayer >= 0)
            playbackCamera.cullingMask &= ~(1 << monitorLayer);

        // Same "scene only" rule as the live viewfinder: no panels (this replay panel
        // included) and no controller rays in the recorded camera's view.
        playbackCamera.cullingMask &= ~(1 << XRUISetup.UILayer);

        int rayLayer = XRUISetup.MoveControllerRaysToOwnLayer();
        if (rayLayer >= 0)
            playbackCamera.cullingMask &= ~(1 << rayLayer);

        int viewfinderPanelLayer = LayerMask.NameToLayer("DirectorViewfinderPanel");
        if (viewfinderPanelLayer >= 0)
            playbackCamera.cullingMask &= ~(1 << viewfinderPanelLayer);

        playbackCamera.usePhysicalProperties = true;
        playbackCamera.sensorSize = new Vector2(36f, 24f);
        playbackCamera.gateFit = Camera.GateFitMode.Horizontal;
        playbackCamera.targetTexture = monitorRenderTexture;
    }

    // World Space canvas showing the playback camera's RenderTexture, with a "Visor no
    // activo" cover for the stretches where the viewfinder wasn't raised. Purely visual - no
    // buttons, so no GraphicRaycaster.
    void BuildMonitor()
    {
        int monitorLayer = LayerMask.NameToLayer(MonitorLayerName);

        monitorObject = new GameObject("RecordedCameraMonitor", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        ApplyLayer(monitorObject, monitorLayer);

        Canvas canvas = monitorObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        RectTransform canvasRect = (RectTransform)monitorObject.transform;
        canvasRect.sizeDelta = new Vector2(MonitorTextureWidth, MonitorTextureHeight);
        canvasRect.localScale = Vector3.one * (MonitorWidthMeters / MonitorTextureWidth);

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasRect, false);
        ApplyLayer(background, monitorLayer);
        StretchToParent((RectTransform)background.transform);
        background.GetComponent<Image>().color = MonitorBackgroundColor;

        var feedObject = new GameObject("Feed", typeof(RectTransform), typeof(RawImage));
        feedObject.transform.SetParent(canvasRect, false);
        ApplyLayer(feedObject, monitorLayer);
        StretchToParent((RectTransform)feedObject.transform);
        monitorFeed = feedObject.GetComponent<RawImage>();
        monitorFeed.texture = monitorRenderTexture;

        monitorInactiveMessage = new GameObject("VisorNoActivo", typeof(RectTransform), typeof(Text));
        monitorInactiveMessage.transform.SetParent(canvasRect, false);
        ApplyLayer(monitorInactiveMessage, monitorLayer);
        StretchToParent((RectTransform)monitorInactiveMessage.transform);
        Text message = monitorInactiveMessage.GetComponent<Text>();
        message.font = uiFont;
        message.text = "Visor no activo";
        message.fontSize = MonitorMessageFontSize;
        message.fontStyle = FontStyle.Bold;
        message.color = TextColor;
        message.alignment = TextAnchor.MiddleCenter;

        UpdateMonitorPose();
        RefreshMonitorState();
    }

    static void StretchToParent(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
