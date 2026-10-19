using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

// Persistent (on the "SoloSession" object under Bootstrap, next to SoloSession). Drives the
// actor's Ensayar Solo (Bootstrap.Mode == Solo): writes the loaded take into SoloSession,
// and clears it on leaving solo. Solo is its own mode - the director's tools and events
// never act in it (each checks Bootstrap.Mode) - and it never touches the director-owned
// RehearsalSessionStateModel.
//
// Uses the room connection Bootstrap already has. If the model isn't there yet when a take
// loads (still connecting) or comes back after a reconnect, the take is written again as
// soon as SoloSession.ModelReady fires.
//
// Iniciar / Parar: the right controller's B (secondary) toggles them while in a take - the
// same button the director records with, so "B = start/stop" means the same in both roles
// (A is the rig's stock Jump). Each press sends the command through SoloSession and shows a
// short world-space message that fades out. Nothing is recorded on the Quest. Opening the
// pause menu or leaving the take while running sends Parar automatically.
public class SoloRehearsalController : MonoBehaviour
{
    public static SoloRehearsalController Instance { get; private set; }

    // Read by the tutorial's solo steps.
    public bool IsRunning => isRunning;

    int currentSceneNumber = SoloSession.NoNumber;
    int currentShotNumber = SoloSession.NoNumber;
    bool isRunning;
#if !DIRECTOR_BUILD
    InputAction startStopAction;
#endif

    bool InTake => currentShotNumber != SoloSession.NoNumber;

    void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Bootstrap.ModeChanged += OnModeChanged;

#if !DIRECTOR_BUILD
        // Same binding convention as the other controller shortcuts: real + simulator.
        startStopAction = new InputAction("SoloStartStop", InputActionType.Button);
        startStopAction.AddBinding("<XRController>{RightHand}/{secondaryButton}");
        startStopAction.AddBinding("<XRSimulatedController>{RightHand}/secondaryButton");
        startStopAction.performed += OnStartStopPressed;
        startStopAction.Enable();
#endif
    }

    void Update()
    {
        // Opening the pause menu mid-run stops the run.
        if (isRunning && PauseMenuController.Instance != null && PauseMenuController.Instance.IsOpen)
            StopRunning("pause menu opened", showMessage: true);
    }

#if !DIRECTOR_BUILD
    void OnStartStopPressed(InputAction.CallbackContext context)
    {
        if (Bootstrap.Mode != Bootstrap.AppMode.Solo || !InTake)
            return;
        if (PauseMenuController.Instance != null && PauseMenuController.Instance.IsOpen)
            return;

        if (isRunning)
            StopRunning("B pressed", showMessage: true);
        else
            StartRunning();
    }
#endif

    void StartRunning()
    {
        isRunning = true;
        Debug.Log($"[Solo] Iniciar (scene {currentSceneNumber}, shot {currentShotNumber})");
        if (SoloSession.Instance != null)
            SoloSession.Instance.SendCommand(SoloCommand.Iniciar);
        FloatingMessage.Show("Iniciando...");
        SendHaptic();
    }

    void StopRunning(string reason, bool showMessage)
    {
        if (!isRunning)
            return;

        isRunning = false;
        Debug.Log($"[Solo] Parar ({reason})");
        if (SoloSession.Instance != null)
            SoloSession.Instance.SendCommand(SoloCommand.Parar);
        if (showMessage)
            FloatingMessage.Show("Detenido");
        SendHaptic();
    }

    void Start()
    {
        if (SoloSession.Instance != null)
            SoloSession.Instance.ModelReady += OnModelReady;
        else
            Debug.LogError("[Solo] no SoloSession found - solo won't be synced.");
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        Bootstrap.ModeChanged -= OnModeChanged;
        if (SoloSession.Instance != null)
            SoloSession.Instance.ModelReady -= OnModelReady;

#if !DIRECTOR_BUILD
        startStopAction.performed -= OnStartStopPressed;
        startStopAction.Disable();
        startStopAction.Dispose();
#endif
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (Bootstrap.Mode != Bootstrap.AppMode.Solo)
            return;

        if (!Bootstrap.IsContentScene(scene.name))
        {
            if (InTake)
                LeaveTake("left the take");
            return;
        }

        EnterTake(Bootstrap.CurrentShotNumber);
    }

    void OnModeChanged(Bootstrap.AppMode previous, Bootstrap.AppMode current)
    {
        if (previous != Bootstrap.AppMode.Solo)
            return;

        if (InTake)
            LeaveTake($"mode {previous} -> {current}");

        if (SoloSession.Instance != null)
            SoloSession.Instance.End();
    }

    void OnModelReady()
    {
        if (Bootstrap.Mode == Bootstrap.AppMode.Solo && InTake && SoloSession.Instance != null)
            SoloSession.Instance.Begin(currentSceneNumber, currentShotNumber);
    }

    void EnterTake(int shotNumber)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        int? sceneNumber = database != null ? database.GetSceneNumberForShot(shotNumber) : null;

        currentShotNumber = shotNumber;
        currentSceneNumber = sceneNumber ?? SoloSession.NoNumber;
        Debug.Log($"[Solo] entered take: scene {currentSceneNumber}, shot {currentShotNumber}");

        if (SoloSession.Instance != null)
            SoloSession.Instance.Begin(currentSceneNumber, currentShotNumber);

        GameObject marker = GameObject.Find($"Shot{shotNumber}_Start");
        if (marker == null)
        {
            Debug.LogWarning($"[Solo] no 'Shot{shotNumber}_Start' marker in this scene - pods not guided.");
            return;
        }

        SoloPods.GuideToMark(marker.transform.position, marker.transform.rotation);
    }

    void LeaveTake(string reason)
    {
        // The take's scene is going away, so no message - just the command.
        StopRunning("left the take", showMessage: false);

        Debug.Log($"[Solo] left take: scene {currentSceneNumber}, shot {currentShotNumber} ({reason})");
        currentSceneNumber = SoloSession.NoNumber;
        currentShotNumber = SoloSession.NoNumber;
    }

    void SendHaptic()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        HapticImpulsePlayer hapticPlayer = rightController != null ? rightController.GetComponent<HapticImpulsePlayer>() : null;
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
}
