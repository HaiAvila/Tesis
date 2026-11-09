using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Samples ActorPositionSync's
// synced transform into an in-memory buffer every 0.1s while recording. Started and
// stopped directly with the right controller's Secondary Button ("B") - confirmed unused
// anywhere else in this project's input actions, unlike the Primary Button ("A"), which
// is already bound to Jump. DIRECTOR_BUILD only, and only while in a content scene.
//
// Stopping does NOT save automatically - it shows a decision panel ("Ver replay" /
// "Guardar" / "Descartar") so the director can review the take (reusing
// RecordingPlaybackController on the still-in-memory buffer) before committing it to the
// DB or throwing it away.
public class RehearsalRecorder : MonoBehaviour
{
    const float SampleInterval = 0.1f;
    // The avatar on its own ticks: the pods arrive at about 19 Hz, and a quick gesture would be
    // lost at the head's 10 Hz.
    const float PoseSampleInterval = 0.05f;

    public static RehearsalRecorder Instance { get; private set; }

    public bool IsRecording { get; private set; }

    // Fired from the exact same start/stop calls that drive the recording itself, so a
    // UI indicator built from these can never drift out of sync with the real state.
    public event Action RecordingStarted;
    // A full-scene take moved on to this shot (Siguiente toma).
    public event Action<int> ShotOpened;
    public event Action RecordingStopped;

    readonly List<Metric> buffer = new List<Metric>();

    // Director viewfinder camera, one entry per actor sample at the same tiempo_relativo -
    // filled in lock-step with buffer by SampleOnce, saved/discarded together with it.
    readonly List<CameraTrackSample> cameraBuffer = new List<CameraTrackSample>();

    // The actor's avatar, whenever this machine has one to show - see AvatarPoseSync.
    readonly List<PoseSample> poseBuffer = new List<PoseSample>();
    byte[] avatarLayoutAtStart;

    Coroutine samplingCoroutine;
    Coroutine poseSamplingCoroutine;
    float recordingStartTime;
    DateTime recordingStartedAt;
    int shotNumberAtStart;
    int sceneNumberAtStart;
    bool fullSceneAtStart;
    // Sesiones.session_id - made at record start for every take, shot or scene.
    string sessionGuidAtStart;
    float pendingDuration;

    // Whether the take being recorded (or waiting for Guardar/Descartar) is a whole scene.
    public bool IsFullSceneTake => fullSceneAtStart;

    // Full-scene takes only: where each shot starts and ends (ShotBoundaries), kept in memory
    // with the other buffers and written only by Guardar, in the same transaction. The first
    // shot opens at 0 when recording starts; each Siguiente toma closes the current one and
    // opens the next; stopping closes the last. Recording without any Siguiente toma leaves
    // one boundary: the first shot, for the whole take.
    readonly List<ShotBoundary> boundaryBuffer = new List<ShotBoundary>();
    // When the set's action was triggered during the take (s from its start), so the replay
    // plays it back at the same moments. Saved with Guardar (EventosToma).
    readonly List<float> setActionBuffer = new List<float>();
    public IReadOnlyList<float> PendingSetActions => setActionBuffer;
    // The scene's shots in order, read at record start.
    readonly List<int> sceneShotsAtStart = new List<int>();

    public IReadOnlyList<ShotBoundary> PendingBoundaries => boundaryBuffer;

#if DIRECTOR_BUILD
    const float DecisionPanelDistance = 1.5f;
    const float DecisionPanelWorldScale = 0.0025f;
    const int DecisionButtonFontSize = 28;
    const float DecisionButtonWidth = 320f;
    const float DecisionButtonHeight = 90f;
    const float DecisionButtonSpacing = 30f;
    const float DecisionPanelPadding = 40f;
    const int DecisionCheckboxFontSize = 22;
    const float DecisionCheckboxHeight = 50f;
    const float DecisionCheckboxSpacing = 20f;
    const float DecisionRowSpacing = 16f;

    static readonly Color DecisionBackgroundColor = new Color(0.1f, 0.1f, 0.14f, 0.85f);
    static readonly Color DecisionButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.9f);
    static readonly Color DecisionCheckboxOffColor = new Color(0.2f, 0.2f, 0.24f, 0.85f);
    static readonly Color DecisionCheckboxOnColor = new Color(0.25f, 0.55f, 0.3f, 0.85f);
    static readonly Color DecisionTextColor = new Color(0.95f, 0.95f, 0.98f, 1f);

    InputAction recordingShortcutAction;
    InputAction nextShotAction;
    Font uiFont;
    GameObject decisionPanelObject;
    bool markAsPreferida;
    bool markAsFavorita;
    Image preferidaCheckboxImage;
    Image favoritaCheckboxImage;

    // The Ver replay / Guardar / Descartar panel shown after stopping (read by the tutorial).
    public bool IsDecisionPanelOpen => decisionPanelObject != null;

    // A take being recorded, or recorded and neither saved nor discarded yet.
    public bool HasPendingTake => IsRecording || buffer.Count > 0;
    public string PendingTakeGuid => sessionGuidAtStart;
    public int PendingTakeScene => sceneNumberAtStart;
    public int PendingTakeShot => shotNumberAtStart;
    public float RecordingTime => Time.time - recordingStartTime;

    // Runs once the pending take is saved or discarded - see FinishAfterConnectionLoss.
    // With the saved session's id, or null after Descartar.
    Action<int?> afterDecision;
    bool keptAfterConnectionLoss;
    const float DecisionTitleHeight = 50f;
    static readonly Color DecisionHighlightColor = new Color(0.2f, 0.45f, 0.75f, 0.95f);
#endif

    void Awake()
    {
        Instance = this;
        SetAction.Triggered += OnSetActionTriggered;

#if DIRECTOR_BUILD
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Same standard OpenXR path convention as PauseMenuController's Menu binding,
        // plus the XR Device Simulator's equivalent control for in-Editor testing.
        recordingShortcutAction = new InputAction("RecordingShortcut", InputActionType.Button);
        recordingShortcutAction.AddBinding("<XRController>{RightHand}/{secondaryButton}");
        recordingShortcutAction.AddBinding("<XRSimulatedController>{RightHand}/secondaryButton");
        recordingShortcutAction.performed += OnRecordingShortcutPressed;
        recordingShortcutAction.Enable();

        // Siguiente toma: the left controller's Y (secondary) - unbound everywhere else for
        // the director (left X is replay play/pause, right A the rig's Jump, right B record).
        // Simulator: T + N.
        nextShotAction = new InputAction("SiguienteToma", InputActionType.Button);
        nextShotAction.AddBinding("<XRController>{LeftHand}/{secondaryButton}");
        nextShotAction.AddBinding("<XRSimulatedController>{LeftHand}/secondaryButton");
        nextShotAction.performed += OnNextShotPressed;
        nextShotAction.Enable();
#endif
    }

    void OnSetActionTriggered()
    {
        if (IsRecording)
            setActionBuffer.Add(Time.time - recordingStartTime);
    }

    void OnDestroy()
    {
        SetAction.Triggered -= OnSetActionTriggered;
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        recordingShortcutAction.performed -= OnRecordingShortcutPressed;
        recordingShortcutAction.Disable();
        recordingShortcutAction.Dispose();

        nextShotAction.performed -= OnNextShotPressed;
        nextShotAction.Disable();
        nextShotAction.Dispose();
#endif
    }

#if DIRECTOR_BUILD
    // The connection dropped mid-rehearsal: the take is local, so it's kept. Stops recording if it
    // still was, and leaves the director to decide on it; `done` runs after that. False if
    // there's no pending take.
    public bool FinishAfterConnectionLoss(Action<int?> done)
    {
        if (!HasPendingTake)
            return false;

        // Reconnection attempts can fail again before the decision: once is enough.
        if (afterDecision != null)
            return true;

        afterDecision = done;
        keptAfterConnectionLoss = true;
        Debug.Log($"[Connection] connection lost with a take pending ({buffer.Count} samples): keeping it");
        if (IsRecording)
            StopRecording();
        else if (IsDecisionPanelOpen)
            ShowDecisionPanel(); // again, with the notice
        return true;
    }

    // A countdown before Acción, for an actor who is also their own director: press B, put the
    // headset on, and recording - t = 0 of the take, so the cues and the set's action line up -
    // starts when it ends. A beep each second, higher for the last three and for Acción. B
    // again cancels it. 0 = record right away.
    [Tooltip("Seconds between pressing B and Acción (recording starts then). 0 = right away.")]
    public float countdownSeconds = 10f;
    Coroutine countdown;
    AudioSource beepSource;
    AudioClip lowBeep, highBeep, actionBeep;

    System.Collections.IEnumerator CountdownThenRecord()
    {
        string scene = SceneManager.GetActiveScene().name;
        Debug.Log($"[Recorder] countdown {countdownSeconds:F0} s before Acción");
        for (int left = Mathf.CeilToInt(countdownSeconds); left > 0; left--)
        {
            Beep(left <= 3 ? highBeep : lowBeep);
            FloatingMessage.Show($"Acción en {left}");
            yield return new WaitForSeconds(1f);
            if (SceneManager.GetActiveScene().name != scene || Bootstrap.Mode != Bootstrap.AppMode.Remote)
            {
                CancelCountdown("left the take");
                yield break;
            }
        }
        countdown = null;
        Beep(actionBeep);
        FloatingMessage.Show("¡Acción!");
        StartRecording();
    }

    void CancelCountdown(string reason)
    {
        if (countdown != null)
            StopCoroutine(countdown);
        countdown = null;
        FloatingMessage.Show("Cuenta regresiva cancelada");
        Debug.Log($"[Recorder] countdown cancelled ({reason})");
    }

    void Beep(AudioClip clip)
    {
        if (beepSource == null)
        {
            beepSource = gameObject.AddComponent<AudioSource>();
            beepSource.playOnAwake = false;
            beepSource.spatialBlend = 0f;
            lowBeep = Tone("BeepLow", 660f, 0.12f);
            highBeep = Tone("BeepHigh", 990f, 0.12f);
            actionBeep = Tone("BeepAction", 1320f, 0.45f);
        }
        beepSource.PlayOneShot(clip, 0.8f);
    }

    static AudioClip Tone(string name, float hz, float seconds)
    {
        const int rate = 44100;
        int n = Mathf.RoundToInt(rate * seconds);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float envelope = Mathf.Min(1f, i / (rate * 0.005f)) * Mathf.Min(1f, (n - i) / (rate * 0.02f));
            data[i] = 0.5f * envelope * Mathf.Sin(2f * Mathf.PI * hz * i / rate);
        }
        AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    void RunAfterDecision(int? savedSessionId)
    {
        Action<int?> done = afterDecision;
        afterDecision = null;
        keptAfterConnectionLoss = false;
        done?.Invoke(savedSessionId);
    }

    void OnRecordingShortcutPressed(InputAction.CallbackContext context)
    {
        // Ensayo Remoto only - Inspección de Escenas has no live actor to record.
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) || Bootstrap.Mode != Bootstrap.AppMode.Remote)
            return;

        if (IsRecording)
            StopRecording();
        else if (countdown != null)
            CancelCountdown("B pressed again");
        else if (countdownSeconds > 0f)
            countdown = StartCoroutine(CountdownThenRecord());
        else
            StartRecording();

        SendHaptic();
    }

    void OnNextShotPressed(InputAction.CallbackContext context)
    {
        if (PanelFocusManager.IsModalOpen)
            return;

        NextShot();
    }

    void SendHaptic(string controllerName = "Right Controller")
    {
        GameObject controller = GameObject.Find(controllerName);
        if (controller == null)
            return;

        HapticImpulsePlayer hapticPlayer = controller.GetComponent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
#endif

    // Siguiente toma - only while recording a full scene, and optional: closes the current
    // shot's boundary, opens the next shot's, and sends SessionCommand.SiguienteToma to
    // everyone through the command channel. Returns whether it moved on.
    public bool NextShot()
    {
        if (!IsRecording || !fullSceneAtStart)
        {
            Debug.Log($"[Escena] Siguiente toma ignored: {(IsRecording ? "not a full-scene take" : "not recording")}");
            return false;
        }

        ShotBoundary current = boundaryBuffer.Count > 0 ? boundaryBuffer[boundaryBuffer.Count - 1] : null;
        int index = current != null ? sceneShotsAtStart.IndexOf(current.ShotNumber) : -1;
        if (current == null || index < 0 || index + 1 >= sceneShotsAtStart.Count)
        {
            Debug.Log($"[Escena] Siguiente toma ignored: shot {(current != null ? current.ShotNumber : -1)} is the last of scene {sceneNumberAtStart}");
            FloatingMessage.Show("Última toma de la escena");
            return false;
        }

        float time = Time.time - recordingStartTime;
        current.TOut = time;
        int nextShot = sceneShotsAtStart[index + 1];
        OpenBoundary(nextShot, time);
        ShotOpened?.Invoke(nextShot);

        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SendCommand(SessionCommand.SiguienteToma);

        Debug.Log($"[Escena] Siguiente toma at {time:F1}s: shot {current.ShotNumber} closed, shot {nextShot} open ({boundaryBuffer.Count} boundaries)");
        FloatingMessage.Show($"Siguiente toma: shot {nextShot}");
#if DIRECTOR_BUILD
        SendHaptic("Left Controller");
#endif
        return true;
    }

    void OpenBoundary(int shotNumber, float time)
    {
        boundaryBuffer.Add(new ShotBoundary
        {
            ShotNumber = shotNumber,
            TIn = time,
            TOut = null,
            Source = ShotBoundary.SourceManual
        });
    }

    // Automatic shot-boundary suggestions - NOT IMPLEMENTED, an empty hook on purpose. Later
    // this is where boundaries get suggested from the actor's headset path (Metricas) or the
    // pods, returned as ShotBoundary rows with Source = ShotBoundary.SourceAuto; they're saved
    // next to the director's manual ones. Called when a full-scene take stops.
    List<ShotBoundary> SuggestShotBoundariesAuto(IReadOnlyList<int> sceneShots, IReadOnlyList<Metric> headsetPath, float duration)
    {
        return new List<ShotBoundary>();
    }

    public void StartRecording()
    {
        if (IsRecording)
            return;

        buffer.Clear();
        cameraBuffer.Clear();
        poseBuffer.Clear();
        setActionBuffer.Clear();
        avatarLayoutAtStart = null;
        shotNumberAtStart = Bootstrap.CurrentShotNumber;
        sceneNumberAtStart = Bootstrap.CurrentSceneNumber;
        fullSceneAtStart = Bootstrap.IsFullScene;
        sessionGuidAtStart = Guid.NewGuid().ToString();

        boundaryBuffer.Clear();
        sceneShotsAtStart.Clear();
        if (fullSceneAtStart)
        {
            ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
            if (database != null)
                sceneShotsAtStart.AddRange(database.GetShotNumbersOfScene(sceneNumberAtStart));
            if (sceneShotsAtStart.Count > 0)
                OpenBoundary(sceneShotsAtStart[0], 0f);
            Debug.Log($"[Escena] recording the full scene {sceneNumberAtStart} (session {sessionGuidAtStart}, shots {string.Join(",", sceneShotsAtStart)}), shot {(sceneShotsAtStart.Count > 0 ? sceneShotsAtStart[0] : -1)} open");
        }

        recordingStartedAt = DateTime.Now;
        recordingStartTime = Time.time;
        IsRecording = true;
        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SetRecording(true);
        samplingCoroutine = StartCoroutine(SampleLoop());
        poseSamplingCoroutine = StartCoroutine(PoseSampleLoop());

        RecordingStarted?.Invoke();
    }

    public void StopRecording()
    {
        if (!IsRecording)
            return;

        IsRecording = false;
        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SetRecording(false);
        if (samplingCoroutine != null)
            StopCoroutine(samplingCoroutine);
        if (poseSamplingCoroutine != null)
            StopCoroutine(poseSamplingCoroutine);

        RecordingStopped?.Invoke();

        pendingDuration = Time.time - recordingStartTime;

        if (fullSceneAtStart)
        {
            // Close the last open boundary at the end of the take.
            if (boundaryBuffer.Count > 0 && boundaryBuffer[boundaryBuffer.Count - 1].TOut == null)
                boundaryBuffer[boundaryBuffer.Count - 1].TOut = pendingDuration;

            boundaryBuffer.AddRange(SuggestShotBoundariesAuto(sceneShotsAtStart, buffer, pendingDuration));

            string list = string.Join(", ", boundaryBuffer.ConvertAll(b => $"shot {b.ShotNumber} {b.TIn:F1}-{b.TOut:F1}s ({b.Source})"));
            Debug.Log($"[Escena] stopped after {pendingDuration:F1}s with {boundaryBuffer.Count} boundaries: {list}");
        }

#if DIRECTOR_BUILD
        // Fresh choice for a fresh recording - reset here (once, per real stop), not inside
        // ShowDecisionPanel itself, since that's also re-invoked as the "Ver replay" close
        // callback and shouldn't wipe out a checkbox the director already ticked.
        markAsPreferida = false;
        markAsFavorita = false;
        ShowDecisionPanel();
#endif
    }

#if DIRECTOR_BUILD
    void ShowDecisionPanel()
    {
        PanelFocusManager.CloseModal(decisionPanelObject);
        if (decisionPanelObject != null)
            Destroy(decisionPanelObject);

        Camera headCamera = Camera.main;
        if (headCamera == null)
        {
            Debug.LogWarning("RehearsalRecorder: no Camera.main found, cannot show the save/discard decision panel.");
            return;
        }

        Transform head = headCamera.transform;
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 panelPosition = head.position + forward * DecisionPanelDistance;
        Quaternion panelRotation = Quaternion.LookRotation(forward, Vector3.up);

        decisionPanelObject = new GameObject("RecordingDecisionPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = decisionPanelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        float panelWidth = DecisionButtonWidth * 3f + DecisionButtonSpacing * 2f + DecisionPanelPadding * 2f;
        float panelHeight = DecisionCheckboxHeight + DecisionRowSpacing + DecisionButtonHeight + DecisionPanelPadding * 2f;
        if (keptAfterConnectionLoss)
            panelHeight += DecisionTitleHeight + DecisionRowSpacing;

        RectTransform canvasRect = (RectTransform)decisionPanelObject.transform;
        canvasRect.sizeDelta = new Vector2(panelWidth, panelHeight);
        canvasRect.position = panelPosition;
        canvasRect.rotation = panelRotation;
        canvasRect.localScale = Vector3.one * DecisionPanelWorldScale;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasRect, false);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        GlassUI.SkinPanel(background.GetComponent<Image>(), 34f);

        var rootLayoutObject = new GameObject("Root", typeof(RectTransform), typeof(VerticalLayoutGroup));
        rootLayoutObject.transform.SetParent(canvasRect, false);
        RectTransform rootRect = (RectTransform)rootLayoutObject.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = new Vector2(DecisionPanelPadding, DecisionPanelPadding);
        rootRect.offsetMax = new Vector2(-DecisionPanelPadding, -DecisionPanelPadding);

        VerticalLayoutGroup rootLayout = rootLayoutObject.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = DecisionRowSpacing;
        rootLayout.childAlignment = TextAnchor.MiddleCenter;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        // After a connection loss: say the take is safe, and point at Ver replay.
        if (keptAfterConnectionLoss)
        {
            var titleObject = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
            titleObject.transform.SetParent(rootRect, false);
            titleObject.GetComponent<LayoutElement>().preferredHeight = DecisionTitleHeight;
            TMP_Text title = titleObject.GetComponent<TextMeshProUGUI>();
            title.font = GlassUI.Regular;
            title.text = "Se perdió la conexión: la toma está a salvo";
            title.fontSize = DecisionButtonFontSize;
            title.fontStyle = FontStyles.Bold;
            title.color = DecisionTextColor;
            title.alignment = TextAlignmentOptions.Center;
        }

        var checkboxRowObject = new GameObject("Checkboxes", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        checkboxRowObject.transform.SetParent(rootRect, false);
        checkboxRowObject.GetComponent<LayoutElement>().preferredHeight = DecisionCheckboxHeight;

        HorizontalLayoutGroup checkboxLayout = checkboxRowObject.GetComponent<HorizontalLayoutGroup>();
        checkboxLayout.spacing = DecisionCheckboxSpacing;
        checkboxLayout.childAlignment = TextAnchor.MiddleCenter;
        checkboxLayout.childControlWidth = true;
        checkboxLayout.childControlHeight = true;
        checkboxLayout.childForceExpandWidth = true;
        checkboxLayout.childForceExpandHeight = true;

        // A scene take can't be preferida yet (approving it needs slicing it into per-shot
        // references first) - same as the star, which is hidden for scene recordings.
        preferidaCheckboxImage = fullSceneAtStart
            ? null
            : CreateCheckbox(checkboxRowObject.transform, "Marcar como preferida", markAsPreferida, OnPreferidaCheckboxClicked);
        favoritaCheckboxImage = CreateCheckbox(checkboxRowObject.transform, "Marcar como favorita", markAsFavorita, OnFavoritaCheckboxClicked);

        var buttonRowObject = new GameObject("Options", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        buttonRowObject.transform.SetParent(rootRect, false);
        buttonRowObject.GetComponent<LayoutElement>().preferredHeight = DecisionButtonHeight;

        HorizontalLayoutGroup buttonLayout = buttonRowObject.GetComponent<HorizontalLayoutGroup>();
        buttonLayout.spacing = DecisionButtonSpacing;
        buttonLayout.childAlignment = TextAnchor.MiddleCenter;
        buttonLayout.childControlWidth = true;
        buttonLayout.childControlHeight = true;
        buttonLayout.childForceExpandWidth = true;
        buttonLayout.childForceExpandHeight = true;

        CreateDecisionButton(buttonRowObject.transform, "Ver replay", OnViewReplayClicked, keptAfterConnectionLoss ? DecisionHighlightColor : (Color?)null);
        CreateDecisionButton(buttonRowObject.transform, "Guardar", OnSaveClicked);
        CreateDecisionButton(buttonRowObject.transform, "Descartar", OnDiscardClicked);

        XRUISetup.MoveToUILayer(decisionPanelObject);

        XRUISetup.EnableRayInteraction(decisionPanelObject);
        XRUISetup.EnsureEventSystem();

        PanelFocusManager.OpenModal(decisionPanelObject, "Fin de grabación");
    }

    // Simple checkbox: a plain button whose background color flips between on/off, with a
    // ✓/☐ prefix baked into the label - matches this project's established convention of
    // lightweight button-based toggles (e.g. RecordingPlaybackController's loop button)
    // rather than introducing Unity's Toggle component for the first time.
    Image CreateCheckbox(Transform parent, string label, bool initialValue, UnityEngine.Events.UnityAction onClick)
    {
        var checkboxObject = new GameObject($"Checkbox_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
        checkboxObject.transform.SetParent(parent, false);
        Image image = checkboxObject.GetComponent<Image>();
        checkboxObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(checkboxObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = GlassUI.Regular;
        text.text = label;
        text.fontSize = DecisionCheckboxFontSize;
        text.color = DecisionTextColor;
        text.alignment = TextAlignmentOptions.Center;
        textRect.offsetMin = new Vector2(44f, 0f);

        // A toggle chip: its icon, and cyan while on.
        TMP_Text icon = GlassUI.IconLabel(checkboxObject.transform, label.Contains("preferida") ? GlassUI.Icon.Star : GlassUI.Icon.Favorite, DecisionCheckboxFontSize * 1.2f);
        var iconRect = (RectTransform)icon.transform;
        iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = new Vector2(24f, 0f);
        iconRect.sizeDelta = new Vector2(40f, 40f);
        GlassUI.SkinButton(checkboxObject, DecisionCheckboxHeight / 2f).Active = initialValue;

        return image;
    }

    void OnPreferidaCheckboxClicked()
    {
        markAsPreferida = !markAsPreferida;
        UpdateCheckboxVisual(preferidaCheckboxImage, "Marcar como preferida", markAsPreferida);
    }

    void OnFavoritaCheckboxClicked()
    {
        markAsFavorita = !markAsFavorita;
        UpdateCheckboxVisual(favoritaCheckboxImage, "Marcar como favorita", markAsFavorita);
    }

    void UpdateCheckboxVisual(Image checkboxImage, string label, bool value)
    {
        if (checkboxImage == null)
            return;

        RoundButton toggle = checkboxImage.GetComponent<RoundButton>();
        if (toggle != null)
            toggle.Active = value;
    }

    void CreateDecisionButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, Color? color = null)
    {
        var buttonObject = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        GlassUI.ButtonStyle style = color.HasValue ? GlassUI.ButtonStyle.Primary : label == "Descartar" ? GlassUI.ButtonStyle.Danger : GlassUI.ButtonStyle.Normal;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = GlassUI.Regular;
        text.text = label;
        text.fontSize = DecisionButtonFontSize;
        text.fontStyle = FontStyles.Bold;
        text.color = DecisionTextColor;
        text.alignment = TextAlignmentOptions.Center;
        textRect.offsetMin = new Vector2(40f, 0f);

        string glyph = label == "Ver replay" ? GlassUI.Icon.PlayCircle : label == "Guardar" ? GlassUI.Icon.Save : GlassUI.Icon.Delete;
        TMP_Text icon = GlassUI.IconLabel(buttonObject.transform, glyph, DecisionButtonFontSize * 1.25f);
        var iconRect = (RectTransform)icon.transform;
        iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = new Vector2(28f, 0f);
        iconRect.sizeDelta = new Vector2(44f, 44f);
        GlassUI.SkinButton(buttonObject, DecisionButtonHeight / 2f, style);
    }

    void DestroyDecisionPanel()
    {
        PanelFocusManager.CloseModal(decisionPanelObject);
        if (decisionPanelObject != null)
            Destroy(decisionPanelObject);
        decisionPanelObject = null;
    }

    void OnViewReplayClicked()
    {
        DestroyDecisionPanel();
        SendHaptic();

        if (RecordingPlaybackController.Instance == null)
        {
            Debug.LogError("RehearsalRecorder: no RecordingPlaybackController instance found, cannot show replay.");
            ShowDecisionPanel();
            return;
        }

        // Same panel/capsule/free-fly system as Inspección de Escenas, just fed directly
        // from the buffer instead of a DB query - nothing saved yet. Reappears when closed.
        RecordingPlaybackController.Instance.StartPlaybackFromBuffer(buffer, ShowDecisionPanel, fullSceneAtStart ? boundaryBuffer : null, poseBuffer, avatarLayoutAtStart);
    }

    void OnSaveClicked()
    {
        DestroyDecisionPanel();
        SendHaptic();

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        int? savedSessionId = null;
        if (database == null)
            Debug.LogError("RehearsalRecorder: no ShotListDatabase instance found, recording discarded.");
        else
            savedSessionId = database.SaveRecordingSession(sceneNumberAtStart, shotNumberAtStart,
                fullSceneAtStart ? RecordingSession.TipoEscena : RecordingSession.TipoToma, sessionGuidAtStart,
                recordingStartedAt, pendingDuration, buffer, cameraBuffer,
                fullSceneAtStart ? boundaryBuffer : null, markAsPreferida, markAsFavorita, poseBuffer, avatarLayoutAtStart);

        if (TakeAudioRecorder.Instance != null)
            TakeAudioRecorder.Instance.Save(sessionGuidAtStart);
        if (database != null && setActionBuffer.Count > 0)
            database.SaveSetActions(sessionGuidAtStart, setActionBuffer);
        setActionBuffer.Clear();

        buffer.Clear();
        cameraBuffer.Clear();
        poseBuffer.Clear();
        boundaryBuffer.Clear();
        RunAfterDecision(savedSessionId);
    }

    void OnDiscardClicked()
    {
        DestroyDecisionPanel();
        SendHaptic();
        setActionBuffer.Clear();
        buffer.Clear();
        cameraBuffer.Clear();
        poseBuffer.Clear();

        // The boundaries go with the session: dropped from memory and, in case anything wrote
        // some already, from the DB too.
        if (fullSceneAtStart)
        {
            boundaryBuffer.Clear();
            ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
            if (database != null)
                database.DeleteBoundaries(sessionGuidAtStart);
        }

        // The take is gone, but what the director said about it is kept, as notes of the shot.
        ShotListDatabase notes = FindFirstObjectByType<ShotListDatabase>();
        if (notes != null)
            notes.DetachVoiceNotes(sessionGuidAtStart);
        if (TakeAudioRecorder.Instance != null)
            TakeAudioRecorder.Instance.Discard();

        RunAfterDecision(null);
    }
#endif

    IEnumerator SampleLoop()
    {
        var wait = new WaitForSeconds(SampleInterval);
        while (true)
        {
            SampleOnce();
            yield return wait;
        }
    }

    IEnumerator PoseSampleLoop()
    {
        var wait = new WaitForSeconds(PoseSampleInterval);
        while (true)
        {
            AvatarPoseSync avatar = AvatarPoseSync.Instance;
            if (avatar != null && avatar.TryCaptureFrame(out byte[] frame))
            {
                poseBuffer.Add(new PoseSample { TiempoRelativo = Time.time - recordingStartTime, Datos = frame });
                if (avatarLayoutAtStart == null)
                    avatarLayoutAtStart = avatar.CaptureLayout();
            }
            yield return wait;
        }
    }

    void SampleOnce()
    {
        if (ActorPositionSync.Instance == null)
            return;

#if DIRECTOR_BUILD
        // Actor and camera are sampled as a pair or not at all, so Metricas and CamaraTrack
        // always end up with the same row count and identical timestamps. The viewfinder
        // exists for the whole of Ensayo Remoto; it's only missing for the first frames
        // after a scene loads, or outside Ensayo Remoto (e.g. Inspección de Escenas, where
        // recording isn't meaningful anyway).
        DirectorViewfinderController viewfinder = DirectorViewfinderController.Instance;
        if (viewfinder == null
            || !viewfinder.TryGetCameraSample(out Vector3 cameraPosition, out Quaternion cameraRotation, out float focalLengthMM, out bool visorActive))
            return;
#endif

        Transform actorTransform = ActorPositionSync.Instance.transform;
        Vector3 position = actorTransform.position;
        Quaternion rotation = actorTransform.rotation;

        // Read once and shared by both rows, so the timestamps are exactly equal.
        float tiempoRelativo = Time.time - recordingStartTime;

#if DIRECTOR_BUILD
        cameraBuffer.Add(new CameraTrackSample
        {
            TiempoRelativo = tiempoRelativo,
            PosX = cameraPosition.x,
            PosY = cameraPosition.y,
            PosZ = cameraPosition.z,
            RotX = cameraRotation.x,
            RotY = cameraRotation.y,
            RotZ = cameraRotation.z,
            RotW = cameraRotation.w,
            FocalMM = focalLengthMM,
            VisorActivo = visorActive
        });
#endif

        buffer.Add(new Metric
        {
            TiempoRelativo = tiempoRelativo,
            PosX = position.x,
            PosY = position.y,
            PosZ = position.z,
            RotX = rotation.x,
            RotY = rotation.y,
            RotZ = rotation.z,
            RotW = rotation.w
        });
    }
}
