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
        public string Icon;
        public string Label;
        public string Description;
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
        bool remote = Bootstrap.Mode == Bootstrap.AppMode.Remote;
        var menuOptions = new List<PauseMenuOption>
        {
            new PauseMenuOption { Icon = GlassUI.Icon.Home, Label = "Menú principal",
#if DIRECTOR_BUILD
                Description = remote ? "Termina el ensayo, también para el actor, y regresa al inicio." : "Regresa al inicio.",
#else
                Description = remote ? "Sales del ensayo y regresas al inicio." : "Regresa al inicio.",
#endif
                OnSelected = GoToMainMenu },
        };

#if DIRECTOR_BUILD
        menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Scenes, Label = "Escenas", Description = "Elige otra escena o toma.", OnSelected = GoToSceneSelect });
        menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Favorite, Label = "Favoritos", Description = "Las tomas que marcaste con corazón.", OnSelected = ShowFavorites });
        if (remote)
            menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Calibrate, Label = "Calibrar", Description = "Calibra el avatar del actor: pose T, sentadilla y palmas juntas.", OnSelected = StartCalibration });
        if (Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        {
            menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Notes, Label = "Notas del shot", Description = "Escucha las notas de voz de este shot.", OnSelected = ShowShotNotes });
            menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Export, Label = "Exportar CSV", Description = "Guarda las tomas nuevas en archivos para analizarlas.", OnSelected = ExportTakes });
        }
#endif

        if (remote && VoiceChat.Instance != null)
        {
            bool on = VoiceChat.Instance.MicrophoneOn;
            menuOptions.Add(new PauseMenuOption
            {
                Icon = on ? GlassUI.Icon.Mic : GlassUI.Icon.MicOff,
                Label = "Micrófono",
                Description = on ? "Encendido: te escuchan. Elígelo para apagarlo." : "Apagado: no te escuchan. Elígelo para encenderlo.",
                OnSelected = ToggleMicrophone,
            });
        }

        // The set's scripted action (scene 3's drone): back to its start, then again after its
        // countdown. Not during a replay, which plays the take's own.
        if (SetAction.HasTarget && SetAction.CanTrigger && !SetAction.ReplayActive)
            menuOptions.Add(new PauseMenuOption
            {
                Icon = GlassUI.Icon.Robot,
                Label = $"Repetir {SetAction.TargetName}",
                Description = $"Vuelve a su lugar y sale otra vez a los {SetAction.TargetDelay:0} segundos.",
                OnSelected = RepeatSetAction,
            });

        menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Pods, Label = "Estado de pods", Description = "Batería y señal de cada pod.", OnSelected = ShowPodStatus });

        // Replays the controls tutorial for the current mode - it's only offered
        // automatically the first time.
        menuOptions.Add(new PauseMenuOption { Icon = GlassUI.Icon.Tutorial, Label = "Tutorial", Description = "Repite el tutorial de los controles.", OnSelected = ShowTutorial });

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

    // A wheel (RadialMenu): icons around, what's pointed at in the middle, which also closes it.
    void BuildPanel(Transform head, List<PauseMenuOption> menuOptions)
    {
        var items = new List<RadialMenu.Item>();
        foreach (PauseMenuOption option in menuOptions)
        {
            PauseMenuOption captured = option;
            items.Add(new RadialMenu.Item { Icon = option.Icon, Label = option.Label, Description = option.Description, OnSelected = () => OnOptionSelected(captured) });
        }

        RadialMenu menu = RadialMenu.Create(head, PanelDistance, items, "Menú", "Apunta a una opción o mueve el joystick izquierdo. Aquí en medio, cierra.", Close);
        panelObject = menu.gameObject;
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

    void RepeatSetAction()
    {
        SetAction.Trigger("pause menu Repetir");
        FloatingMessage.Show($"El {SetAction.TargetName} sale en {SetAction.TargetDelay:0} s");
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
            FloatingMessage.Show("No hay pods: abre la app en la computadora con los pods y el receptor");
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
