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
// - Z puts it in zero: the box shows the rotation since that moment, box = q * Inverse(q0),
//   so it turns about the same world axes as the pod. The zero is kept as the raw sample and
//   converted again each frame, so it survives changing the conversion or the flips.
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

    long rateCount;
    float rateStart;
    float packetsPerSecond;
    float lastRateLog;

    // Last state, so each change is logged once.
    int loggedPodId = -1;
    int loggedState = -1;
    int loggedConversion = -1;
    bool loggedFlipX, loggedFlipY, loggedFlipZ;

    InputAction zeroAction, conversionAction, flipXAction, flipYAction, flipZAction;
    GUIStyle textStyle, smallStyle, axisLabelStyle;

    void Awake()
    {
        if (receiver == null)
            receiver = FindFirstObjectByType<PodReceiver>();
        if (receiver == null)
            Debug.LogError("[PodAxisTest] no PodReceiver in the scene.");

        zeroAction = Bind("ZeroPod", "<Keyboard>/z", Zero);
        conversionAction = Bind("CycleConversion", "<Keyboard>/c", CycleConversion);
        flipXAction = Bind("FlipX", "<Keyboard>/1", () => ToggleFlip(0));
        flipYAction = Bind("FlipY", "<Keyboard>/2", () => ToggleFlip(1));
        flipZAction = Bind("FlipZ", "<Keyboard>/3", () => ToggleFlip(2));

        BuildBox();
        BuildWorldAxes();
        Debug.Log($"[PodAxisTest] started: following pod {podId}. Z zero, C conversion, 1/2/3 flip X/Y/Z.");
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
        foreach (InputAction action in new[] { zeroAction, conversionAction, flipXAction, flipYAction, flipZAction })
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

    void Zero()
    {
        if (receiver == null || !receiver.TryGetRawRotation(podId, out Vector4 raw))
        {
            Debug.Log($"[PodAxisTest] Z ignored: pod {podId} has no IMU reading yet.");
            return;
        }

        zeroed = true;
        zeroRaw = raw;
        zeroedAt = Time.time;
        Debug.Log($"[PodAxisTest] pod {podId} zeroed at raw {Format(raw)} (converted {Format(receiver.ToUnityRotation(raw))})");
    }

    void CycleConversion()
    {
        if (receiver != null)
            receiver.conversion = (receiver.conversion + 1) % ConversionCount;
    }

    void ToggleFlip(int axis)
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
        box.localRotation = zeroed
            ? current * Quaternion.Inverse(receiver.ToUnityRotation(zeroRaw))
            : current;
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

        int state = PodState();
        string stateText = state == 2 ? "<color=#7fe07f>con lecturas del IMU</color>"
            : state == 1 ? "<color=#ffaa33>conectado, sin lectura del IMU</color>"
            : "<color=#999999>sin datos</color>";

        GUILayout.BeginArea(new Rect(20, 16, Mathf.Min(Screen.width - 40, 760), Screen.height - 32));
        GUILayout.Label($"<b><size=22>Pod {podId}</size></b>  {stateText}   ·   conectados según el receptor: {receiver.connectedPods} de {receiver.targetPods}", textStyle);
        GUILayout.Label($"Paquetes por segundo: <b>{packetsPerSecond:F1}</b>", textStyle);

        if (receiver.TryGetRawRotation(podId, out Vector4 raw))
        {
            Quaternion converted = receiver.ToUnityRotation(raw);
            GUILayout.Label($"Crudo (BNO085 i, j, k, real): <b>{Format(raw)}</b>", textStyle);
            GUILayout.Label($"Convertido a Unity (x, y, z, w): <b>{Format(converted)}</b>   euler <b>{FormatEuler(converted)}</b>", textStyle);
            GUILayout.Label(zeroed
                ? $"Caja (desde el cero, hace {Time.time - zeroedAt:F0} s): euler <b>{FormatEuler(box.localRotation)}</b>"
                : "Caja: sin poner en cero (muestra la rotación convertida tal cual)", textStyle);
        }
        else
        {
            GUILayout.Label("Crudo: --   Convertido: --", textStyle);
        }

        GUILayout.Label("Precisión (acc): <b>--</b>", textStyle);
        GUILayout.Label("Los pods no la envían: el game rotation vector del BNO085 no trae estimación de precisión.", smallStyle);
        GUILayout.Space(6);
        GUILayout.Label($"Conversión: <b><size=20>{receiver.conversion}</size></b>   ·   espejo X: {YesNo(receiver.flipX)}   Y: {YesNo(receiver.flipY)}   Z: {YesNo(receiver.flipZ)}", textStyle);
        GUILayout.Label("<b>Z</b> poner en cero   ·   <b>C</b> cambiar conversión   ·   <b>1 / 2 / 3</b> espejo X / Y / Z", textStyle);
        GUILayout.Label("Pon el pod como la caja (plano, cara de arriba hacia arriba, USB a la derecha) y presiona Z. Luego gíralo: la caja debe girar igual.", smallStyle);
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
