using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
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
// - Steps that ask for an action (press B, raise the viewfinder...) move on by themselves once
//   the action is detected, with a short "¡Muy bien!" and a haptic pulse. Info-only steps have
//   a "Siguiente" button. "Saltar tutorial" is always there.
// - The actor's tutorials end on how to calibrate, so the limit and the start are set first.
//   Moving and turning with the joysticks is only a tip: nobody has to do it, since walking and
//   turning your head need no teaching.
//
// The same panel guides the actor through a take's setup, every time, not just once (see
// UpdateActorSetup): the Quest's limit goes back to tiny on every launch, and the avatar has to
// be calibrated. It also holds the calibration's poses, too long for a floating message and
// needed for the whole countdown; a tutorial in progress waits for them.
// - Text is Mexican Spanish (tú). In each hint the control to press is highlighted in amber
//   and what it does in light blue (rich text - see Key/Act).
// - In the Editor each hint also shows the XR Device Simulator keys for that control.
//
// The panel floats up and to the left of the view and follows the head with a lag (same idea
// as the replay panel). It's a "guide" in PanelFocusManager: never hidden when a menu opens,
// and it never sits on top of the panels the manager reports (the open menu, or the shown
// persistent panels): if it would, it docks to that panel's left or above it, and comes
// back once the panel closes - see ComputeAnchor. Only its buttons catch the ray, and it's
// on the UI layer, so virtual cameras don't see it.
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

    // Placement in the head's yaw-only frame: distance, yaw from straight ahead (0 =
    // centered), height relative to the eyes (a little below them).
    const float PanelDistance = 1.25f;
    const float PanelYawOffsetDegrees = 0f;
    const float PanelUp = -0.08f;
    const float DockGapDegrees = 4f;
    const float MaxAbovePitchDegrees = 22f;
    const float AbovePitchCostWeight = 1.5f;
    const float ObstacleRefreshSeconds = 0.25f;
    const float FollowDistanceThreshold = 1f;
    const float FollowAngleThreshold = 45f;
    const float FollowTransitionDuration = 0.4f;

    const float PromptDelaySeconds = 0.75f;
    const float CameraWaitTimeoutSeconds = 3f;
    const float StepDoneDelaySeconds = 0.9f;

    const float WalkDistanceMeters = 0.5f;
    const float SetupDelaySeconds = 1.5f;

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

    Font uiFont;
    Coroutine promptCoroutine;

    GameObject panelObject;
    TMP_Text titleText;
    TMP_Text hintText;
    TMP_Text simulatorHintText;
    GameObject primaryButton;
    TMP_Text primaryButtonLabel;
    GameObject secondaryButton;
    TMP_Text secondaryButtonLabel;
    Action onPrimary;
    Action onSecondary;

    Mode currentMode;
    List<Step> steps;
    int stepIndex;
    bool stepDone;
    float stepDoneTime;

    // Captured when each step starts - action steps compare against these.
    Vector3 baselineHeadPosition;
    int baselineAlignCount;

#if !DIRECTOR_BUILD
    // The actor's setup hints and the calibration's poses, in this same panel.
    bool setupPanel;         // the panel is showing a hint: nothing else asked for it
    bool showingPhase;
    string setupText;
    bool limitDismissed, podsDismissed, calibrationDismissed;
    float setupNotBefore;
#endif
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

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Whatever was showing belonged to the scene that just unloaded. A tutorial in
        // progress isn't resumed - it can be replayed from the pause menu.
        CleanUp();

#if !DIRECTOR_BUILD
        limitDismissed = podsDismissed = calibrationDismissed = false;
        setupNotBefore = Time.time + SetupDelaySeconds;
#endif

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
        switch (Bootstrap.Mode)
        {
            case Bootstrap.AppMode.Remote: return Mode.DirectorRemote;
            case Bootstrap.AppMode.Inspect: return Mode.DirectorInspection;
            default: return Mode.None;
        }
#else
        switch (Bootstrap.Mode)
        {
            case Bootstrap.AppMode.Remote: return Mode.ActorRemote;
            case Bootstrap.AppMode.Solo: return Mode.ActorSolo;
            default: return Mode.None;
        }
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
        bool teachesCalibration = mode == Mode.ActorRemote || mode == Mode.ActorSolo || mode == Mode.DirectorRemote;
        SetHint(teachesCalibration ? "¿Quieres ver un tutorial de cómo calibrar y de los controles?" : "¿Quieres ver un tutorial de los controles?", null, TextColor);
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
#if !DIRECTOR_BUILD
        UpdateActorSetup();
#endif

        if (panelObject == null)
            return;

        UpdateFollow();

#if !DIRECTOR_BUILD
        if (showingPhase)
            return;
#endif

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

    // Whether the calibration's poses show in this panel on this machine (the actor, in a take).
    public bool ShowsCalibrationHere
    {
        get
        {
            Mode mode = CurrentMode();
            return mode == Mode.ActorRemote || mode == Mode.ActorSolo;
        }
    }

#if !DIRECTOR_BUILD
    // In order: the calibration's poses, if one is running (a tutorial in progress resumes after);
    // nothing while a tutorial is open or during a take; then a tiny limit, a pod problem, then
    // calibrating. Each hint can be put off until the next take, and goes away by itself once
    // it's dealt with.
    void UpdateActorSetup()
    {
        if (!ShowsCalibrationHere || Time.time < setupNotBefore)
            return;

        AvatarPoseSync avatar = AvatarPoseSync.Instance;
        string phaseText = avatar != null ? AvatarPoseSync.PhaseInstruction(avatar.RunningPhase) : null;
        if (phaseText != null)
        {
            if (promptCoroutine != null)
            {
                StopCoroutine(promptCoroutine);
                promptCoroutine = null;
            }

            if (panelObject == null && !BuildPanel())
                return;
            showingPhase = true;
            titleText.text = "Calibración";
            SetHint(phaseText, null, TextColor);
            SetButtons(null, null, null, null);
            return;
        }

        if (showingPhase)
        {
            showingPhase = false;
            if (steps != null)
                ShowStep();
            else
                CleanUp();
            return;
        }

        if (promptCoroutine != null || (panelObject != null && !setupPanel))
            return;

        if (StartMarkAligner.IsTakeRunning)
        {
            if (setupPanel)
                CleanUp();
            return;
        }

        PodStatusController pods = PodStatusController.Instance;
        string missingPods = pods != null && !podsDismissed ? pods.MissingPods() : null;
        string lowPods = pods != null && !podsDismissed ? pods.LowPods() : null;

        PlayAreaOutline playArea = PlayAreaOutline.Instance;
        string text = null;
        Action dismiss = null;
        if (!limitDismissed && playArea != null && playArea.IsTiny)
        {
            text = $"Tu {Key("límite")} es muy chico: {playArea.Width:F1} × {playArea.Depth:F1} m. {Act("Camina hacia afuera")} de él y, cuando el Quest te lo ofrezca, {Act("crea un límite nuevo")} del tamaño de tu cuarto.";
            dismiss = () => limitDismissed = true;
        }
        else if (missingPods != null)
        {
            text = $"{Key("Sin conexión")}: {missingPods}. {Act("Revisa que los pods estén encendidos")} y cerca del receptor. Puedes ver el detalle en el menú de pausa, en {Key("Estado de pods")}.";
            dismiss = () => podsDismissed = true;
        }
        else if (lowPods != null)
        {
            text = $"{Key("Batería baja")}: {lowPods}. {Act("Carga los pods")} cuando puedas. Puedes ver el detalle en el menú de pausa, en {Key("Estado de pods")}.";
            dismiss = () => podsDismissed = true;
        }
        else if (!calibrationDismissed && avatar != null && avatar.PodsAvailable && !avatar.IsSending && !avatar.IsReceiving)
        {
            text = $"Cuando estés listo, presiona {Key("Y")} (control izquierdo) para {Act("calibrar tu avatar")}.";
            dismiss = () => calibrationDismissed = true;
        }

        if (text == null)
        {
            if (setupPanel)
                CleanUp();
            return;
        }

        if (setupPanel && text == setupText)
            return;

        if (panelObject == null && !BuildPanel())
            return;

        setupPanel = true;
        setupText = text;
        titleText.text = "Antes de ensayar";
        SetHint(text, null, TextColor);
        SetButtons(null, null, "Ahora no", () =>
        {
            dismiss();
            CleanUp();
        });
    }
#endif

    void CaptureBaselines()
    {
        Camera head = Camera.main;
        baselineHeadPosition = head != null ? head.transform.position : Vector3.zero;

        baselineAlignCount = StartMarkAligner.Instance != null ? StartMarkAligner.Instance.ManualAlignCount : 0;

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
                list.Add(DirectorCalibrationStep());
                list.Add(VoiceStep("el actor"));
                list.Add(VoiceNoteStep());
                list.Add(MoveTipStep());
                list.Add(new Step { Text = $"En el piso, la {Key("línea roja")} es el {Act("límite real del actor")} y la {Key("azul")} es {Act("el tuyo")}: pídele solo lo que le cabe en su espacio." });
                list.Add(new Step { Text = $"Arriba a la izquierda, {Key("Pods 9/9")} te dice {Act("cuántos pods están conectados")}: se pone ámbar con batería baja y rojo si falta uno. El detalle está en el menú de pausa, en {Key("Estado de pods")}." });
                AddDirectorRemoteSteps(list);
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. Desde ahí, {Key("Menú Principal")} {Act("termina el ensayo")}, también para el actor."));
                break;

            case Mode.DirectorInspection:
                AddDirectorInspectionSteps(list);
                list.Add(VoiceNoteStep());
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}: desde ahí vas a Selección de Escenas, Favoritos o al Menú Principal."));
                break;
#else
            case Mode.ActorRemote:
                list.Add(VoiceStep("el director"));
                list.Add(MoveTipStep());
                list.Add(PlayAreaStep());
                list.Add(SetStartStep());
                list.Add(TapeMarkStep());
                list.Add(new Step { Text = $"El director elige la toma: cuando la cambie, {Act("pasarás a esa escena")} automáticamente. Mientras graba, verás {Key("Grabando")} arriba a la izquierda. Si termina el ensayo, {Act("regresas al menú principal")}." });
                list.Add(new Step { Text = $"Si ensayas sin nadie en la computadora, presiona {Key("B")} (control derecho) para {Act("iniciar la toma")}: hay 10 segundos de cuenta regresiva y la grabación empieza en {Act("¡Acción!")}. Presiona {Key("B")} otra vez para {Act("cortar")}. Después guarda o descarta la toma en la computadora." });
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. {Key("Menú Principal")} {Act("te saca del ensayo")} y {Key("Estado de pods")} te muestra {Act("la batería y la señal")} de cada pod."));
                list.Add(ActorCalibrationStep());
                break;

            case Mode.ActorSolo:
                list.Add(MoveTipStep());
                list.Add(PlayAreaStep());
                list.Add(SetStartStep());
                list.Add(TapeMarkStep());
                list.Add(new Step
                {
                    Text = $"Presiona {Key("B")} (control derecho) para {Act("iniciar")} el ensayo de la toma.",
                    SimulatorHint = "Simulador: mantén Y + N.",
                    IsDone = () => SoloRehearsalController.Instance != null && SoloRehearsalController.Instance.IsRunning,
                });
                list.Add(new Step
                {
                    Text = $"Presiona {Key("B")} otra vez para {Act("detenerlo")}. Si abres el menú de pausa o sales de la toma, se detiene solo.",
                    SimulatorHint = "Simulador: mantén Y + N.",
                    IsDone = () => SoloRehearsalController.Instance != null && !SoloRehearsalController.Instance.IsRunning,
                });
                list.Add(MenuStep($"Presiona {Key("Menu")} (control izquierdo) para {Act("abrir el menú de pausa")}. {Key("Menú Principal")} {Act("te regresa al inicio")} para elegir otra toma y {Key("Estado de pods")} te muestra {Act("la batería y la señal")} de cada pod."));
                list.Add(ActorCalibrationStep());
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

    // Just a tip, nothing to do: walking and turning your head need no teaching.
    Step MoveTipStep() => new Step
    {
        Text = $"Consejo: si no quieres caminar, también puedes {Act("moverte")} con el {Key("joystick izquierdo")} y {Act("girar")} con el {Key("joystick derecho")}.",
        SimulatorHint = "Simulador: mantén T + W/A/S/D para moverte, Y + A/D para girar.",
    };

    // Info only, so nobody is made to calibrate in the tutorial; the hint afterwards asks for it.
    // The poses match AvatarPoseSync's instructions, the buzzes PodAvatar's calibration cues.
    Step ActorCalibrationStep() => new Step
    {
        Text = $"Por último, presiona {Key("Y")} (control izquierdo) para {Act("calibrar tu avatar")} cuando estés listo. Este panel te dice cada pose: {Key("en T")}, {Key("brazos abajo con sentadilla ligera")} y {Key("palmas juntas al frente")}. Los pods dan 3 toques antes de cada pose y un {Act("doble pulso")} al capturarla.",
        SimulatorHint = "Simulador: mantén T + N.",
    };

    Step DirectorCalibrationStep() => new Step
    {
        Text = $"El actor {Act("calibra su avatar")} con {Key("Y")} en su control. Si prefieres iniciarlo tú, abre el menú de pausa y elige {Key("Calibrar")}.",
    };

    // Director only (VoiceNoteRecorder).
    Step VoiceNoteStep() => new Step
    {
        Text = $"Para grabar una {Act("nota de voz")}, presiona el {Key("joystick izquierdo")} hacia adentro, o el botón {Key("Nota de voz")} en la esquina. Presiona otra vez para terminar. El actor no te escucha. Se oyen solas en la reproducción, en su momento. Para verlas todas, usa el {Key("micrófono")} del panel de reproducción o, en Inspección, el menú de pausa → {Key("Notas del shot")}.",
    };

    // Ensayo Remoto only: there's no one to talk to otherwise.
    Step VoiceStep(string otherPerson) => new Step
    {
        Text = $"Puedes {Act("hablar con")} {otherPerson}: tu micrófono está encendido durante el ensayo. Si están en el mismo cuarto, apágalo en el menú de pausa, en {Key("Micrófono")}.",
    };

    // Actor only: the Quest's own limit (it isn't drawn by the app).
    Step PlayAreaStep() => new Step
    {
        Text = $"El Quest marca el {Act("límite de tu espacio real")}. Si es muy chico, {Act("camina hacia afuera")} de él y, cuando el Quest te lo ofrezca, {Act("crea un límite nuevo")} del tamaño de tu cuarto.",
    };

    // Actor only: "Fijar inicio aquí" (StartMarkAligner).
    Step SetStartStep() => new Step
    {
        Text = $"Ve a un extremo de tu espacio, mira hacia donde vas a caminar y presiona {Key("X")} (control izquierdo) para {Act("fijar el inicio de la toma aquí")}.",
        SimulatorHint = "Simulador: mantén T + B.",
        IsDone = () => StartMarkAligner.Instance != null && StartMarkAligner.Instance.ManualAlignCount > baselineAlignCount,
    };

    // Actor only: the tape "T" (StartMarkTape). It starts out right under the feet, below the
    // headset's view when looking ahead - hence "mira hacia abajo".
    Step TapeMarkStep() => new Step
    {
        Text = $"La {Key("marca de cinta amarilla")} en el piso es el {Act("inicio de la toma")}. Está a tus pies: {Act("mira hacia abajo")} para verla y párate con la punta de los pies en la barra.",
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
            Text = $"Presiona {Key("B")} (control derecho) para {Act("empezar a grabar")} la toma. Primero hay una cuenta regresiva de 10 segundos con pitidos (para ponerte el visor si también actúas); la grabación empieza en {Act("¡Acción!")}. En el visor verás REC.",
            SimulatorHint = "Simulador: mantén Y + N.",
            IsDone = () => recorder != null && recorder.IsRecording,
        });
        // Escena completa only (RehearsalRecorder.NextShot). Also done once the recording
        // stops, since Siguiente toma is optional and does nothing after that.
        if (Bootstrap.IsFullScene)
        {
            list.Add(new Step
            {
                Text = $"Estás grabando la escena completa. Presiona {Key("Y")} (control izquierdo) para {Act("pasar a la siguiente toma")}: marca dónde termina un shot y empieza el siguiente. Es opcional.",
                SimulatorHint = "Simulador: mantén T + N.",
                IsDone = () => recorder != null && (recorder.PendingBoundaries.Count > 1 || !recorder.IsRecording),
            });
        }
        list.Add(new Step
        {
            Text = $"Presiona {Key("B")} otra vez para {Act("detener la grabación")}.",
            SimulatorHint = "Simulador: mantén Y + N.",
            IsDone = () => recorder != null && !recorder.IsRecording,
        });
        list.Add(new Step
        {
            // A scene take can't be preferida (the stop panel has no such box for it).
            Text = $"Elige qué hacer con la toma: {Key("Ver replay")}, {Key("Guardar")} o {Key("Descartar")}. Antes de guardar puedes {Act(Bootstrap.IsFullScene ? "marcarla como favorita" : "marcarla como preferida o favorita")}.",
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
                Text = $"La toma se reproduce sola. Usa el {Key("botón de pausa")} del panel o {Key("X")} (control izquierdo) para {Act("pausar o reanudar")}.",
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
                Text = $"{Key("↺")} {Act("regresa al inicio")}, {Key("1×")} {Act("cambia la velocidad")} (1.5×, 2×, 0.25×, 0.5×), {Key("∞")} {Act("repite la toma en loop")}, {Key("Conv.")} y {Key("Notas")} {Act("silencian")} la conversación y las notas de la toma, y {Key("▣")} {Act("muestra u oculta")} lo que grabó la cámara virtual.",
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
        GlassUI.Glass(background, 30f);
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

        titleText = CreateText(canvasRect, "Title", TitleFontSize, FontStyles.Bold, SubTextColor);
        hintText = CreateText(canvasRect, "Hint", HintFontSize, FontStyles.Normal, TextColor);
        simulatorHintText = CreateText(canvasRect, "SimulatorHint", SimulatorHintFontSize, FontStyles.Italic, SubTextColor);
        BuildButtonRow(canvasRect);

        XRUISetup.MoveToUILayer(panelObject);
        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.RegisterGuide(panelObject, "Tutorial");
        return true;
    }

    TMP_Text CreateText(Transform parent, string name, int fontSize, FontStyles style, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);

        TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = GlassUI.Regular;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAlignmentOptions.Left;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
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

    GameObject CreateButton(Transform parent, string name, Color color, UnityEngine.Events.UnityAction onClick, out TMP_Text label)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Image>().color = color;
        buttonObject.GetComponent<LayoutElement>().preferredWidth = ButtonWidth;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        label = textObject.GetComponent<TextMeshProUGUI>();
        label.font = GlassUI.Regular;
        label.fontSize = ButtonFontSize;
        label.fontStyle = FontStyles.Bold;
        label.color = TextColor;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        GlassUI.SkinButton(buttonObject, ButtonHeight / 2f, color == PrimaryButtonColor ? GlassUI.ButtonStyle.Primary : GlassUI.ButtonStyle.Normal);
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

    // The panels the tutorial must not cover come from PanelFocusManager - the one place that
    // knows what's open: the modal on top, or, with no menu open, every persistent panel
    // that's shown (replay panel, recorded-camera monitor, viewfinder). The tutorial is
    // registered there as a guide, so it's never hidden itself. Refreshed a few times a
    // second rather than every frame.
    void RefreshObstacles()
    {
        PanelFocusManager.GetObstacles(obstacles);

        // Head-locked panels (the corner chips) move with the head, so they'd always end
        // up wherever the tutorial is when you turn to read it - avoiding them made it run
        // away. Only panels that stay put in the world count.
        Camera headCamera = Camera.main;
        if (headCamera != null)
            obstacles.RemoveAll(obstacle => obstacle == null || obstacle.IsChildOf(headCamera.transform));

        int signature = 0;
        foreach (RectTransform obstacle in obstacles)
            signature ^= obstacle.GetInstanceID();

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

    // Default spot: centered in front, a little below the eyes (yaw-only, so looking up or
    // down doesn't drag it along). If that spot would cover an open panel with buttons, it moves
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

        PanelFocusManager.Unregister(panelObject);
        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;

#if !DIRECTOR_BUILD
        setupPanel = false;
        showingPhase = false;
        setupText = null;
#endif
        steps = null;
        stepDone = false;
        onPrimary = null;
        onSecondary = null;
        obstacles.Clear();
        obstacleSignature = 0;
        obstaclesChanged = false;
    }
}
