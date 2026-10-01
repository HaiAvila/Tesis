using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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

    public static RehearsalRecorder Instance { get; private set; }

    public bool IsRecording { get; private set; }

    // Fired from the exact same start/stop calls that drive the recording itself, so a
    // UI indicator built from these can never drift out of sync with the real state.
    public event Action RecordingStarted;
    public event Action RecordingStopped;

    readonly List<Metric> buffer = new List<Metric>();

    Coroutine samplingCoroutine;
    float recordingStartTime;
    DateTime recordingStartedAt;
    int shotNumberAtStart;
    float pendingDuration;

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
    Font uiFont;
    GameObject decisionPanelObject;
    bool markAsPreferida;
    bool markAsFavorita;
    Image preferidaCheckboxImage;
    Image favoritaCheckboxImage;
#endif

    void Awake()
    {
        Instance = this;

#if DIRECTOR_BUILD
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Same standard OpenXR path convention as PauseMenuController's Menu binding,
        // plus the XR Device Simulator's equivalent control for in-Editor testing.
        recordingShortcutAction = new InputAction("RecordingShortcut", InputActionType.Button);
        recordingShortcutAction.AddBinding("<XRController>{RightHand}/{secondaryButton}");
        recordingShortcutAction.AddBinding("<XRSimulatedController>{RightHand}/secondaryButton");
        recordingShortcutAction.performed += OnRecordingShortcutPressed;
        recordingShortcutAction.Enable();
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        recordingShortcutAction.performed -= OnRecordingShortcutPressed;
        recordingShortcutAction.Disable();
        recordingShortcutAction.Dispose();
#endif
    }

#if DIRECTOR_BUILD
    void OnRecordingShortcutPressed(InputAction.CallbackContext context)
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
            return;

        if (IsRecording)
            StopRecording();
        else
            StartRecording();

        SendHaptic();
    }

    void SendHaptic()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        if (rightController == null)
            return;

        HapticImpulsePlayer hapticPlayer = rightController.GetComponent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
#endif

    public void StartRecording()
    {
        if (IsRecording)
            return;

        buffer.Clear();
        shotNumberAtStart = Bootstrap.CurrentShotNumber;
        recordingStartedAt = DateTime.Now;
        recordingStartTime = Time.time;
        IsRecording = true;
        samplingCoroutine = StartCoroutine(SampleLoop());

        RecordingStarted?.Invoke();
    }

    public void StopRecording()
    {
        if (!IsRecording)
            return;

        IsRecording = false;
        if (samplingCoroutine != null)
            StopCoroutine(samplingCoroutine);

        RecordingStopped?.Invoke();

        pendingDuration = Time.time - recordingStartTime;

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
        background.GetComponent<Image>().color = DecisionBackgroundColor;

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

        preferidaCheckboxImage = CreateCheckbox(checkboxRowObject.transform, "Marcar como preferida", markAsPreferida, OnPreferidaCheckboxClicked);
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

        CreateDecisionButton(buttonRowObject.transform, "Ver replay", OnViewReplayClicked);
        CreateDecisionButton(buttonRowObject.transform, "Guardar", OnSaveClicked);
        CreateDecisionButton(buttonRowObject.transform, "Descartar", OnDiscardClicked);

        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
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
        image.color = initialValue ? DecisionCheckboxOnColor : DecisionCheckboxOffColor;
        checkboxObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(checkboxObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.text = (initialValue ? "☑ " : "☐ ") + label;
        text.fontSize = DecisionCheckboxFontSize;
        text.color = DecisionTextColor;
        text.alignment = TextAnchor.MiddleCenter;

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

        checkboxImage.color = value ? DecisionCheckboxOnColor : DecisionCheckboxOffColor;

        Text text = checkboxImage.GetComponentInChildren<Text>();
        if (text != null)
            text.text = (value ? "☑ " : "☐ ") + label;
    }

    void CreateDecisionButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject($"Button_{label}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = DecisionButtonColor;
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
        text.text = label;
        text.fontSize = DecisionButtonFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = DecisionTextColor;
        text.alignment = TextAnchor.MiddleCenter;
    }

    void DestroyDecisionPanel()
    {
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
        RecordingPlaybackController.Instance.StartPlaybackFromBuffer(buffer, ShowDecisionPanel);
    }

    void OnSaveClicked()
    {
        DestroyDecisionPanel();
        SendHaptic();

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
            Debug.LogError("RehearsalRecorder: no ShotListDatabase instance found, recording discarded.");
        else
            database.SaveRecordingSession(shotNumberAtStart, recordingStartedAt, pendingDuration, buffer, markAsPreferida, markAsFavorita);

        buffer.Clear();
    }

    void OnDiscardClicked()
    {
        DestroyDecisionPanel();
        SendHaptic();
        buffer.Clear();
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

    void SampleOnce()
    {
        if (ActorPositionSync.Instance == null)
            return;

        Transform actorTransform = ActorPositionSync.Instance.transform;
        Vector3 position = actorTransform.position;
        Quaternion rotation = actorTransform.rotation;

        // TEMPORARY diagnostic - remove once the empty position/rotation issue is confirmed fixed.
        Debug.Log($"RehearsalRecorder: sampled pos={position:F3} rot={rotation:F3}");

        buffer.Add(new Metric
        {
            TiempoRelativo = Time.time - recordingStartTime,
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
