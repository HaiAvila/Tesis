using System;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Video-game-style controls tutorial,
// one short hint at a time, per role and mode: director in Ensayo Remoto or Inspección de
// Escenas, actor in Ensayo Remoto or Ensayar Solo.
//
// - The first time a content scene opens in a given mode, a small panel asks whether to see
//   the tutorial. Answering (Sí or No) is remembered per role and mode in PlayerPrefs, so it
//   isn't asked again. It can always be replayed from the pause menu's "Tutorial" option.
// - Steps that ask for an action (walk, press B, raise the viewfinder...) move on by
//   themselves once the action is detected, with a short "¡Muy bien!" and a haptic pulse.
//   Info-only steps have a "Siguiente" button. "Saltar tutorial" is always there.
// - Text is Mexican Spanish (tú). In each hint the control to press is highlighted in amber
//   and what it does in light blue (rich text - see Key/Act).
// - In the Editor each hint also shows the XR Device Simulator keys for that control.
//
// The panel floats up and to the left of the view and follows the head with a lag (same idea
// as the replay panel). It never sits on top of another panel with buttons (pause menu,
// stop-recording decision panel, Favoritos, replay panel): if it would, it docks to that
// panel's left side, and comes back once the panel closes - see ComputeAnchor. Only its
// buttons catch the ray, and it's on the UI layer, so virtual cameras don't see it.
public class TutorialController : MonoBehaviour
{
    enum Mode { None, DirectorRemote, DirectorInspection, ActorRemote, ActorSolo }

    class Step
    {
        public string Text;
        public string SimulatorHint; // Editor only
        public Func<bool> IsDone;    // null = info step, advanced with "Siguiente"
    }

    const float PanelWidth = 760f;
    const float PanelWorldScale = 0.0012f;
    const float PanelPadding = 28f;
    const float PanelSpacing = 14f;
    const int TitleFontSize = 22;
    const int HintFontSize = 30;
    const int SimulatorHintFontSize = 20;
    const int ButtonFontSize = 26;
    const float ButtonWidth = 250f;
    const float ButtonHeight = 64f;

    // Placement in the head's yaw-only frame: distance, yaw to the left of straight ahead,
    // height above the eyes - placeholder values, not tuned.
    const float PanelDistance = 1.25f;
    const float PanelYawOffsetDegrees = -20f;
    const float PanelUp = 0.2f;
    const float DockGapDegrees = 4f;
    const float MaxAbovePitchDegrees = 22f;
    const float AbovePitchCostWeight = 1.5f;
    const float ObstacleRefreshSeconds = 0.25f;
    const float FollowDistanceThreshold = 1f;
    const float FollowAngleThreshold = 35f;
    const float FollowTransitionDuration = 0.4f;

    const float PromptDelaySeconds = 0.75f;
    const float CameraWaitTimeoutSeconds = 3f;
    const float StepDoneDelaySeconds = 0.9f;

    const float WalkDistanceMeters = 0.5f;
    const float StickThreshold = 0.6f;
    const float RigTurnDegrees = 20f;

    static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.12f, 0.88f);
    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.95f);
    static readonly Color PrimaryButtonColor = new Color(0.2f, 0.45f, 0.75f, 0.95f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);
    static readonly Color SubTextColor = new Color(0.7f, 0.7f, 0.76f, 1f);
    static readonly Color DoneColor = new Color(0.5f, 0.9f, 0.5f, 1f);

    // Rich-text highlight colors: the control to press, and what it does.
    const string KeyColorHex = "#FFC94A";
    const string ActionColorHex = "#7FD3FF";

    struct AngularRect
    {
        public float MinYaw, MaxYaw, MinPitch, MaxPitch;
    }

    public static TutorialController Instance { get; private set; }

    InputAction leftStickAction;
    InputAction rightStickAction;
    Font uiFont;
    Coroutine promptCoroutine;

    GameObject panelObject;
    Text titleText;
    Text hintText;
    Text simulatorHintText;
    GameObject primaryButton;
    Text primaryButtonLabel;
    GameObject secondaryButton;
    Text secondaryButtonLabel;
    Action onPrimary;
    Action onSecondary;

    Mode currentMode;
    List<Step> steps;
    int stepIndex;
    bool stepDone;
    float stepDoneTime;

    // Captured when each step starts - action steps compare against these.
    Vector3 baselineHeadPosition;
    Transform rig;
    float baselineRigYaw;
#if DIRECTOR_BUILD
    float baselineFocalLength;
    bool baselinePlaying;
    int baselineScrubCount;
#endif

    // Open panels with buttons the tutorial must not cover - see RefreshObstacles.
    readonly List<RectTransform> obstacles = new List<RectTransform>();
    float nextObstacleRefreshTime;
    int obstacleSignature;
    bool obstaclesChanged;

    Vector3 anchorPosition;
    Quaternion anchorRotation;
    Vector3 transitionStartPosition;
    Quaternion transitionStartRotation;
    float transitionElapsed;
    bool isTransitioning;

    void Awake()
    {
        Instance = this;
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Same binding convention as the other controller shortcuts: real + simulator. Only
        // read, never consumed, so stock locomotion on the same sticks keeps working.
        leftStickAction = new InputAction("TutorialLeftStick", InputActionType.Value, expectedControlType: "Vector2");
        leftStickAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        leftStickAction.AddBinding("<XRSimulatedController>{LeftHand}/primary2DAxis");
        leftStickAction.Enable();

        rightStickAction = new InputAction("TutorialRightStick", InputActionType.Value, expectedControlType: "Vector2");
        rightStickAction.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
        rightStickAction.AddBinding("<XRSimulatedController>{RightHand}/primary2DAxis");
        rightStickAction.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        leftStickAction.Disable();
        leftStickAction.Dispose();
        rightStickAction.Disable();
        rightStickAction.Dispose();

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Whatever was showing belonged to the scene that just unloaded. A tutorial in
        // progress isn't resumed - it can be replayed from the pause menu.
        CleanUp();

        Mode current = CurrentMode();
        if (current == Mode.None || HasSeen(current))
            return;

        promptCoroutine = StartCoroutine(ShowPromptWhenReady(current));
    }

    // From the pause menu's "Tutorial" option - straight into the steps, no prompt.
    public void StartTutorial()
    {
        Mode current = CurrentMode();
        if (current == Mode.None)
            return;

        CleanUp();
        if (!BuildPanel())
            return;

        BeginSteps(current);
    }

    static Mode CurrentMode()
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
            return Mode.None;

#if DIRECTOR_BUILD
        return Bootstrap.IsInspecting ? Mode.DirectorInspection : Mode.DirectorRemote;
#else
        if (Bootstrap.ActorJoinedRemoteRehearsal)
            return Mode.ActorRemote;
        return Bootstrap.IsInspecting ? Mode.ActorSolo : Mode.None;
#endif
    }

    static string ModeName(Mode mode)
    {
        switch (mode)
        {
            case Mode.DirectorInspection: return "Inspección de Escenas";
            case Mode.ActorSolo: return "Ensayar Solo";
            default: return "Ensayo Remoto";
        }
    }

    // The mode names include the role (DirectorRemote / ActorRemote...), so director and actor
    // runs in the Editor - which share PlayerPrefs - are remembered separately.
    static string SeenKey(Mode mode) => $"Tutorial.Seen.{mode}";

    static bool HasSeen(Mode mode) => PlayerPrefs.GetInt(SeenKey(mode), 0) == 1;

    static void MarkSeen(Mode mode)
    {
        PlayerPrefs.SetInt(SeenKey(mode), 1);
        PlayerPrefs.Save();
    }

    // The rig's camera isn't there at the exact instant sceneLoaded fires, and a short
    // pause lets the scene settle before asking.
    IEnumerator ShowPromptWhenReady(Mode mode)
    {
        yield return new WaitForSeconds(PromptDelaySeconds);

        float waited = 0f;
        while (Camera.main == null && waited < CameraWaitTimeoutSeconds)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        promptCoroutine = null;
        if (!BuildPanel())
            yield break;

        currentMode = mode;
        titleText.text = $"Tutorial · {ModeName(mode)}";
        SetHint("¿Quieres ver un tutorial de los controles?", null, TextColor);
        SetButtons("Sí", () =>
        {
            MarkSeen(mode);
            BeginSteps(mode);
        }, "No", () =>
        {
            MarkSeen(mode);
            CleanUp();
        });
    }

    void BeginSteps(Mode mode)
    {
        currentMode = mode;
        steps = BuildSteps(mode);
        stepIndex = 0;
        ShowStep();
    }

    void ShowStep()
    {
        if (stepIndex >= steps.Count)
        {
            ShowFinished();
            return;
        }

        Step step = steps[stepIndex];
        stepDone = false;
        CaptureBaselines();

        titleText.text = $"Tutorial · {ModeName(currentMode)} · {stepIndex + 1}/{steps.Count}";
        SetHint(step.Text, step.SimulatorHint, TextColor);

        if (step.IsDone == null)
            SetButtons("Siguiente", NextStep, "Saltar tutorial", CleanUp);
        else
            SetButtons(null, null, "Saltar tutorial", CleanUp);
    }

    void NextStep()
    {
        stepIndex++;
        ShowStep();
    }

    void ShowFinished()
    {
        steps = null;
        titleText.text = $"Tutorial · {ModeName(currentMode)}";
        SetHint($"¡Listo! Puedes volver a ver este tutorial cuando quieras: presiona {Key("Menu")} (control izquierdo) y elige {Key("Tutorial")}.", null, TextColor);
        SetButtons("Cerrar", CleanUp, null, null);
    }

    void Update()
    {
        if (panelObject == null)
            return;

        UpdateFollow();

        if (steps == null || stepIndex >= steps.Count)
            return;

        Step step = steps[stepIndex];
        if (!stepDone)
        {
            if (step.IsDone == null || !step.IsDone())
                return;

            stepDone = true;
            stepDoneTime = Time.time;
            SetHint("¡Muy bien! " + step.Text, null, DoneColor);
            SetButtons(null, null, "Saltar tutorial", CleanUp);
            SendHaptic();
            return;
        }

        if (Time.time - stepDoneTime >= StepDoneDelaySeconds)
            NextStep();
    }

    void CaptureBaselines()
    {
        Camera head = Camera.main;
        baselineHeadPosition = head != null ? head.transform.position : Vector3.zero;

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        rig = origin != null ? origin.transform : null;
        baselineRigYaw = rig != null ? rig.eulerAngles.y : 0f;

#if DIRECTOR_BUILD
        baselineFocalLength = DirectorViewfinderController.Instance != null ? DirectorViewfinderController.Instance.CurrentFocalLengthMM : 0f;
        baselinePlaying = RecordingPlaybackController.Instance != null && RecordingPlaybackController.Instance.IsPlaying;
        baselineScrubCount = RecordingPlaybackController.Instance != null ? RecordingPlaybackController.Instance.ScrubCount : 0;
#endif
    }

    // --- Step content ------------------------------------------------------------------

    // Highlights for the hint text: the control to press (amber) and what it does (light
    // blue). Legacy Text renders these rich-text tags.
    static string Key(string text) => $"<color={KeyColorHex}><b>{text}</b></color>";
    static string Act(string text) => $"<color={ActionColorHex}><b>{text}</b></color>";

    List<Step> BuildSteps(Mode mode)
    {
        var list = new List<Step> { RayStep() };

        switch (mode)
        {
#if DIRECTOR_BUILD
            case Mode.DirectorRemote:
                list.Add(WalkStep());
                list.Add(TurnStep());
                AddDirectorRemoteSteps(list);
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. Desde ahí, {Key("Menú Principal")} {Act("termina el ensayo")}, también para el actor."));
                break;

            case Mode.DirectorInspection:
                AddDirectorInspectionSteps(list);
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}: desde ahí vas a Selección de Escenas, Favoritos o al Menú Principal."));
                break;
#else
            case Mode.ActorRemote:
                list.Add(WalkStep());
                list.Add(TurnStep());
                list.Add(new Step { Text = $"El director elige la toma: cuando la cambie, {Act("pasarás a esa escena")} automáticamente. Si termina el ensayo, {Act("regresas al menú principal")}." });
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. {Key("Menú Principal")} {Act("te saca del ensayo")}."));
                break;

            case Mode.ActorSolo:
                list.Add(WalkStep());
                list.Add(TurnStep());
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. {Key("Menú Principal")} {Act("te regresa al inicio")} para elegir otra toma."));
                break;
#endif
        }

        return list;
    }

    Step RayStep() => new Step
    {
        Text = $"Apunta con el rayo del control y presiona el {Key("gatillo")} para {Act("usar los botones")} de los paneles.",
        SimulatorHint = "Simulador: mantén T (izq.) o Y (der.) y haz clic izquierdo.",
    };

    // Real steps and the left joystick both move the head through the scene.
    Step WalkStep() => new Step
    {
        Text = $"{Act("Camina")} por la escena: caminando de verdad o con el {Key("joystick izquierdo")}.",
        SimulatorHint = "Simulador: mantén T + W/A/S/D.",
        IsDone = () => HeadMovedFromBaseline(),
    };

    // Joystick turning rotates the rig itself, unlike turning the head.
    Step TurnStep() => new Step
    {
        Text = $"Usa el {Key("joystick derecho")} para {Act("girar")} a la izquierda o a la derecha.",
        SimulatorHint = "Simulador: mantén Y + A/D.",
        IsDone = () => Mathf.Abs(rightStickAction.ReadValue<Vector2>().x) > StickThreshold
                       || (rig != null && Mathf.Abs(Mathf.DeltaAngle(baselineRigYaw, rig.eulerAngles.y)) > RigTurnDegrees),
    };

    Step MenuStep(string text) => new Step
    {
        Text = text,
        SimulatorHint = "Simulador: mantén T + M.",
        IsDone = () => PauseMenuController.Instance != null && PauseMenuController.Instance.IsOpen,
    };

    bool HeadMovedFromBaseline()
    {
        Camera head = Camera.main;
        if (head == null)
            return false;

        Vector3 moved = head.transform.position - baselineHeadPosition;
        moved.y = 0f;
        return moved.magnitude > WalkDistanceMeters;
    }

#if DIRECTOR_BUILD
    void AddDirectorRemoteSteps(List<Step> list)
    {
        DirectorViewfinderController viewfinder = DirectorViewfinderController.Instance;
        RehearsalRecorder recorder = RehearsalRecorder.Instance;

        list.Add(new Step
        {
            Text = $"Toca el {Key("Grip derecho")} para {Act("levantar el visor")} de la cámara virtual.",
            SimulatorHint = "Simulador: mantén Y y toca G.",
            IsDone = () => viewfinder != null && viewfinder.IsVisible,
        });
        list.Add(new Step
        {
            Text = $"Con el visor arriba, mueve el {Key("joystick derecho")} a la izquierda o la derecha para {Act("cambiar de lente")} (de 24 a 135 mm).",
            SimulatorHint = "Simulador: mantén Y + A/D.",
            IsDone = () => viewfinder != null && Mathf.Abs(viewfinder.CurrentFocalLengthMM - baselineFocalLength) > 0.5f,
        });
        list.Add(new Step
        {
            Text = $"Mantén el {Key("Grip")} y empuja el {Key("joystick derecho")} para hacer {Act("zoom fino")}: arriba acerca, abajo aleja.",
            SimulatorHint = "Simulador: mantén Y + G + W/S.",
            IsDone = () => viewfinder != null
                           && Mathf.Abs(viewfinder.CurrentFocalLengthMM - baselineFocalLength) > 0.5f
                           && !DirectorViewfinderController.IsNamedFocalLength(viewfinder.CurrentFocalLengthMM),
        });
        list.Add(new Step
        {
            Text = $"Presiona {Key("B")} (control derecho) para {Act("empezar a grabar")} la toma. En el visor verás REC.",
            SimulatorHint = "Simulador: mantén Y + N.",
            IsDone = () => recorder != null && recorder.IsRecording,
        });
        list.Add(new Step
        {
            Text = $"Presiona {Key("B")} otra vez para {Act("detener la grabación")}.",
            SimulatorHint = "Simulador: mantén Y + N.",
            IsDone = () => recorder != null && !recorder.IsRecording,
        });
        list.Add(new Step
        {
            Text = $"Elige qué hacer con la toma: {Key("Ver replay")}, {Key("Guardar")} o {Key("Descartar")}. Antes de guardar puedes {Act("marcarla como preferida o favorita")}.",
            SimulatorHint = "Simulador: mantén T o Y y haz clic izquierdo.",
            // "Ver replay" also closes the decision panel, but it comes back when the replay
            // is closed - so this waits for both to be gone, i.e. Guardar or Descartar.
            IsDone = () => (recorder == null || !recorder.IsDecisionPanelOpen)
                           && (RecordingPlaybackController.Instance == null || !RecordingPlaybackController.Instance.IsPanelOpen),
        });
    }

    // The replay steps only make sense when the shot actually has a recording playing.
    void AddDirectorInspectionSteps(List<Step> list)
    {
        RecordingPlaybackController playback = RecordingPlaybackController.Instance;
        if (playback != null && playback.IsPanelOpen)
        {
            list.Add(new Step
            {
                Text = $"La toma se reproduce sola. Usa el {Key("botón de pausa")} del panel o {Key("A")} (control izquierdo) para {Act("pausar o reanudar")}.",
                SimulatorHint = "Simulador: mantén T + B.",
                IsDone = () => playback.IsPlaying != baselinePlaying,
            });
            list.Add(new Step
            {
                Text = $"Arrastra la {Key("barra")} para {Act("saltar a otro momento")} de la toma. Al soltarla, se queda en pausa ahí.",
                SimulatorHint = "Simulador: mantén T o Y y arrastra con clic izquierdo.",
                IsDone = () => playback.ScrubCount > baselineScrubCount,
            });
            list.Add(new Step
            {
                Text = $"{Key("↺")} {Act("regresa al inicio")}, {Key("∞")} {Act("repite la toma en loop")} y {Key("▣")} {Act("muestra u oculta")} lo que grabó la cámara virtual.",
            });
            list.Add(new Step
            {
                Text = $"La {Key("estrella")} {Act("marca la toma como preferida")} del shot. El {Key("corazón")} {Act("la guarda en Favoritos")}.",
            });
        }

        list.Add(new Step
        {
            Text = $"Usa el {Key("joystick izquierdo")} para {Act("volar")} por la escena, hacia donde estés mirando.",
            SimulatorHint = "Simulador: mantén T + W/A/S/D.",
            IsDone = () => HeadMovedFromBaseline(),
        });
    }
#endif

    // --- Panel -------------------------------------------------------------------------

    bool BuildPanel()
    {
        Camera headCamera = Camera.main;
        if (headCamera == null)
        {
            Debug.LogWarning("TutorialController: no Camera.main found, cannot show the tutorial.");
            return false;
        }

        panelObject = new GameObject("TutorialPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));

        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = headCamera;

        RectTransform canvasRect = (RectTransform)panelObject.transform;
        canvasRect.sizeDelta = new Vector2(PanelWidth, 200f); // height driven by the ContentSizeFitter
        canvasRect.localScale = Vector3.one * PanelWorldScale;

        RefreshObstacles();
        obstaclesChanged = false;
        nextObstacleRefreshTime = Time.time + ObstacleRefreshSeconds;
        ComputeAnchor(headCamera.transform, out anchorPosition, out anchorRotation);
        canvasRect.SetPositionAndRotation(anchorPosition, anchorRotation);
        isTransitioning = false;

        // Background doesn't catch the ray - only the buttons do.
        Image background = panelObject.GetComponent<Image>();
        background.color = BackgroundColor;
        background.raycastTarget = false;

        VerticalLayoutGroup layout = panelObject.GetComponent<VerticalLayoutGroup>();
        int padding = Mathf.RoundToInt(PanelPadding);
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.spacing = PanelSpacing;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        panelObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        titleText = CreateText(canvasRect, "Title", TitleFontSize, FontStyle.Bold, SubTextColor);
        hintText = CreateText(canvasRect, "Hint", HintFontSize, FontStyle.Normal, TextColor);
        simulatorHintText = CreateText(canvasRect, "SimulatorHint", SimulatorHintFontSize, FontStyle.Italic, SubTextColor);
        BuildButtonRow(canvasRect);

        XRUISetup.MoveToUILayer(panelObject);
        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();
        return true;
    }

    Text CreateText(Transform parent, string name, int fontSize, FontStyle style, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(parent, false);

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    void BuildButtonRow(Transform parent)
    {
        var rowObject = new GameObject("Buttons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        rowObject.GetComponent<LayoutElement>().preferredHeight = ButtonHeight;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = PanelSpacing;
        rowLayout.childAlignment = TextAnchor.MiddleRight;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = true;

        // Secondary (Saltar / No) first, primary (Siguiente / Sí / Cerrar) on the right.
        secondaryButton = CreateButton(rowObject.transform, "Secondary", ButtonColor, () => onSecondary?.Invoke(), out secondaryButtonLabel);
        primaryButton = CreateButton(rowObject.transform, "Primary", PrimaryButtonColor, () => onPrimary?.Invoke(), out primaryButtonLabel);
    }

    GameObject CreateButton(Transform parent, string name, Color color, UnityEngine.Events.UnityAction onClick, out Text label)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = color;
        buttonObject.GetComponent<LayoutElement>().preferredWidth = ButtonWidth;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        label = textObject.GetComponent<Text>();
        label.font = uiFont;
        label.fontSize = ButtonFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = TextColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        return buttonObject;
    }

    void SetHint(string text, string simulatorHint, Color color)
    {
        hintText.text = text;
        hintText.color = color;

        bool showSimulatorHint = Application.isEditor && !string.IsNullOrEmpty(simulatorHint);
        simulatorHintText.gameObject.SetActive(showSimulatorHint);
        if (showSimulatorHint)
            simulatorHintText.text = simulatorHint;
    }

    // A null label hides that button.
    void SetButtons(string primaryLabel, Action primaryAction, string secondaryLabel, Action secondaryAction)
    {
        onPrimary = primaryAction;
        onSecondary = secondaryAction;

        primaryButton.SetActive(primaryLabel != null);
        if (primaryLabel != null)
            primaryButtonLabel.text = primaryLabel;

        secondaryButton.SetActive(secondaryLabel != null);
        if (secondaryLabel != null)
            secondaryButtonLabel.text = secondaryLabel;
    }

    // --- Placement -----------------------------------------------------------------------

    // Every open world-space panel with buttons (it has a GraphicRaycaster) on the UI layer,
    // other than this one: pause menu, stop-recording decision panel, Favoritos, replay
    // panel. Display-only canvases (REC indicator, viewfinder, recorded-camera monitor) don't
    // count. Refreshed a few times a second rather than every frame.
    void RefreshObstacles()
    {
        obstacles.Clear();
        int signature = 0;

        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (!canvas.isRootCanvas
                || canvas.renderMode != RenderMode.WorldSpace
                || canvas.gameObject == panelObject
                || canvas.gameObject.layer != XRUISetup.UILayer
                || canvas.GetComponent<GraphicRaycaster>() == null)
                continue;

            obstacles.Add((RectTransform)canvas.transform);
            signature ^= canvas.GetInstanceID();
        }

        if (signature != obstacleSignature)
            obstaclesChanged = true;
        obstacleSignature = signature;
    }

    // A rect's extent as seen from the head: yaw relative to refYaw, and pitch, in degrees.
    static AngularRect Measure(RectTransform rect, Vector3 headPosition, float refYaw)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);

        var result = new AngularRect { MinYaw = float.MaxValue, MaxYaw = float.MinValue, MinPitch = float.MaxValue, MaxPitch = float.MinValue };
        foreach (Vector3 corner in corners)
        {
            Vector3 d = corner - headPosition;
            float yaw = Mathf.DeltaAngle(refYaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
            float pitch = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            result.MinYaw = Mathf.Min(result.MinYaw, yaw);
            result.MaxYaw = Mathf.Max(result.MaxYaw, yaw);
            result.MinPitch = Mathf.Min(result.MinPitch, pitch);
            result.MaxPitch = Mathf.Max(result.MaxPitch, pitch);
        }
        return result;
    }

    static bool Overlaps(AngularRect a, AngularRect b, float gap)
    {
        return a.MaxYaw + gap > b.MinYaw && a.MinYaw - gap < b.MaxYaw
            && a.MaxPitch > b.MinPitch && a.MinPitch < b.MaxPitch;
    }

    static float FlatYaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

    // Default spot: up and to the left of straight ahead (yaw-only, so looking up or down
    // doesn't drag it along). If that spot would cover an open panel with buttons, it moves
    // either to that panel's left or above it - whichever is the smaller move from the
    // default spot (a wide, low panel like the stop-recording one -> above; a tall one like
    // the pause menu -> to the left). Repeated in case the new spot hits another panel.
    // Rotation faces away from the head, the same convention every other panel in this
    // project uses.
    void ComputeAnchor(Transform head, out Vector3 position, out Quaternion rotation)
    {
        float headYaw = FlatYaw(head.forward);

        Rect size = ((RectTransform)panelObject.transform).rect;
        float halfYaw = Mathf.Atan2(size.width * PanelWorldScale / 2f, PanelDistance) * Mathf.Rad2Deg;
        float halfPitch = Mathf.Atan2(size.height * PanelWorldScale / 2f, PanelDistance) * Mathf.Rad2Deg;
        float defaultPitch = Mathf.Atan2(PanelUp, PanelDistance) * Mathf.Rad2Deg;

        float yaw = PanelYawOffsetDegrees;
        float pitch = defaultPitch;
        for (int pass = 0; pass < 6; pass++)
        {
            bool moved = false;
            var self = new AngularRect { MinYaw = yaw - halfYaw, MaxYaw = yaw + halfYaw, MinPitch = pitch - halfPitch, MaxPitch = pitch + halfPitch };

            foreach (RectTransform obstacle in obstacles)
            {
                if (obstacle == null)
                    continue;

                AngularRect other = Measure(obstacle, head.position, headYaw);
                if (Mathf.Abs(other.MinYaw + other.MaxYaw) / 2f > 90f) // behind the head
                    continue;

                if (!Overlaps(self, other, DockGapDegrees))
                    continue;

                // Looking up is less comfortable than turning, so "above" counts extra and is
                // ruled out past MaxAbovePitchDegrees (a tall panel like the pause menu).
                float leftYaw = other.MinYaw - DockGapDegrees - halfYaw;
                float abovePitch = other.MaxPitch + DockGapDegrees + halfPitch;
                bool aboveIsComfortable = abovePitch <= MaxAbovePitchDegrees;
                float leftCost = Mathf.Abs(leftYaw - PanelYawOffsetDegrees);
                float aboveCost = Mathf.Abs(abovePitch - defaultPitch) * AbovePitchCostWeight;
                if (!aboveIsComfortable || leftCost <= aboveCost)
                    yaw = leftYaw;
                else
                    pitch = abovePitch;

                moved = true;
                break;
            }

            if (!moved)
                break;
        }

        // Tilted by the pitch so it still faces the head when it sits higher up.
        rotation = Quaternion.Euler(-pitch, headYaw + yaw, 0f);
        Quaternion flat = Quaternion.Euler(0f, headYaw + yaw, 0f);
        position = head.position + flat * Vector3.forward * PanelDistance + Vector3.up * (PanelDistance * Mathf.Tan(pitch * Mathf.Deg2Rad));
    }

    bool CoversAnObstacle(Transform head)
    {
        float headYaw = FlatYaw(head.forward);
        AngularRect self = Measure((RectTransform)panelObject.transform, head.position, headYaw);
        foreach (RectTransform obstacle in obstacles)
        {
            if (obstacle != null && Overlaps(self, Measure(obstacle, head.position, headYaw), 0f))
                return true;
        }
        return false;
    }

    // Re-anchors once the head has strayed far enough (same body-locked-with-lag behavior as
    // the replay panel), or right away when a panel with buttons opens or closes, or when
    // the tutorial ends up covering one - then glides there.
    void UpdateFollow()
    {
        Camera headCamera = Camera.main;
        if (headCamera == null)
            return;

        if (Time.time >= nextObstacleRefreshTime)
        {
            RefreshObstacles();
            nextObstacleRefreshTime = Time.time + ObstacleRefreshSeconds;
        }

        Transform head = headCamera.transform;
        ComputeAnchor(head, out Vector3 desiredPosition, out Quaternion desiredRotation);

        if (!isTransitioning
            && (obstaclesChanged
                || CoversAnObstacle(head)
                || Vector3.Distance(desiredPosition, anchorPosition) > FollowDistanceThreshold
                || Quaternion.Angle(desiredRotation, anchorRotation) > FollowAngleThreshold))
        {
            obstaclesChanged = false;
            transitionStartPosition = panelObject.transform.position;
            transitionStartRotation = panelObject.transform.rotation;
            anchorPosition = desiredPosition;
            anchorRotation = desiredRotation;
            transitionElapsed = 0f;
            isTransitioning = true;
        }

        if (!isTransitioning)
            return;

        transitionElapsed += Time.deltaTime;
        float t = Mathf.Clamp01(transitionElapsed / FollowTransitionDuration);
        panelObject.transform.SetPositionAndRotation(
            Vector3.Lerp(transitionStartPosition, anchorPosition, t),
            Quaternion.Slerp(transitionStartRotation, anchorRotation, t));

        if (t >= 1f)
            isTransitioning = false;
    }

    void SendHaptic()
    {
        foreach (string controllerName in new[] { "Left Controller", "Right Controller" })
        {
            GameObject controller = GameObject.Find(controllerName);
            HapticImpulsePlayer hapticPlayer = controller != null ? controller.GetComponent<HapticImpulsePlayer>() : null;
            if (hapticPlayer != null)
                hapticPlayer.SendHapticImpulse(0.3f, 0.08f);
        }
    }

    void CleanUp()
    {
        if (promptCoroutine != null)
            StopCoroutine(promptCoroutine);
        promptCoroutine = null;

        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;

        steps = null;
        stepDone = false;
        onPrimary = null;
        onSecondary = null;
        rig = null;
        obstacles.Clear();
        obstacleSignature = 0;
        obstaclesChanged = false;
    }
}
