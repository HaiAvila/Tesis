using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). When a content scene loads while
// Bootstrap.Mode is Inspect, loads the most recent recorded session for the current
// shot and plays it back on the actor's avatar at the original relative speed between
// samples (not instant, not accelerated), with a small transport panel (play/pause,
// restart, loop, scrub slider, time label). Never touches the director's own camera/rig -
// the avatar is a separate object (PlaybackAvatar), and the live one hides while the panel
// is open.
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
// or "Ver replay" of an unsaved buffer) play the avatar only.
public class RecordingPlaybackController : MonoBehaviour
{
    public static RecordingPlaybackController Instance { get; private set; }

    // Read by the tutorial: whether a replay panel is up, its play state, and how many
    // times the slider has been dragged and released.
    public bool IsPanelOpen => panelObject != null;
    public bool IsPlaying => isPlaying;
    public RecordingSession CurrentSession => currentSession;
    public float PlaybackTime => playbackTime;
    public int ScrubCount { get; private set; }

    const int IconFontSize = 36;
    const int TimeLabelFontSize = 16;
    const float ButtonSize = 60f;
    const float ButtonSpacing = 10f;
    const float SliderHeight = 10f;
    const float SliderHandleSize = 18f;
    // Shot boundaries of a full-scene recording: a tick on the slider at each t_in, its shot
    // number ("T6") in a row under the slider. Placeholder look, not tuned.
    const float TickWidth = 3f;
    const float TickLabelRowHeight = 16f;
    const int TickLabelFontSize = 13;
    const float TickLabelWidth = 40f;
    static readonly Color TickColor = new Color(1f, 0.82f, 0.3f, 1f);
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
    static readonly Color SliderBackgroundColor = new Color(0.05f, 0.05f, 0.08f, 0.6f);
    static readonly Color SliderFillColor = new Color(0.6f, 0.6f, 0.68f, 0.8f);
    static readonly Color SliderHandleColor = new Color(0.95f, 0.95f, 0.98f, 0.95f);

    // Same star/heart colors as SceneSelectController and FavoritesPanelController - the
    // visual language for preferida/favorita is consistent everywhere it appears.
    static readonly Color PreferidaOnColor = new Color(0.85f, 0.7f, 0.15f, 1f);
    static readonly Color PreferidaOffColor = new Color(0.75f, 0.75f, 0.78f, 1f);
    static readonly Color FavoritaOnColor = new Color(1f, 0.2f, 0.25f, 1f);
    static readonly Color FavoritaOffColor = new Color(0.75f, 0.75f, 0.78f, 1f);

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

    PlaybackAvatar playbackAvatar;
#if DIRECTOR_BUILD
    // The take's sound: the conversation's two tracks and the notes said at a moment of it, each
    // playing in step with the replay at 1× (at another speed it would sound distorted, so it's
    // silent). Each kind can be silenced from the panel.
    class TimedNote
    {
        public float Time;
        public AudioClip Clip;
    }

    const float AudioDriftSeconds = 0.15f;
    const int AudioToggleFontSize = 18;
    AudioSource directorSource, actorSource, noteSource;
    readonly List<TimedNote> timedNotes = new List<TimedNote>();
    bool conversationAudible = true;
    bool notesAudible = true;
    Text conversationToggleText, notesToggleText;
    bool HasConversation => (directorSource != null && directorSource.clip != null) || (actorSource != null && actorSource.clip != null);
    bool HasAudio => HasConversation || timedNotes.Count > 0;
    const float NoteTickWidth = 6f;
    static readonly Color NoteTickColor = new Color(1f, 0.79f, 0.29f, 1f);
    readonly List<GameObject> noteTicks = new List<GameObject>();
#endif
    GameObject panelObject;
    Image playPauseIcon;
    Text loopText;
    Text speedText;

    // The speed button cycles through these, from 1×. Slow ones to study a movement, fast ones
    // to get through a long take. Back to 1× for every new replay.
    static readonly float[] Speeds = { 1f, 1.5f, 2f, 0.25f, 0.5f };
    const int SpeedFontSize = 22;
    int speedIndex;
    public float PlaybackSpeed => Speeds[speedIndex];
    Text timeText;
    Image preferidaIcon;
    Image favoritaIcon;
    Slider scrubSlider;
    Font uiFont;

    // The DB-backed session currently playing, if any - null when reviewing an in-memory
    // buffer that hasn't been saved yet ("Ver replay" before Guardar/Descartar), in which
    // case there's no session row to mark preferida/favorita on, so those icons are hidden.
    RecordingSession currentSession;

    // A full-scene recording's shot boundaries (saved, or still in the recorder's memory for
    // "Ver replay"): a tick on the slider at each t_in, labeled with the shot number. Null
    // for a single shot.
    List<ShotBoundary> boundaries;

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
    bool wasPlayingBeforeFocus;
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
        // physical button means something else (or nothing) on every other screen. And not
        // while a menu is open: the replay is paused for it and resumes when it closes.
        if (panelObject == null || PanelFocusManager.IsModalOpen)
            return;

        OnPlayPauseClicked();
    }
#endif

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CleanUp();

        // Inspección only - never Ensayar Solo, which used to share the "inspecting" flag
        // and so showed the replay panel, the ghost and the recorded camera to the actor.
        if (Bootstrap.Mode != Bootstrap.AppMode.Inspect || !Bootstrap.IsContentScene(scene.name))
            return;

        LoadAndStart(Bootstrap.CurrentShotNumber);
    }

    void CleanUp()
    {
        if (playbackCoroutine != null)
            StopCoroutine(playbackCoroutine);
        playbackCoroutine = null;

        PanelFocusManager.Unregister(panelObject);
        PanelFocusManager.Unregister(monitorObject);

        if (playbackAvatar != null)
            playbackAvatar.Destroy();
#if DIRECTOR_BUILD
        UnloadAudio();
        conversationAudible = true;
        notesAudible = true;
        conversationToggleText = null;
        notesToggleText = null;
#endif
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

        playbackAvatar = null;
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
        playPauseIcon = null;
        loopText = null;
        speedText = null;
        speedIndex = 0;
        timeText = null;
        preferidaIcon = null;
        favoritaIcon = null;
        scrubSlider = null;
        samples = null;
        currentSession = null;
        boundaries = null;
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

        // The whole scene ("Escena completa", shot 0) plays the scene's full-scene recordings,
        // never a single shot's.
        // A shot opened to walk the set, without a take: nothing to play.
        if (Bootstrap.InspectWithoutTake)
        {
            Bootstrap.InspectWithoutTake = false;
            Bootstrap.CurrentSessionId = null;
            FloatingMessage.ShowLong("Sin toma: recorre el set y deja notas de voz si quieres", 6f);
            return;
        }

        RecordingSession session;
        if (Bootstrap.CurrentSessionId.HasValue)
        {
            session = database.GetSession(Bootstrap.CurrentSessionId.Value);
            Bootstrap.CurrentSessionId = null;
        }
        else if (Bootstrap.IsFullScene)
        {
            List<RecordingSession> sceneSessions = database.GetSceneSessions(Bootstrap.CurrentSceneNumber);
            session = sceneSessions.Count > 0 ? sceneSessions[0] : null;
        }
        else
        {
            session = database.GetMostRecentSession(shotNumber);
        }

        if (session == null)
        {
            Debug.LogWarning(Bootstrap.IsFullScene
                ? $"[Escena] no full-scene recording found for scene {Bootstrap.CurrentSceneNumber}."
                : $"RecordingPlaybackController: no recorded session found for shot {shotNumber}.");
            FloatingMessage.ShowLong("Este shot no tiene tomas: recorre el set y deja notas de voz si quieres", 6f);
            return;
        }

        List<Metric> dbSamples = database.GetMetricsForSession(session.Id);
        if (dbSamples.Count == 0)
        {
            Debug.LogWarning($"RecordingPlaybackController: session {session.Id} for shot {shotNumber} has no samples.");
            return;
        }

        List<ShotBoundary> sessionBoundaries = null;
        if (session.IsScene)
        {
            sessionBoundaries = database.GetBoundariesForSession(session.SessionGuid);
            Debug.Log($"[Escena] replaying full-scene session {session.Id} (scene {session.SceneNumber}) with {sessionBoundaries.Count} shot boundaries");
        }

        BeginPlayback(dbSamples, null, session, database.GetCameraTrackForSession(session.Id), sessionBoundaries, database.GetPoseTrackForSession(session.Id), session.AvatarLayout);
    }

    // Plays back an in-memory buffer directly - e.g. "Ver replay" right after stopping a
    // recording, before it's saved (or discarded). Same playback system, no DB involved.
    // onClosed fires when the panel's close button is pressed, so the caller (e.g. the
    // save/discard decision popup) knows to reappear. bufferBoundaries: a full-scene take's
    // shot boundaries, still in memory too (null for a single shot).
    public void StartPlaybackFromBuffer(List<Metric> bufferSamples, Action onClosed, IReadOnlyList<ShotBoundary> bufferBoundaries = null, List<PoseSample> bufferPoses = null, byte[] bufferLayout = null)
    {
        if (bufferSamples == null || bufferSamples.Count == 0)
        {
            Debug.LogWarning("RecordingPlaybackController: buffer is empty, nothing to replay.");
            onClosed?.Invoke();
            return;
        }

        CleanUp();
        BeginPlayback(bufferSamples, onClosed, null, null, bufferBoundaries != null ? new List<ShotBoundary>(bufferBoundaries) : null, bufferPoses, bufferLayout);
    }

    void BeginPlayback(List<Metric> samplesToPlay, Action onClosed, RecordingSession session = null, List<CameraTrackSample> cameraSamplesToPlay = null, List<ShotBoundary> shotBoundaries = null, List<PoseSample> poseFrames = null, byte[] avatarLayout = null)
    {
        samples = samplesToPlay;
        totalDuration = samples[samples.Count - 1].TiempoRelativo;
        onManualClose = onClosed;
        currentSession = session;
        cameraSamples = cameraSamplesToPlay;
        boundaries = shotBoundaries != null && shotBoundaries.Count > 0 ? shotBoundaries : null;
        monitorEnabled = true;

        BuildAvatar(poseFrames, avatarLayout);
#if DIRECTOR_BUILD
        LoadAudio();
#endif
        BuildPanel();
#if DIRECTOR_BUILD
        RefreshNoteTicks();
#endif

        // Avatar only when there's no camera track.
        if (HasCameraTrack)
        {
            BuildCameraMarker();
            BuildPlaybackCamera();
            BuildMonitor();
        }

        ApplySampleAt(0f);
        UpdateTimeText();
        SetPlaying(true);

        // Persistent panels: hidden (and the replay paused) while a menu is open - see
        // PanelFocusManager. Registered after SetPlaying, so if a menu is already open the
        // replay starts paused and resumes when it closes.
        PanelFocusManager.RegisterPersistent(panelObject, "Panel de reproducción", OnFocusSuspend, OnFocusResume);
        if (monitorObject != null)
            PanelFocusManager.RegisterPersistent(monitorObject, "Monitor de cámara grabada");

#if DIRECTOR_BUILD
        if (DirectorFreeFlyCamera.Instance != null)
            DirectorFreeFlyCamera.Instance.EnableFly();
#endif

        playbackCoroutine = StartCoroutine(PlaybackLoop());
    }

    // A menu opened over the replay: pause, remembering whether it was playing.
    void OnFocusSuspend()
    {
        wasPlayingBeforeFocus = isPlaying;
        SetPlaying(false);
        Debug.Log($"[PanelFocus] replay paused for a menu (was playing: {wasPlayingBeforeFocus})");
    }

    // The last menu closed: back to exactly the play state it had.
    void OnFocusResume()
    {
        if (wasPlayingBeforeFocus)
            SetPlaying(true);
        Debug.Log($"[PanelFocus] replay restored (playing: {isPlaying})");
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

    void BuildAvatar(List<PoseSample> poseFrames, byte[] avatarLayout)
    {
        if (AvatarPoseSync.Instance == null)
        {
            Debug.LogError("RecordingPlaybackController: no AvatarPoseSync instance found, the replay has no avatar.");
            return;
        }

        playbackAvatar = new PlaybackAvatar(AvatarPoseSync.Instance.CreatePlaybackAvatar(), poseFrames, avatarLayout);
        Debug.Log($"[Replay] avatar: {(poseFrames != null ? poseFrames.Count : 0)} body frames");
        if (!playbackAvatar.HasBodyData)
            FloatingMessage.Show("Toma sin datos del cuerpo");
    }

    void BuildPanel()
    {
        Camera headCamera = Camera.main;
        Transform head = headCamera != null ? headCamera.transform : transform;
        ComputeDesiredAnchor(head, out anchorPosition, out anchorRotation);
        isTransitioning = false;

        float buttonCount = 4f; // play/pause + restart + loop + speed
        if (currentSession != null)
            buttonCount += currentSession.IsScene ? 1f : 2f; // preferida (not for a scene recording) + favorita - only when a saved session (not an unsaved buffer) is playing
        if (onManualClose != null)
            buttonCount += 1f; // close
        if (HasCameraTrack)
            buttonCount += 1f; // monitor toggle
#if DIRECTOR_BUILD
        buttonCount += currentSession != null ? 2f : 1f; // notes + delete (only a saved take)
        if (HasConversation)
            buttonCount += 1f; // conversation on/off
        if (timedNotes.Count > 0)
            buttonCount += 1f; // notes on/off
#endif
        float panelWidth = ButtonSize * buttonCount + ButtonSpacing * (buttonCount - 1f) + PanelPadding * 2f;
        float panelHeight = ButtonSize + RowSpacing + SliderHeight + RowSpacing + (TimeLabelFontSize + 6f) + PanelPadding * 2f;
        if (boundaries != null)
            panelHeight += TickLabelRowHeight + RowSpacing; // the shot numbers under the slider's ticks

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
        if (boundaries != null)
            BuildShotTicks(rootRect);
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

        // Symbols instead of labels, per design direction. Star and heart are sprites
        // (UIIcons), not glyphs - see UIIcons for why.
        // Play/Pause is also on left A (OnPlayPausePressed); both go through SetPlaying, which
        // keeps this icon in sync.
        playPauseIcon = CreateSpriteIconButton(rowObject.transform, "PlayPause", OnPlayPauseClicked);
        playPauseIcon.sprite = isPlaying ? UIIcons.Pause() : UIIcons.Play();
        playPauseIcon.color = TextColor;
        CreateIconButton(rowObject.transform, "↺", OnRestartClicked, ButtonColor); // ↺
        speedText = CreateIconButton(rowObject.transform, SpeedLabel(), OnSpeedClicked, ButtonColor);
        speedText.fontSize = SpeedFontSize;
        loopText = CreateIconButton(rowObject.transform, "∞", OnLoopToggleClicked, ButtonColor); // ∞ (loop toggle; color indicates on/off)

        // Only present for a DB-backed session - an unsaved buffer ("Ver replay" before
        // Guardar/Descartar) has no session row to mark. Same star/heart language as the
        // session list and Favoritos; clicking preferida always (re-)assigns it - it's
        // exclusive per shot, there's no "unset" from here, same as the session list.
        // A scene recording has no star: approving it needs slicing it into per-shot
        // references first, which comes later. The heart works.
        if (currentSession != null)
        {
            if (!currentSession.IsScene)
            {
                preferidaIcon = CreateSpriteIconButton(rowObject.transform, "Preferida", OnPreferidaClicked);
                ApplyPreferidaVisual(currentSession.EsPreferida);
            }

            favoritaIcon = CreateSpriteIconButton(rowObject.transform, "Favorita", OnFavoritaClicked);
            ApplyFavoritaVisual(currentSession.EsFavorita);
        }

        // Shows/hides the recorded-camera monitor (on by default; color indicates on/off,
        // same as the loop toggle). Only present when the session has a camera track.
        if (HasCameraTrack)
            monitorToggleText = CreateIconButton(rowObject.transform, "▣", OnMonitorToggleClicked, ButtonActiveColor);

#if DIRECTOR_BUILD
        if (HasConversation)
        {
            conversationToggleText = CreateIconButton(rowObject.transform, "Conv.", OnConversationToggleClicked, ButtonActiveColor);
            conversationToggleText.fontSize = AudioToggleFontSize;
        }
        if (timedNotes.Count > 0)
        {
            notesToggleText = CreateIconButton(rowObject.transform, "Notas", OnNotesAudioToggleClicked, ButtonActiveColor);
            notesToggleText.fontSize = AudioToggleFontSize;
        }

        Image notesIcon = CreateSpriteIconButton(rowObject.transform, "Notas", OnNotesClicked);
        notesIcon.sprite = UIIcons.Microphone();
        notesIcon.color = TextColor;

        if (currentSession != null)
        {
            Image deleteIcon = CreateSpriteIconButton(rowObject.transform, "Borrar", OnDeleteClicked);
            deleteIcon.sprite = UIIcons.Trash();
            deleteIcon.color = TextColor;
        }
#endif

        // Only present when reviewing an in-memory buffer (no scene change to exit through).
        if (onManualClose != null)
            CreateIconButton(rowObject.transform, "✕", OnCloseClicked, ButtonColor);
    }

#if DIRECTOR_BUILD
    string NotesTakeGuid => currentSession != null ? currentSession.SessionGuid
        : RehearsalRecorder.Instance != null ? RehearsalRecorder.Instance.PendingTakeGuid : null;

    void OnNotesClicked()
    {
        if (VoiceNotesPanel.Instance == null)
            return;

        int scene = currentSession != null ? currentSession.SceneNumber : RehearsalRecorder.Instance != null ? RehearsalRecorder.Instance.PendingTakeScene : Bootstrap.CurrentSceneNumber;
        int shot = currentSession != null ? currentSession.ShotNumber : RehearsalRecorder.Instance != null ? RehearsalRecorder.Instance.PendingTakeShot : Bootstrap.CurrentShotNumber;
        VoiceNotesPanel.Instance.Show(NotesTakeGuid, scene, shot, SeekTo, null);
    }

    public void JumpTo(float time) => SeekTo(time);

    void OnConversationToggleClicked()
    {
        conversationAudible = !conversationAudible;
        SetToggleColor(conversationToggleText, conversationAudible);
    }

    void OnNotesAudioToggleClicked()
    {
        notesAudible = !notesAudible;
        SetToggleColor(notesToggleText, notesAudible);
    }

    void SetToggleColor(Text label, bool on)
    {
        Image image = label != null ? label.transform.parent.GetComponent<Image>() : null;
        if (image != null)
            image.color = on ? ButtonActiveColor : ButtonColor;
    }

    void EnsureAudioSources()
    {
        if (directorSource != null)
            return;

        var audioObject = new GameObject("ReplayAudio");
        audioObject.transform.SetParent(transform, false);
        directorSource = audioObject.AddComponent<AudioSource>();
        actorSource = audioObject.AddComponent<AudioSource>();
        noteSource = audioObject.AddComponent<AudioSource>();
        foreach (AudioSource source in new[] { directorSource, actorSource, noteSource })
        {
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }
    }

    // A saved take's from its files; an unsaved one's still in TakeAudioRecorder.
    void LoadAudio()
    {
        EnsureAudioSources();
        UnloadAudio();

        if (currentSession != null)
        {
            directorSource.clip = WavFile.Load(TakeAudioRecorder.TrackPath(currentSession.SessionGuid, TakeAudioRecorder.DirectorTrack));
            actorSource.clip = WavFile.Load(TakeAudioRecorder.TrackPath(currentSession.SessionGuid, TakeAudioRecorder.ActorTrack));
        }
        else if (TakeAudioRecorder.Instance != null)
        {
            directorSource.clip = TakeAudioRecorder.Instance.PendingClip(TakeAudioRecorder.DirectorTrack);
            actorSource.clip = TakeAudioRecorder.Instance.PendingClip(TakeAudioRecorder.ActorTrack);
        }

        LoadTimedNotes();
        Debug.Log($"[Replay] audio: conversation {(HasConversation ? "yes" : "no")}, {timedNotes.Count} notes at moments");
    }

    void LoadTimedNotes()
    {
        foreach (TimedNote note in timedNotes)
            Destroy(note.Clip);
        timedNotes.Clear();
        if (noteSource != null)
        {
            noteSource.Stop();
            noteSource.clip = null;
        }

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        string guid = NotesTakeGuid;
        if (database == null || string.IsNullOrEmpty(guid))
            return;

        foreach (VoiceNote note in database.GetVoiceNotesOfTake(guid))
        {
            if (!note.TakeTime.HasValue)
                continue;
            AudioClip clip = WavFile.Load(System.IO.Path.Combine(VoiceNoteRecorder.NotesFolder, note.Archivo));
            if (clip != null)
                timedNotes.Add(new TimedNote { Time = note.TakeTime.Value, Clip = clip });
        }
    }

    void UnloadAudio()
    {
        foreach (AudioSource source in new[] { directorSource, actorSource })
        {
            if (source == null)
                continue;
            source.Stop();
            if (source.clip != null)
                Destroy(source.clip);
            source.clip = null;
        }
        foreach (TimedNote note in timedNotes)
            Destroy(note.Clip);
        timedNotes.Clear();
        if (noteSource != null)
        {
            noteSource.Stop();
            noteSource.clip = null;
        }
    }

    // Every frame: each source plays from the replay's moment, or stays silent. A seek, a pause
    // or a speed change is caught by the same check.
    void SyncAudio()
    {
        if (directorSource == null)
            return;

        bool audible = isPlaying && samples != null && Mathf.Approximately(PlaybackSpeed, 1f)
                       && !(VoiceNoteRecorder.Instance != null && VoiceNoteRecorder.Instance.IsRecording);
        SyncSource(directorSource, audible && conversationAudible, playbackTime);
        SyncSource(actorSource, audible && conversationAudible, playbackTime);

        TimedNote current = null;
        if (audible && notesAudible)
        {
            foreach (TimedNote note in timedNotes)
            {
                if (playbackTime >= note.Time && playbackTime < note.Time + note.Clip.length)
                {
                    current = note;
                    break;
                }
            }
        }

        if (current == null)
        {
            if (noteSource.isPlaying)
                noteSource.Stop();
            return;
        }

        float offset = playbackTime - current.Time;
        if (noteSource.clip != current.Clip || !noteSource.isPlaying || Mathf.Abs(noteSource.time - offset) > AudioDriftSeconds)
        {
            noteSource.clip = current.Clip;
            noteSource.time = offset;
            noteSource.Play();
        }
    }

    static void SyncSource(AudioSource source, bool on, float time)
    {
        if (source.clip == null)
            return;

        if (!on || time >= source.clip.length)
        {
            if (source.isPlaying)
                source.Pause();
            return;
        }

        if (!source.isPlaying || Mathf.Abs(source.time - time) > AudioDriftSeconds)
        {
            source.time = Mathf.Clamp(time, 0f, source.clip.length - 0.01f);
            source.Play();
        }
    }

    void SeekTo(float time)
    {
        SetPlaying(false);
        playbackTime = Mathf.Clamp(time, 0f, totalDuration);
        ApplySampleAt(playbackTime);
        UpdateSliderAndTimeText();
    }

    void OnDeleteClicked()
    {
        if (currentSession == null || VoiceNotesPanel.Instance == null)
            return;

        RecordingSession session = currentSession;
        VoiceNotesPanel.Instance.Confirm("¿Borrar esta toma y sus notas? No se puede deshacer.", () =>
        {
            ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
            if (database == null)
            {
                Debug.LogError("RecordingPlaybackController: no ShotListDatabase instance found, nothing deleted.");
                return;
            }

            database.DeleteSession(session);
            FloatingMessage.Show("Toma borrada");
            Bootstrap.LoadSceneSelect();
        });
    }

    // A mark on the slider at each moment the director said something.
    public void RefreshNoteTicks()
    {
        foreach (GameObject tick in noteTicks)
        {
            if (tick != null)
                Destroy(tick);
        }
        noteTicks.Clear();

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        string guid = NotesTakeGuid;
        if (scrubSlider == null || database == null || string.IsNullOrEmpty(guid) || totalDuration <= 0f)
            return;

        // A note said just now during this replay should also be heard in it.
        LoadTimedNotes();

        Transform handleArea = scrubSlider.handleRect.parent; // inset like the handle, so the marks line up
        foreach (VoiceNote note in database.GetVoiceNotesOfTake(guid))
        {
            if (!note.TakeTime.HasValue)
                continue;

            var tick = new GameObject("NoteTick", typeof(RectTransform), typeof(Image));
            tick.transform.SetParent(handleArea, false);
            tick.transform.SetAsFirstSibling();
            tick.layer = handleArea.gameObject.layer;

            float x = Mathf.Clamp01(note.TakeTime.Value / totalDuration);
            var rect = (RectTransform)tick.transform;
            rect.anchorMin = new Vector2(x, 0f);
            rect.anchorMax = new Vector2(x, 1f);
            rect.sizeDelta = new Vector2(NoteTickWidth, 0f);
            rect.anchoredPosition = Vector2.zero;

            Image image = tick.GetComponent<Image>();
            image.color = NoteTickColor;
            image.raycastTarget = false;
            noteTicks.Add(tick);
        }
    }
#endif

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

        ApplyPreferidaVisual(true);
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

        ApplyFavoritaVisual(newValue);
    }

    void ApplyPreferidaVisual(bool isPreferida)
    {
        if (preferidaIcon == null)
            return;

        preferidaIcon.sprite = UIIcons.Star(isPreferida);
        preferidaIcon.color = isPreferida ? PreferidaOnColor : PreferidaOffColor;
    }

    void ApplyFavoritaVisual(bool isFavorita)
    {
        if (favoritaIcon == null)
            return;

        favoritaIcon.sprite = UIIcons.Heart(isFavorita);
        favoritaIcon.color = isFavorita ? FavoritaOnColor : FavoritaOffColor;
    }

    GameObject CreateButtonObject(Transform parent, string name, UnityEngine.Events.UnityAction onClick, Color color)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        RectTransform buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.sizeDelta = new Vector2(ButtonSize, ButtonSize);
        buttonObject.GetComponent<Image>().color = color;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);
        return buttonObject;
    }

    // The sprite and its color are set by the caller.
    Image CreateSpriteIconButton(Transform parent, string name, UnityEngine.Events.UnityAction onClick)
    {
        GameObject buttonObject = CreateButtonObject(parent, $"Button_{name}", onClick, ButtonColor);
        return UIIcons.CreateIcon(buttonObject.transform, null, Color.white);
    }

    Text CreateIconButton(Transform parent, string symbol, UnityEngine.Events.UnityAction onClick, Color color)
    {
        GameObject buttonObject = CreateButtonObject(parent, $"Button_{symbol}", onClick, color);

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
        // Inset by half a handle so the handle stays inside the bar at 0% and 100%.
        handleAreaRect.offsetMin = new Vector2(SliderHandleSize / 2f, 0f);
        handleAreaRect.offsetMax = new Vector2(-SliderHandleSize / 2f, 0f);

        // The Slider stretches the handle vertically to the slider's own height
        // (SliderHandleSize), so sizeDelta.y stays 0 - a non-zero y is added on top of that
        // stretch, which is what made the old square handle twice as tall as intended.
        var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(handleArea.transform, false);
        RectTransform handleRect = (RectTransform)handle.transform;
        handleRect.sizeDelta = new Vector2(SliderHandleSize, 0f);
        Image handleImage = handle.GetComponent<Image>();
        handleImage.sprite = UIIcons.Circle();
        handleImage.preserveAspect = true;
        handleImage.color = SliderHandleColor;

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

    // After BuildSlider. The ticks go on the slider itself, above the bar and the fill but
    // under the handle; the labels go in their own row under it. Both use the handle's inset,
    // so a tick at time t sits exactly where the handle is at t. None of it takes raycasts, so
    // dragging the slider works the same over a tick.
    void BuildShotTicks(RectTransform parent)
    {
        RectTransform sliderRect = (RectTransform)scrubSlider.transform;
        RectTransform tickArea = CreateHandleInsetArea(sliderRect, "Shot Ticks");
        tickArea.SetSiblingIndex(2); // after Background and Fill Area, before the handle's area

        var labelRowObject = new GameObject("ShotTickLabels", typeof(RectTransform), typeof(LayoutElement));
        labelRowObject.transform.SetParent(parent, false);
        labelRowObject.GetComponent<LayoutElement>().preferredHeight = TickLabelRowHeight;
        RectTransform labelArea = CreateHandleInsetArea((RectTransform)labelRowObject.transform, "Labels");

        float duration = Mathf.Max(totalDuration, 0.01f);
        foreach (ShotBoundary boundary in boundaries)
        {
            float x = Mathf.Clamp01(boundary.TIn / duration);

            var tick = new GameObject($"Tick_{boundary.ShotNumber}", typeof(RectTransform), typeof(Image));
            tick.transform.SetParent(tickArea, false);
            RectTransform tickRect = (RectTransform)tick.transform;
            tickRect.anchorMin = new Vector2(x, 0f);
            tickRect.anchorMax = new Vector2(x, 1f);
            tickRect.sizeDelta = new Vector2(TickWidth, 0f);
            Image tickImage = tick.GetComponent<Image>();
            tickImage.color = TickColor;
            tickImage.raycastTarget = false;

            var label = new GameObject($"TickLabel_{boundary.ShotNumber}", typeof(RectTransform), typeof(Text));
            label.transform.SetParent(labelArea, false);
            RectTransform labelRect = (RectTransform)label.transform;
            labelRect.anchorMin = new Vector2(x, 0f);
            labelRect.anchorMax = new Vector2(x, 1f);
            labelRect.sizeDelta = new Vector2(TickLabelWidth, 0f);
            Text labelText = label.GetComponent<Text>();
            labelText.font = uiFont;
            labelText.text = $"T{boundary.ShotNumber}";
            labelText.fontSize = TickLabelFontSize;
            labelText.fontStyle = FontStyle.Bold;
            labelText.color = TickColor;
            labelText.alignment = TextAnchor.UpperCenter;
            // A legacy Text truncated to its rect draws nothing when the line is taller than
            // the row - let it overflow instead.
            labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
            labelText.verticalOverflow = VerticalWrapMode.Overflow;
            labelText.raycastTarget = false;
        }
    }

    static RectTransform CreateHandleInsetArea(RectTransform parent, string name)
    {
        var areaObject = new GameObject(name, typeof(RectTransform));
        areaObject.transform.SetParent(parent, false);
        RectTransform areaRect = (RectTransform)areaObject.transform;
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = new Vector2(SliderHandleSize / 2f, 0f);
        areaRect.offsetMax = new Vector2(-SliderHandleSize / 2f, 0f);
        return areaRect;
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

    string SpeedLabel() => PlaybackSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    void OnSpeedClicked()
    {
        speedIndex = (speedIndex + 1) % Speeds.Length;
        UpdateTimeText();
        if (speedText != null)
        {
            speedText.text = SpeedLabel();
            Image speedButtonImage = speedText.transform.parent.GetComponent<Image>();
            if (speedButtonImage != null)
                speedButtonImage.color = PlaybackSpeed == 1f ? ButtonColor : ButtonActiveColor;
        }
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
        isScrubbing = true;
        SetPlaying(false);
    }

    // Stays paused where the slider was dropped, even if it was playing before - the
    // director scrubs to look at a moment, and resumes explicitly with ▶ or left A.
    void OnScrubEnd()
    {
        isScrubbing = false;
        ScrubCount++;
    }

    void OnSliderValueChanged(float value)
    {
        if (!isScrubbing)
            return;

        playbackTime = value;
        ApplySampleAt(playbackTime);
        UpdateTimeText();
    }

    // The one place isPlaying changes, so the ▶/❚❚ button always matches the real state -
    // whether it was toggled from the button, left A, the slider, restart or reaching the end.
    void SetPlaying(bool playing)
    {
        isPlaying = playing;

        if (playPauseIcon != null)
            playPauseIcon.sprite = playing ? UIIcons.Pause() : UIIcons.Play();
    }

    IEnumerator PlaybackLoop()
    {
        while (true)
        {
            if (isPlaying && samples != null)
            {
                playbackTime += Time.deltaTime * PlaybackSpeed;

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

#if DIRECTOR_BUILD
            SyncAudio();
#endif
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
        {
            timeText.text = $"{playbackTime:F1}s / {totalDuration:F1}s";
#if DIRECTOR_BUILD
            if (HasAudio && !Mathf.Approximately(PlaybackSpeed, 1f))
                timeText.text += "  ·  audio solo a 1×";
#endif
        }
    }

    // The single place playback time turns into poses - called from the playback loop, the
    // slider and restart alike, so the ghost and the recorded camera always share one time.
    void ApplySampleAt(float time)
    {
        ApplyCameraSampleAt(time);

        if (samples == null || samples.Count == 0 || playbackAvatar == null)
            return;

        if (playbackAvatar.HasBodyData)
        {
            playbackAvatar.ShowFrameAt(time);
            return;
        }

        Metric first = samples[0];
        if (time <= first.TiempoRelativo)
        {
            ShowUnderHead(first);
            return;
        }

        Metric last = samples[samples.Count - 1];
        if (time >= last.TiempoRelativo)
        {
            ShowUnderHead(last);
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

            playbackAvatar.ShowUnderHead(position, rotation);
            return;
        }
    }

    void ShowUnderHead(Metric sample)
    {
        playbackAvatar.ShowUnderHead(
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
