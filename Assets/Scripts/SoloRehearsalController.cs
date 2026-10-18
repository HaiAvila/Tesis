using System.Collections;
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
    const float MessageDistance = 1.5f;
    const float MessageHoldSeconds = 0.5f;
    const float MessageFadeSeconds = 1f;
    const float MessageWorldScale = 0.002f;
    const int MessageFontSize = 64;
    static readonly Color MessageColor = new Color(1f, 1f, 1f, 1f);
    static readonly Color MessageBackgroundColor = new Color(0.08f, 0.08f, 0.12f, 0.75f);

    public static SoloRehearsalController Instance { get; private set; }

    // Read by the tutorial's solo steps.
    public bool IsRunning => isRunning;

    int currentSceneNumber = SoloSession.NoNumber;
    int currentShotNumber = SoloSession.NoNumber;
    bool isRunning;
    Font uiFont;
#if !DIRECTOR_BUILD
    InputAction startStopAction;
#endif

    bool InTake => currentShotNumber != SoloSession.NoNumber;

    void Awake()
    {
        Instance = this;
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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
        ShowMessage("Iniciando...");
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
            ShowMessage("Detenido");
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
    }

    void LeaveTake(string reason)
    {
        // The take's scene is going away, so no message - just the command.
        StopRunning("left the take", showMessage: false);

        Debug.Log($"[Solo] left take: scene {currentSceneNumber}, shot {currentShotNumber} ({reason})");
        currentSceneNumber = SoloSession.NoNumber;
        currentShotNumber = SoloSession.NoNumber;
    }

    // Placed once in front of the head, at eye level, and left there in the world - not
    // head-locked. Fades out and destroys itself; it belongs to the take's scene, so a scene
    // change takes it along too.
    void ShowMessage(string text)
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        Vector3 forward = head.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        var messageObject = new GameObject("SoloMessage", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Image));
        Canvas canvas = messageObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;

        var rect = (RectTransform)messageObject.transform;
        rect.sizeDelta = new Vector2(520f, 140f);
        rect.SetPositionAndRotation(head.transform.position + forward * MessageDistance, Quaternion.LookRotation(forward, Vector3.up));
        rect.localScale = Vector3.one * MessageWorldScale;

        Image background = messageObject.GetComponent<Image>();
        background.color = MessageBackgroundColor;
        background.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(rect, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text label = textObject.GetComponent<Text>();
        label.font = uiFont;
        label.text = text;
        label.fontSize = MessageFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = MessageColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;

        XRUISetup.MoveToUILayer(messageObject);
        StartCoroutine(FadeAndDestroy(messageObject.GetComponent<CanvasGroup>()));
    }

    IEnumerator FadeAndDestroy(CanvasGroup group)
    {
        yield return new WaitForSeconds(MessageHoldSeconds);

        float elapsed = 0f;
        while (group != null && elapsed < MessageFadeSeconds)
        {
            elapsed += Time.deltaTime;
            group.alpha = 1f - Mathf.Clamp01(elapsed / MessageFadeSeconds);
            yield return null;
        }

        if (group != null)
            Destroy(group.gameObject);
    }

    void SendHaptic()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        HapticImpulsePlayer hapticPlayer = rightController != null ? rightController.GetComponent<HapticImpulsePlayer>() : null;
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
}
