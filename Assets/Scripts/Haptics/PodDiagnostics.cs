using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Diagnostics only - the PodDiagnostics scene, played on its own (no Bootstrap, no XR rig,
// no Normcore). Its job is to settle which of PodReceiver.ToUnity()'s four conversions is
// right: hold a pod, rotate it, and watch its cube.
//
// - One cube per pod sending IMU readings, in a row by pod ID (1..9, the receiver's
//   POD_01..POD_09), so all 9 fit on screen. Each cube follows PodReceiver.GetRotation(id).
//   The label "Pod N" always stays, so the row never shifts: white = readings, orange =
//   connected but no IMU reading (the pod sends an all-zero quaternion), grey = no data.
//   Any ID outside 1..9 gets a slot of its own at the right end.
// - The cubes carry three axis sticks - red +X, green +Y, blue +Z - since a plain cube looks
//   the same from too many sides to tell a mirrored or swapped axis.
// - C cycles PodReceiver.conversion 0-3 in Play mode, also from the on-screen button.
//   PodReceiver converts on read, so every cube changes at once.
// - Buttons fire PodReceiver.CueCameraStart() and CueCut() - both broadcast to every pod.
//
// On-screen text is IMGUI: this is a desktop tool, not part of the app's world-space XR UI.
public class PodDiagnostics : MonoBehaviour
{
    const int FirstPodId = 1;
    const int PodCount = 9;
    const int ConversionCount = 4; // the cases in PodReceiver.ToUnity()

    public PodReceiver receiver;

    [Header("Layout")]
    public float spacing = 0.6f;
    public float cubeSize = 0.3f;

    static readonly Color CubeColor = new Color(0.85f, 0.85f, 0.88f);
    static readonly Color AxisXColor = new Color(0.9f, 0.2f, 0.2f);
    static readonly Color AxisYColor = new Color(0.2f, 0.8f, 0.25f);
    static readonly Color AxisZColor = new Color(0.25f, 0.45f, 1f);

    readonly SortedDictionary<int, Transform> cubes = new SortedDictionary<int, Transform>();
    int nextExtraSlot = PodCount;
    readonly Dictionary<int, float> slotX = new Dictionary<int, float>();

    InputAction cycleConversionAction;
    string lastSent = "";
    GUIStyle textStyle;
    GUIStyle bigStyle;
    GUIStyle labelStyle;

    // Read at the IMGUI layout pass and reused by the other passes - see OnGUI.
    int[] guiActive = new int[0];
    readonly List<int> guiNoImu = new List<int>();
    bool guiShowSent;

    void Awake()
    {
        if (receiver == null)
            receiver = FindFirstObjectByType<PodReceiver>();
        if (receiver == null)
            Debug.LogError("[PodDiagnostics] no PodReceiver in the scene.");

        cycleConversionAction = new InputAction("CycleConversion", InputActionType.Button, "<Keyboard>/c");
        cycleConversionAction.performed += _ => CycleConversion();
        cycleConversionAction.Enable();

        for (int i = 0; i < PodCount; i++)
            CreateCube(FirstPodId + i, i);
    }

    void OnDestroy()
    {
        cycleConversionAction.Disable();
        cycleConversionAction.Dispose();
    }

    void Update()
    {
        if (receiver == null)
            return;

        // A pod outside 1..9 still gets a cube, at the right end of the row.
        foreach (int id in receiver.ActivePods())
        {
            if (!cubes.ContainsKey(id))
                CreateCube(id, nextExtraSlot++);
        }

        // A cube only for a pod sending real IMU readings; a connected pod without one
        // (all-zero quaternion) keeps just its label, in orange.
        foreach (var pod in cubes)
        {
            bool showing = receiver.HasOrientation(pod.Key);
            pod.Value.gameObject.SetActive(showing);
            if (showing)
                pod.Value.localRotation = receiver.GetRotation(pod.Key);
        }
    }

    void CycleConversion()
    {
        if (receiver == null)
            return;

        receiver.conversion = (receiver.conversion + 1) % ConversionCount;
        Debug.Log($"[PodDiagnostics] conversion = {receiver.conversion}");
    }

    // Slot 0..8 for pods 1..9, centered on x = 0.
    void CreateCube(int podId, int slot)
    {
        float x = (slot - (PodCount - 1) / 2f) * spacing;
        slotX[podId] = x;

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = $"Pod_{podId:00}";
        cube.transform.SetParent(transform, false);
        cube.transform.localPosition = new Vector3(x, 0f, 0f);
        cube.transform.localScale = Vector3.one * cubeSize;
        Destroy(cube.GetComponent<Collider>());
        cube.GetComponent<Renderer>().material.color = CubeColor;

        // Sticks in the cube's own space (scale 1 = the cube's edge), poking out of the +X, +Y
        // and +Z faces.
        CreateAxisStick(cube.transform, "+X", Vector3.right, AxisXColor);
        CreateAxisStick(cube.transform, "+Y", Vector3.up, AxisYColor);
        CreateAxisStick(cube.transform, "+Z", Vector3.forward, AxisZColor);

        cubes[podId] = cube.transform;
        cube.SetActive(false);
    }

    static void CreateAxisStick(Transform cube, string name, Vector3 axis, Color color)
    {
        const float Length = 0.9f;
        const float Thickness = 0.12f;

        var stick = GameObject.CreatePrimitive(PrimitiveType.Cube);
        stick.name = name;
        stick.transform.SetParent(cube, false);
        stick.transform.localPosition = axis * (0.5f + Length / 2f);
        stick.transform.localScale = new Vector3(
            axis.x != 0f ? Length : Thickness,
            axis.y != 0f ? Length : Thickness,
            axis.z != 0f ? Length : Thickness);
        Destroy(stick.GetComponent<Collider>());
        stick.GetComponent<Renderer>().material.color = color;
    }

    void SendCameraStart()
    {
        receiver.CueCameraStart();
        lastSent = $"Inicio de cámara (TRIPLE a todos) - {Time.time:F1}s";
        Debug.Log("[PodDiagnostics] CueCameraStart()");
    }

    void SendCut()
    {
        receiver.CueCut();
        lastSent = $"Corte (LONG a todos) - {Time.time:F1}s";
        Debug.Log("[PodDiagnostics] CueCut()");
    }

    void OnGUI()
    {
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, richText = true, wordWrap = true };
            textStyle.normal.textColor = Color.white;
            bigStyle = new GUIStyle(textStyle) { fontSize = 26 };
            labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, richText = true, clipping = TextClipping.Overflow };
            labelStyle.normal.textColor = Color.white;
        }

        if (receiver == null)
        {
            GUI.Label(new Rect(20, 20, 800, 30), "No hay PodReceiver en la escena.", textStyle);
            return;
        }

        // IMGUI runs OnGUI once to lay out and again for each event (mouse, repaint), and every
        // pass must make the same controls. The pods' state comes from the receive thread and
        // a button click sets lastSent mid-frame, so the optional lines used to differ between
        // passes: the frame aborted ("Getting control N's position in a group with only N
        // controls") and the click was lost. So what decides which lines exist is read once,
        // at the layout pass, and reused by the others.
        if (Event.current.type == EventType.Layout)
        {
            guiActive = receiver.ActivePods();
            System.Array.Sort(guiActive);
            guiNoImu.Clear();
            foreach (int id in guiActive)
                if (!receiver.HasOrientation(id))
                    guiNoImu.Add(id);
            guiShowSent = lastSent.Length > 0;
        }

        int[] active = guiActive;
        string alive = active.Length > 0 ? string.Join(", ", active) : "ninguno";
        List<int> noImu = guiNoImu;

        // As wide as the window allows, so nothing runs off a small Game view.
        GUILayout.BeginArea(new Rect(20, 20, Mathf.Min(Screen.width - 40, 720), Screen.height / 2f));
        GUILayout.Label($"Conectados (según el receptor): <b>{receiver.connectedPods}</b> de <b>{receiver.targetPods}</b>", textStyle);
        GUILayout.Label($"Con datos ahora: <b>{alive}</b>", textStyle);
        if (noImu.Count > 0)
            GUILayout.Label($"<color=#ffaa33>Conectados pero sin lectura del IMU: <b>{string.Join(", ", noImu)}</b></color>", textStyle);
        GUILayout.Label($"Conversión: <b>{receiver.conversion}</b>", bigStyle);
        GUILayout.Label("Presiona <b>C</b> para cambiar (0-3).", textStyle);
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Cambiar conversión", GUILayout.Height(36)))
            CycleConversion();
        if (GUILayout.Button("Inicio de cámara", GUILayout.Height(36)))
            SendCameraStart();
        if (GUILayout.Button("Corte", GUILayout.Height(36)))
            SendCut();
        GUILayout.EndHorizontal();

        if (guiShowSent)
            GUILayout.Label($"Enviado: {lastSent}", textStyle);
        GUILayout.Space(8);
        GUILayout.Label("Ejes del cubo: <color=#e63333>rojo +X</color>, <color=#33cc40>verde +Y</color>, <color=#4073ff>azul +Z</color>", textStyle);
        if (active.Length == 0)
            GUILayout.Label("Sin datos: revisa que bridge.py esté corriendo (desde Bridge/).", textStyle);
        GUILayout.EndArea();

        // Pod ID under each slot, whether or not its cube is showing.
        Camera cam = Camera.main;
        if (cam == null)
            return;

        // Sized to the gap between two slots on screen, so the labels never run into each
        // other however small the window is. White = IMU readings, orange = connected without
        // an IMU reading, grey = no data.
        float slotPixels = Mathf.Abs(cam.WorldToScreenPoint(transform.TransformPoint(Vector3.right * spacing)).x
                                     - cam.WorldToScreenPoint(transform.position).x);
        labelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(slotPixels * 0.28f), 10, 24);

        foreach (var pod in cubes)
        {
            Vector3 under = transform.TransformPoint(new Vector3(slotX[pod.Key], -cubeSize * 2.2f, 0f));
            Vector3 screen = cam.WorldToScreenPoint(under);
            if (screen.z <= 0f)
                continue;

            string text = receiver.HasOrientation(pod.Key) ? $"<b>Pod {pod.Key}</b>"
                : receiver.IsAlive(pod.Key) ? $"<color=#ffaa33><b>Pod {pod.Key}</b>\nsin IMU</color>"
                : $"<color=#777777>Pod {pod.Key}</color>";
            if (receiver.TryGetHealth(pod.Key, out PodReceiver.PodHealth health))
            {
                string label = health.ToLabel();
                if (label.Length > 0)
                    text += $"\n{label}";
            }
            GUI.Label(new Rect(screen.x - slotPixels / 2f, Screen.height - screen.y, slotPixels, labelStyle.fontSize * 3f), text, labelStyle);
        }
    }
}
