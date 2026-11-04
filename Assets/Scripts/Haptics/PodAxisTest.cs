using UnityEngine;
using UnityEngine.InputSystem;

// Diagnostics only - the PodAxisTest scene, played on its own (no Bootstrap, XR rig or
// Normcore). One pod-shaped box follows ONE pod, to check PodReceiver's conversion and axis
// flips up close: hold the pod the way the box sits, press Z, then turn the pod.
//
// - The box is the pod's housing, 52 x 12 x 29 mm, ten times bigger: length along X with the
//   USB end (orange) at +X, thickness along Y with a blue patch on the top face, width
//   along Z. Fixed red/green/blue bars at the left show Unity's +X (right), +Y (up) and +Z
//   (into the screen).
// - Zero in two steps, because the BNO's game rotation vector has no compass: its "forward"
//   is wherever the pod faced at power-up, so a single zero leaves the axes the pod turns
//   about rotated by that unknown heading - with ~90° the two tilts look swapped.
//     1. Z: the pod flat, top up, USB end to the right - the box's own pose. The box then
//        shows box = q · q0⁻¹ (right pose, but the tilt axes still carry the BNO's heading).
//     2. X: the USB end tipped straight up (+90° about the axis into the screen). That turn,
//        seen by the BNO, is about R·forward, where R is the BNO's heading relative to the
//        screen; R is read off it, and from then on box = R⁻¹ · (q · q0⁻¹) · R, which turns
//        about the same axes as the pod. Same math the body calibration will need (one pose
//        can't separate heading from how the pod is mounted).
//   Both steps are kept as raw samples and recomputed each frame, so they survive changing
//   the conversion or the flips.
// - C cycles PodReceiver.conversion, 1/2/3 toggle PodReceiver.flipX/Y/Z. Those live on the
//   PodReceiver object (its Inspector), since the whole app uses the same conversion.
// - On screen: pod state, packets per second, raw quaternion, converted quaternion + euler,
//   the box's euler, and "--" for acc: the pods don't send one (the BNO085's game rotation
//   vector has no accuracy estimate).
// - Every step is logged with [PodAxisTest].
public class PodAxisTest : MonoBehaviour
{
    const int ConversionCount = 4; // the cases in PodReceiver.ToUnity()
    const float BoxScale = 10f;
    static readonly Vector3 PodSizeMeters = new Vector3(0.052f, 0.012f, 0.029f);
    const float RateWindow = 1f;
    const float RateLogInterval = 5f;

    static readonly Color BodyColor = new Color(0.82f, 0.82f, 0.86f);
    static readonly Color UsbColor = new Color(1f, 0.55f, 0.1f);
    static readonly Color TopColor = new Color(0.25f, 0.55f, 1f);
    static readonly Color AxisXColor = new Color(0.9f, 0.2f, 0.2f);
    static readonly Color AxisYColor = new Color(0.2f, 0.8f, 0.25f);
    static readonly Color AxisZColor = new Color(0.25f, 0.45f, 1f);

    public PodReceiver receiver;

    [Tooltip("The one pod the box follows (1-9). Can be changed in Play mode.")]
    public int podId = 1;

    Transform box;
    Vector3 axesOrigin;

    bool zeroed;
    Vector4 zeroRaw;
    float zeroedAt;

    // Step 2 of the zero (X): the USB end tipped straight up.
    bool headingSet;
    Vector4 step2Raw;

    // A good step 2 is ~90° about a horizontal axis.
    const float Step2AngleTolerance = 30f;
    const float Step2MaxVerticalAxis = 0.5f;

    long rateCount;
    float rateStart;
    float packetsPerSecond;
    float lastRateLog;

    // Last state, so each change is logged once.
    int loggedPodId = -1;
    int loggedState = -1;
    int loggedConversion = -1;
    bool loggedFlipX, loggedFlipY, loggedFlipZ;

    InputAction zeroAction, step2Action, conversionAction, flipXAction, flipYAction, flipZAction;
    GUIStyle textStyle, smallStyle, axisLabelStyle;

    // Read at the IMGUI layout pass and reused by the draw pass - see OnGUI.
    int guiState;
    bool guiHasRaw, guiFocused = true;
    Vector4 guiRaw;

    void Awake()
    {
        if (receiver == null)
            receiver = FindFirstObjectByType<PodReceiver>();
        if (receiver == null)
            Debug.LogError("[PodAxisTest] no PodReceiver in the scene.");

        zeroAction = Bind("ZeroPod", "<Keyboard>/z", Zero);
        step2Action = Bind("ZeroStep2", "<Keyboard>/x", Step2);
        conversionAction = Bind("CycleConversion", "<Keyboard>/c", CycleConversion);
        flipXAction = Bind("FlipX", "<Keyboard>/1", () => ToggleFlip(0));
        flipYAction = Bind("FlipY", "<Keyboard>/2", () => ToggleFlip(1));
        flipZAction = Bind("FlipZ", "<Keyboard>/3", () => ToggleFlip(2));

        BuildBox();
        BuildWorldAxes();
        Debug.Log($"[PodAxisTest] started: following pod {podId}. Z zero (flat, USB right), X step 2 (USB up), C conversion, 1/2/3 flip X/Y/Z.");
    }

    static InputAction Bind(string name, string path, System.Action onPress)
    {
        var action = new InputAction(name, InputActionType.Button, path);
        action.performed += _ => onPress();
        action.Enable();
        return action;
    }

    void OnDestroy()
    {
        foreach (InputAction action in new[] { zeroAction, step2Action, conversionAction, flipXAction, flipYAction, flipZAction })
        {
            action.Disable();
            action.Dispose();
        }
    }

    // --- box and reference axes ----------------------------------------------

    void BuildBox()
    {
        Vector3 size = PodSizeMeters * BoxScale; // 0.52 x 0.12 x 0.29 m

        box = new GameObject("PodBox").transform;
        box.SetParent(transform, false);

        CreateBlock(box, "Body", Vector3.zero, size, BodyColor);
        // USB end: a block sticking out of the +X face.
        CreateBlock(box, "USB", new Vector3(size.x / 2f + 0.02f, 0f, 0f), new Vector3(0.04f, size.y * 0.55f, size.z * 0.4f), UsbColor);
        // Top face, toward the USB end, so top/bottom and the two ends all look different.
        CreateBlock(box, "Top", new Vector3(size.x * 0.25f, size.y / 2f + 0.003f, 0f), new Vector3(size.x * 0.3f, 0.006f, size.z * 0.6f), TopColor);
    }

    void BuildWorldAxes()
    {
        const float Length = 0.3f;
        const float Thickness = 0.015f;
        axesOrigin = transform.position + new Vector3(-0.8f, -0.12f, 0f);

        CreateBlock(transform, "World +X", axesOrigin - transform.position + Vector3.right * Length / 2f, new Vector3(Length, Thickness, Thickness), AxisXColor);
        CreateBlock(transform, "World +Y", axesOrigin - transform.position + Vector3.up * Length / 2f, new Vector3(Thickness, Length, Thickness), AxisYColor);
        CreateBlock(transform, "World +Z", axesOrigin - transform.position + Vector3.forward * Length / 2f, new Vector3(Thickness, Thickness, Length), AxisZColor);
    }

    static void CreateBlock(Transform parent, string name, Vector3 localPosition, Vector3 size, Color color)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = name;
        block.transform.SetParent(parent, false);
        block.transform.localPosition = localPosition;
        block.transform.localScale = size;
        Destroy(block.GetComponent<Collider>());
        block.GetComponent<Renderer>().material.color = color;
    }

    // --- controls ----------------------------------------------------------------

    // Also called by the Inspector buttons (PodAxisTestInspector), which work without Game view focus.
    public void Zero()
    {
        if (receiver == null || !receiver.TryGetRawRotation(podId, out Vector4 raw))
        {
            Debug.Log($"[PodAxisTest] Z ignored: pod {podId} has no IMU reading yet.");
            return;
        }

        zeroed = true;
        zeroRaw = raw;
        zeroedAt = Time.time;
        headingSet = false;
        Debug.Log($"[PodAxisTest] pod {podId} zeroed (step 1, flat, USB right) at raw {Format(raw)} (converted {Format(receiver.ToUnityRotation(raw))}). Now tip the USB end straight up and press X.");
    }

    // Step 2 of the zero: the USB end tipped straight up from the step-1 pose.
    public void Step2()
    {
        if (receiver == null || !zeroed)
        {
            Debug.Log("[PodAxisTest] X ignored: do step 1 (Z, pod flat with the USB end to the right) first.");
            return;
        }
        if (!receiver.TryGetRawRotation(podId, out Vector4 raw))
        {
            Debug.Log($"[PodAxisTest] X ignored: pod {podId} has no IMU reading.");
            return;
        }

        if (!TryReadHeading(raw, out Quaternion heading, out float angle, out Vector3 axis, out string problem))
        {
            Debug.LogWarning($"[PodAxisTest] step 2 rejected: {problem} (turn of {angle:F0}° about {axis.ToString("F2")}). Tip the USB end straight up, about 90°, and press X again.");
            return;
        }

        step2Raw = raw;
        headingSet = true;
        Quaternion mount = Quaternion.Inverse(heading) * receiver.ToUnityRotation(zeroRaw);
        Debug.Log($"[PodAxisTest] step 2 done: turn of {angle:F0}° about {axis.ToString("F2")}; BNO heading vs. the screen {Signed(heading.eulerAngles.y):F0}°; chip in the pod (euler) {FormatEuler(mount)}.");
    }

    // From the turn between step 1 and step 2: in Unity, USB right -> USB up is +90° about
    // forward (Quaternion.Euler(0, 0, 90) * right = up). The BNO sees R·P1·R⁻¹, a turn about
    // R·forward, so R is the yaw that takes forward to that axis.
    bool TryReadHeading(Vector4 step2, out Quaternion heading, out float angle, out Vector3 axis, out string problem)
    {
        Quaternion turn = receiver.ToUnityRotation(step2) * Quaternion.Inverse(receiver.ToUnityRotation(zeroRaw));
        turn.ToAngleAxis(out angle, out axis);
        if (angle > 180f)
        {
            angle = 360f - angle;
            axis = -axis;
        }

        heading = Quaternion.identity;
        problem = null;
        if (Mathf.Abs(angle - 90f) > Step2AngleTolerance)
            problem = $"expected ~90°, got {angle:F0}°";
        else if (Mathf.Abs(axis.y) > Step2MaxVerticalAxis)
            problem = "that was mostly a turn about the vertical, not a tip";
        if (problem != null)
            return false;

        Vector3 flat = new Vector3(axis.x, 0f, axis.z).normalized;
        heading = Quaternion.AngleAxis(Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg, Vector3.up);
        return true;
    }

    public void CycleConversion()
    {
        if (receiver != null)
            receiver.conversion = (receiver.conversion + 1) % ConversionCount;
    }

    // axis: 0 = X, 1 = Y, 2 = Z.
    public void ToggleFlip(int axis)
    {
        if (receiver == null)
            return;

        if (axis == 0) receiver.flipX = !receiver.flipX;
        else if (axis == 1) receiver.flipY = !receiver.flipY;
        else receiver.flipZ = !receiver.flipZ;
    }

    // --- per frame -----------------------------------------------------------------

    void Update()
    {
        if (receiver == null)
            return;

        LogChanges();
        UpdateRate();

        bool hasReading = receiver.HasOrientation(podId);
        box.gameObject.SetActive(hasReading);
        if (!hasReading)
            return;

        Quaternion current = receiver.GetRotation(podId);
        if (!zeroed)
        {
            box.localRotation = current;
            return;
        }

        Quaternion sinceZero = current * Quaternion.Inverse(receiver.ToUnityRotation(zeroRaw));
        // Recomputed every frame from the raw samples, so a conversion change applies at once.
        if (headingSet && TryReadHeading(step2Raw, out Quaternion heading, out _, out _, out _))
            box.localRotation = Quaternion.Inverse(heading) * sinceZero * heading;
        else
            box.localRotation = sinceZero;
    }

    void UpdateRate()
    {
        long count = receiver.GetPacketCount(podId);
        float elapsed = Time.unscaledTime - rateStart;
        if (elapsed < RateWindow)
            return;

        packetsPerSecond = (count - rateCount) / elapsed;
        rateCount = count;
        rateStart = Time.unscaledTime;

        if (packetsPerSecond > 0f && Time.unscaledTime - lastRateLog >= RateLogInterval)
        {
            lastRateLog = Time.unscaledTime;
            Debug.Log($"[PodAxisTest] pod {podId}: {packetsPerSecond:F1} packets/s");
        }
    }

    // 0 = no data, 1 = connected without an IMU reading, 2 = readings.
    int PodState() => receiver.HasOrientation(podId) ? 2 : receiver.IsAlive(podId) ? 1 : 0;

    void LogChanges()
    {
        if (podId != loggedPodId)
        {
            if (loggedPodId != -1)
                Debug.Log($"[PodAxisTest] now following pod {podId} (was {loggedPodId}); zero cleared.");
            loggedPodId = podId;
            loggedState = -1;
            zeroed = false;
            headingSet = false;
            rateCount = receiver.GetPacketCount(podId);
            rateStart = Time.unscaledTime;
        }

        int state = PodState();
        if (state != loggedState)
        {
            loggedState = state;
            string text = state == 2 ? "IMU readings arriving" : state == 1 ? "connected, NO IMU reading (all-zero quaternion)" : "no data (pod off, or bridge.py not running)";
            Debug.Log($"[PodAxisTest] pod {podId}: {text}");
        }

        if (receiver.conversion != loggedConversion || receiver.flipX != loggedFlipX || receiver.flipY != loggedFlipY || receiver.flipZ != loggedFlipZ)
        {
            loggedConversion = receiver.conversion;
            loggedFlipX = receiver.flipX;
            loggedFlipY = receiver.flipY;
            loggedFlipZ = receiver.flipZ;
            Debug.Log($"[PodAxisTest] conversion {receiver.conversion}, flip X {receiver.flipX}, Y {receiver.flipY}, Z {receiver.flipZ}");
        }
    }

    // --- on screen -------------------------------------------------------------------

    static string Format(Vector4 q) => $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})";
    static string Format(Quaternion q) => $"({q.x:F3}, {q.y:F3}, {q.z:F3}, {q.w:F3})";

    // Euler angles as -180..180, easier to read than Unity's 0..360.
    static string FormatEuler(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return $"({Signed(e.x):F0}°, {Signed(e.y):F0}°, {Signed(e.z):F0}°)";
    }

    static float Signed(float angle) => angle > 180f ? angle - 360f : angle;

    static string YesNo(bool value) => value ? "<b>sí</b>" : "no";

    void OnGUI()
    {
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, richText = true, wordWrap = true };
            textStyle.normal.textColor = Color.white;
            smallStyle = new GUIStyle(textStyle) { fontSize = 14 };
            smallStyle.normal.textColor = new Color(0.75f, 0.75f, 0.8f);
            axisLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        }

        if (receiver == null)
        {
            GUI.Label(new Rect(20, 20, 800, 30), "No hay PodReceiver en la escena.", textStyle);
            return;
        }

        // IMGUI runs OnGUI once to lay out and again to draw, and both passes must make the
        // same controls - the pod's state (from the receive thread) or the focus can change in
        // between, which aborted the frame ("Getting control N's position in a group with only
        // N controls") and threw away clicks. So whatever decides which lines exist is read
        // once, at the layout pass, and reused by the others.
        if (Event.current.type == EventType.Layout)
        {
            guiState = PodState();
            guiHasRaw = receiver.TryGetRawRotation(podId, out guiRaw);
            guiFocused = Application.isFocused;
        }

        int state = guiState;
        string stateText = state == 2 ? "<color=#7fe07f>con lecturas del IMU</color>"
            : state == 1 ? "<color=#ffaa33>conectado, sin lectura del IMU</color>"
            : "<color=#999999>sin datos</color>";

        GUILayout.BeginArea(new Rect(20, 16, Mathf.Min(Screen.width - 40, 760), Screen.height - 32));
        GUILayout.Label($"<b><size=22>Pod {podId}</size></b>  {stateText}   ·   conectados según el receptor: {receiver.connectedPods} de {receiver.targetPods}", textStyle);
        GUILayout.Label($"Paquetes por segundo: <b>{packetsPerSecond:F1}</b>", textStyle);

        if (guiHasRaw)
        {
            Vector4 raw = guiRaw;
            Quaternion converted = receiver.ToUnityRotation(raw);
            GUILayout.Label($"Crudo (BNO085 i, j, k, real): <b>{Format(raw)}</b>", textStyle);
            GUILayout.Label($"Convertido a Unity (x, y, z, w): <b>{Format(converted)}</b>   euler <b>{FormatEuler(converted)}</b>", textStyle);
            GUILayout.Label(!zeroed ? "Caja: sin poner en cero (muestra la rotación convertida tal cual)"
                : $"Caja ({(headingSet ? "cero completo" : "solo paso 1")}, hace {Time.time - zeroedAt:F0} s): euler <b>{FormatEuler(box.localRotation)}</b>", textStyle);
        }
        else
        {
            GUILayout.Label("Crudo: --   Convertido: --", textStyle);
        }

        GUILayout.Label("Precisión (acc): <b>--</b>", textStyle);
        GUILayout.Label("Los pods no la envían: el game rotation vector del BNO085 no trae estimación de precisión.", smallStyle);
        GUILayout.Space(6);
        GUILayout.Label($"Conversión: <b><size=20>{receiver.conversion}</size></b>   ·   espejo X: {YesNo(receiver.flipX)}   Y: {YesNo(receiver.flipY)}   Z: {YesNo(receiver.flipZ)}", textStyle);
        GUILayout.Label("<b>Z</b> cero paso 1   ·   <b>X</b> paso 2   ·   <b>C</b> conversión   ·   <b>1 / 2 / 3</b> espejo X / Y / Z   (o los botones en el Inspector de PodAxisTest)", textStyle);
        // The Input System drops the keyboard while the Game view doesn't have focus
        // (editorInputBehaviorInPlayMode = PointersAndKeyboardsRespectGameViewFocus).
        if (!guiFocused)
            GUILayout.Label("<color=#ffaa33><b>La vista Game no tiene el foco: las teclas no llegan.</b> Haz clic dentro de la vista Game, o usa los botones del Inspector.</color>", textStyle);
        // One line in every state, so the layout never changes with it.
        string next = !zeroed
            ? "<b>Paso 1:</b> pon el pod plano, cara de arriba hacia arriba, el extremo del <b>USB a la derecha</b>, y presiona <b>Z</b>."
            : !headingSet
                ? "<b>Paso 2:</b> sin girarlo hacia los lados, levanta el extremo del <b>USB</b> hasta que apunte <b>hacia arriba</b> (90°) y presiona <b>X</b>."
                : "<b>Listo:</b> gira el pod como quieras; la caja debe girar igual. Para repetir, vuelve al paso 1 (Z).";
        GUILayout.Label($"<color=#9fd0ff>{next}</color>", textStyle);
        if (state == 0)
            GUILayout.Label("Sin datos: revisa que bridge.py esté corriendo (desde Bridge/) y que el pod esté prendido.", smallStyle);
        GUILayout.EndArea();

        // Labels for the fixed world axes.
        Camera cam = Camera.main;
        if (cam == null)
            return;

        DrawAxisLabel(cam, axesOrigin + Vector3.right * 0.36f, "+X", AxisXColor);
        DrawAxisLabel(cam, axesOrigin + Vector3.up * 0.36f, "+Y", AxisYColor);
        DrawAxisLabel(cam, axesOrigin + Vector3.forward * 0.36f, "+Z (adentro)", AxisZColor);

        // Name the orange end, so it's clear which end of the real pod it is.
        if (box.gameObject.activeSelf)
            DrawAxisLabel(cam, box.TransformPoint(new Vector3(PodSizeMeters.x * BoxScale / 2f + 0.07f, 0f, 0f)), "USB", UsbColor);
    }

    void DrawAxisLabel(Camera cam, Vector3 world, string text, Color color)
    {
        Vector3 screen = cam.WorldToScreenPoint(world);
        if (screen.z <= 0f)
            return;

        axisLabelStyle.normal.textColor = color;
        GUI.Label(new Rect(screen.x - 10f, Screen.height - screen.y - 10f, 160f, 24f), text, axisLabelStyle);
    }
}
