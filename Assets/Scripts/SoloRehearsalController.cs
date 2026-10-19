using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
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
    // Start mark - gaffer-tape "T", in meters. Placeholder look, not tuned.
    const float StartMarkTapeWidth = 0.05f;
    const float StartMarkBarLength = 0.4f;
    const float StartMarkStemLength = 0.25f;
    const float StartMarkToeOffset = 0.12f; // bar a little ahead of the mark's center
    // Above the floor *collider*. The visible floor sits ~1.2 cm higher in scenes 2 and 3
    // (measured: spaceship 0.012 vs 0.000, apartment 0.005 vs -0.006), and a 5 mm lift left
    // the tape under it there. 2.5 cm clears both and still reads as on the floor.
    const float StartMarkFloorOffset = 0.025f;
    static readonly Color StartMarkTapeColor = new Color(1f, 0.85f, 0.1f, 1f);

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
            Debug.LogWarning($"[Solo] no 'Shot{shotNumber}_Start' marker in this scene - no start mark shown.");
            return;
        }

        BuildStartMark(marker.transform, scene: marker.scene);
        SoloPods.GuideToMark(marker.transform.position, marker.transform.rotation);
    }

    // Start mark, like gaffer tape on a set: a "T" on the floor at the shot's start
    // (Shot{N}_Start, the same marker ShotStartPositioner puts the rig on). The bar is where
    // the toes go, across the facing direction; the stem runs back between the feet.
    // Dropped onto whatever floor collider is under the marker, lifted StartMarkFloorOffset so
    // it sits on top of the visible floor, with no colliders of its own. Solo only.
    void BuildStartMark(Transform marker, Scene scene)
    {
        if (!SceneFloor.TryFindFloorBelow(marker.position, out Vector3 floor))
            Debug.LogWarning($"[Solo] no floor found below the start marker at {marker.position} - mark left at the marker's height.");
        Vector3 position = floor + Vector3.up * StartMarkFloorOffset;

        Vector3 forward = marker.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        // A world-space canvas lying flat (1 canvas unit = 1 mm): UI images are unlit, so the
        // tape reads as bright tape whatever the scene's lighting - a lit quad came out dark
        // olive on the mountain set - and the UI shader is always in the build.
        var root = new GameObject("SoloStartMark", typeof(RectTransform), typeof(Canvas));
        SceneManager.MoveGameObjectToScene(root, scene);
        root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = new Vector2(StartMarkBarLength, StartMarkBarLength) * 1000f; // pivot = the mark's center
        rootRect.SetPositionAndRotation(position, Quaternion.LookRotation(Vector3.down, forward)); // face up, canvas "up" = facing direction
        rootRect.localScale = Vector3.one * 0.001f;

        // Canvas space: +y is the facing direction, origin is the mark's center.
        CreateTapeStrip(rootRect, "Bar", new Vector2(0f, StartMarkToeOffset), new Vector2(StartMarkBarLength, StartMarkTapeWidth));
        CreateTapeStrip(rootRect, "Stem", new Vector2(0f, StartMarkToeOffset - StartMarkStemLength / 2f), new Vector2(StartMarkTapeWidth, StartMarkStemLength));

        Debug.Log($"[Solo] start mark shown at {position} for shot {currentShotNumber}");
    }

    // center/size in meters, in the mark's own frame (x = right, y = facing direction).
    static void CreateTapeStrip(RectTransform parent, string name, Vector2 center, Vector2 size)
    {
        var strip = new GameObject(name, typeof(RectTransform), typeof(Image));
        strip.transform.SetParent(parent, false);
        var rect = (RectTransform)strip.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = center * 1000f;
        rect.sizeDelta = size * 1000f;

        Image image = strip.GetComponent<Image>();
        image.color = StartMarkTapeColor;
        image.raycastTarget = false;
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
