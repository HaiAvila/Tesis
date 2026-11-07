using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

// Persistent (lives under Bootstrap, DontDestroyOnLoad) pause/navigation menu, only
// actionable in the 3 content scenes. Opened/closed with the left controller's Menu
// button - the standard OpenXR/XRI binding path, not a project-specific action asset.
public class PauseMenuController : MonoBehaviour
{
    const float PanelDistance = 1.5f;
    const float PanelWorldScale = 0.0025f;
    const float CompactPanelWorldScale = 0.002f;  // a long menu, so it still fits in view
    const int CompactOptionCount = 6;
    const int ButtonFontSize = 30;
    const float ButtonWidth = 420f;
    const float ButtonHeight = 90f;
    const float ButtonSpacing = 30f;
    const float PanelPadding = 80f;

    static readonly Color BackgroundColor = new Color(0.1f, 0.1f, 0.14f, 0.85f);
    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.9f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);

    class PauseMenuOption
    {
        public string Label;
        public Action OnSelected;
    }

    readonly List<GameObject> pausedLocomotionObjects = new List<GameObject>();

    public static PauseMenuController Instance { get; private set; }

    // Read by the tutorial's "open the pause menu" step.
    public bool IsOpen => isOpen;

    InputAction menuAction;
    GameObject panelObject;
    Font uiFont;
    bool isOpen;

    void Awake()
    {
        Instance = this;
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Standard OpenXR binding path for the left controller's Menu button (matches
        // Unity's own OpenXR Starter Assets sample), created directly rather than via a
        // project input action asset since this is the only place that needs it. A second
        // binding targets the XR Device Simulator's control (named "menuButton" there,
        // unlike real OpenXR devices which expose "menu") so this is testable in-Editor
        // without a headset.
        menuAction = new InputAction("PauseMenuToggle", InputActionType.Button);
        menuAction.AddBinding("<XRController>{LeftHand}/menu");
        menuAction.AddBinding("<XRSimulatedController>{LeftHand}/menuButton");
        menuAction.performed += OnMenuButtonPressed;
        menuAction.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        menuAction.performed -= OnMenuButtonPressed;
        menuAction.Disable();
        menuAction.Dispose();

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // The panel and whatever it paused belonged to the scene that just unloaded.
        panelObject = null;
        pausedLocomotionObjects.Clear();
        isOpen = false;
    }

    void OnMenuButtonPressed(InputAction.CallbackContext context)
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
            return;

        if (isOpen)
            Close();
        else
            Open();
    }

    void Open()
    {
        Camera headCamera = Camera.main;
        if (headCamera == null)
        {
            Debug.LogWarning("PauseMenuController: no Camera.main found, cannot open pause menu.");
            return;
        }

        PauseLocomotion();
        BuildPanel(headCamera.transform, BuildOptions());
        PanelFocusManager.OpenModal(panelObject, "Menú de pausa");
        SendHaptic();
        isOpen = true;
    }

    // Extensible, not hardcoded into the layout logic below - new entries are a one-line
    // add here. Rebuilt fresh on every open so any state-dependent label stays current.
    // Recording is started/stopped via its own controller shortcut (RehearsalRecorder),
    // not from this menu. Scene selection is a director-only action - the actor doesn't
    // choose shots, so that option is left out of their menu entirely.
    List<PauseMenuOption> BuildOptions()
    {
        var menuOptions = new List<PauseMenuOption>
        {
            new PauseMenuOption { Label = "Menú Principal", OnSelected = GoToMainMenu }
        };

#if DIRECTOR_BUILD
        menuOptions.Add(new PauseMenuOption { Label = "Selección de Escenas", OnSelected = GoToSceneSelect });
        menuOptions.Add(new PauseMenuOption { Label = "Favoritos", OnSelected = ShowFavorites });
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote)
            menuOptions.Add(new PauseMenuOption { Label = "Calibrar", OnSelected = StartCalibration });
        if (Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        {
            menuOptions.Add(new PauseMenuOption { Label = "Notas del shot", OnSelected = ShowShotNotes });
            menuOptions.Add(new PauseMenuOption { Label = "Exportar tomas a CSV", OnSelected = ExportTakes });
        }
#endif

        if (Bootstrap.Mode == Bootstrap.AppMode.Remote && VoiceChat.Instance != null)
            menuOptions.Add(new PauseMenuOption { Label = VoiceChat.Instance.MicrophoneOn ? "Micrófono: encendido" : "Micrófono: apagado", OnSelected = ToggleMicrophone });

        menuOptions.Add(new PauseMenuOption { Label = "Estado de pods", OnSelected = ShowPodStatus });

        // Replays the controls tutorial for the current mode - it's only offered
        // automatically the first time.
        menuOptions.Add(new PauseMenuOption { Label = "Tutorial", OnSelected = ShowTutorial });

        return menuOptions;
    }

    void Close()
    {
        ClosePanelAndResumeLocomotion();
        SendHaptic();
    }

    void ClosePanelAndResumeLocomotion()
    {
        PanelFocusManager.CloseModal(panelObject);
        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;

        ResumeLocomotion();
        isOpen = false;
    }

    void PauseLocomotion()
    {
        pausedLocomotionObjects.Clear();

        // Joystick-driven locomotion only ("Move" and "Turn"/snap-turn share the "Turn"
        // object) - head tracking and gravity live elsewhere and are never touched.
        GameObject move = GameObject.Find("Move");
        GameObject turn = GameObject.Find("Turn");

        foreach (GameObject locomotionObject in new[] { move, turn })
        {
            if (locomotionObject != null && locomotionObject.activeSelf)
            {
                locomotionObject.SetActive(false);
                pausedLocomotionObjects.Add(locomotionObject);
            }
        }
    }

    void ResumeLocomotion()
    {
        foreach (GameObject locomotionObject in pausedLocomotionObjects)
        {
            if (locomotionObject != null)
                locomotionObject.SetActive(true);
        }

        pausedLocomotionObjects.Clear();
    }

    void SendHaptic()
    {
        GameObject leftController = GameObject.Find("Left Controller");
        if (leftController == null)
            return;

        HapticImpulsePlayer hapticPlayer = leftController.GetComponent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }

    void BuildPanel(Transform head, List<PauseMenuOption> menuOptions)
    {
        Vector3 forward = head.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 panelPosition = head.position + forward * PanelDistance;
        Quaternion panelRotation = Quaternion.LookRotation(forward, Vector3.up);

        panelObject = new GameObject("PauseMenuPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        float panelHeight = menuOptions.Count * ButtonHeight + (menuOptions.Count - 1) * ButtonSpacing + PanelPadding * 2;
        float panelWidth = ButtonWidth + PanelPadding * 2;

        RectTransform canvasRect = (RectTransform)panelObject.transform;
        canvasRect.sizeDelta = new Vector2(panelWidth, panelHeight);
        canvasRect.position = panelPosition;
        canvasRect.rotation = panelRotation;
        canvasRect.localScale = Vector3.one * (menuOptions.Count >= CompactOptionCount ? CompactPanelWorldScale : PanelWorldScale);

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasRect, false);
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;
        background.GetComponent<Image>().color = BackgroundColor;

        var layoutObject = new GameObject("Options", typeof(RectTransform), typeof(VerticalLayoutGroup));
        layoutObject.transform.SetParent(canvasRect, false);
        RectTransform layoutRect = (RectTransform)layoutObject.transform;
        layoutRect.anchorMin = new Vector2(0.5f, 0.5f);
        layoutRect.anchorMax = new Vector2(0.5f, 0.5f);
        layoutRect.pivot = new Vector2(0.5f, 0.5f);
        layoutRect.sizeDelta = new Vector2(ButtonWidth, panelHeight - PanelPadding * 2);

        VerticalLayoutGroup layout = layoutObject.GetComponent<VerticalLayoutGroup>();
        layout.spacing = ButtonSpacing;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;

        foreach (PauseMenuOption option in menuOptions)
            CreateButton(layoutRect, option);

        XRUISetup.MoveToUILayer(panelObject);

        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();

        LayoutRebuilder.ForceRebuildLayoutImmediate(layoutRect);
    }

    void CreateButton(RectTransform parent, PauseMenuOption option)
    {
        var buttonObject = new GameObject($"Button_{option.Label}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = ButtonColor;
        buttonObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.text = option.Label;
        text.fontSize = ButtonFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = TextColor;
        text.alignment = TextAnchor.MiddleCenter;

        buttonObject.GetComponent<Button>().onClick.AddListener(() => OnOptionSelected(option));
    }

    void OnOptionSelected(PauseMenuOption option)
    {
        ClosePanelAndResumeLocomotion();
        SendHaptic();

        option.OnSelected?.Invoke();
    }

    void ToggleMicrophone()
    {
        VoiceChat.Instance.MicrophoneOn = !VoiceChat.Instance.MicrophoneOn;
        FloatingMessage.Show(VoiceChat.Instance.MicrophoneOn ? "Micrófono encendido" : "Micrófono apagado");
    }

    void ShowPodStatus()
    {
        if (PodStatusController.Instance != null)
            PodStatusController.Instance.ShowPanel();
    }

    void ShowTutorial()
    {
        if (TutorialController.Instance != null)
            TutorialController.Instance.StartTutorial();
    }

    void GoToMainMenu()
    {
        // Back to neutral - this is the only way back to the home menu from a content
        // scene, on either build, so it's an exit point the mode must reset at (see
        // Bootstrap.AppMode). GoToSceneSelect below is not an exit: it keeps the current mode.
        //
        // Also an exit from a remote rehearsal for both roles: the director ends the synced
        // session (sending the actor home - checked before the mode is reset below, since
        // EndRemoteRehearsal is a no-op outside Remote mode), and the actor leaves it, so later
        // shot selections stop pulling them. Leaving Solo sends Parar first if it was running.
#if DIRECTOR_BUILD
        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.EndRemoteRehearsal();
#endif
        Bootstrap.SetMode(Bootstrap.AppMode.None, "pause menu Menú Principal");

        // Actor_HomeMenu/Director_HomeMenu have no camera of their own - they rely on
        // MenuBackdrop being loaded first, same as Bootstrap's initial connect flow.
        SceneManager.LoadScene("MenuBackdrop");

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu", LoadSceneMode.Additive);
#else
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
#endif
    }

#if DIRECTOR_BUILD
    // Runs on the machine with the pods, through the command channel.
    void StartCalibration()
    {
        if (RehearsalRecorder.Instance != null && RehearsalRecorder.Instance.IsRecording)
        {
            FloatingMessage.Show("No puedes calibrar durante la toma");
            return;
        }

        if (AvatarPoseSync.Instance == null || !AvatarPoseSync.Instance.PodsAvailable)
        {
            FloatingMessage.Show("No hay pods conectados");
            return;
        }

        if (RehearsalSessionSync.Instance != null)
            RehearsalSessionSync.Instance.SendCommand(SessionCommand.Calibrate);
    }

    // The open take's notes, if one is open, and the shot's general ones: the only way to them
    // when the shot has no take.
    void ShowShotNotes()
    {
        if (VoiceNotesPanel.Instance == null)
            return;

        RecordingPlaybackController playback = RecordingPlaybackController.Instance;
        RecordingSession session = playback != null && playback.IsPanelOpen ? playback.CurrentSession : null;
        VoiceNotesPanel.Instance.Show(session?.SessionGuid, Bootstrap.CurrentSceneNumber, Bootstrap.CurrentShotNumber,
            session != null ? playback.JumpTo : (Action<float>)null, null);
    }

    void ExportTakes()
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("PauseMenuController: no ShotListDatabase instance found, nothing exported.");
            return;
        }

        try
        {
            int exported = TakeExporter.ExportAll(database, out int alreadyExported);
            FloatingMessage.Show(exported > 0 ? $"{exported} tomas exportadas" : "No hay tomas nuevas");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Export] failed: {e}");
            FloatingMessage.Show("No se pudieron exportar las tomas");
        }
    }

    void GoToSceneSelect()
    {
        Bootstrap.LoadSceneSelect();
    }

    void ShowFavorites()
    {
        FavoritesPanelController favoritesPanel = FindFirstObjectByType<FavoritesPanelController>();
        if (favoritesPanel == null)
        {
            Debug.LogError("PauseMenuController: no FavoritesPanelController instance found in the scene (expected the persisted object from Bootstrap).");
            return;
        }

        favoritesPanel.Show();
    }
#endif
}
