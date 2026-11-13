using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// The director's app on a PC without a headset (the "desktop director" of the pipeline: mainly
// to review takes and edit the haptic timeline with a mouse). Bootstrap adds it in the director's
// build; it turns itself on in a PC player when no headset is active a moment after start, or
// always with "-escritorio" on the command line (then XR is stopped even if a headset is
// plugged in). In the Editor only on request (SessionState "Tesis.Escritorio"), since the XR
// Device Simulator already stands in for the headset there.
//
// What it does instead of the headset:
//   the view    right mouse button + move = look around; WASD = move; Q / E = down / up;
//               Shift = faster. It starts at eye height on each take's start, like the headset.
//   the panels  plain mouse clicks and hover on every world-space panel (each canvas gets a
//               GraphicRaycaster and this camera); the wheel menu too.
//   the buttons the controllers' actions get keys, found by name among the enabled input
//               actions: Esc / Tab menu, R record, N next take (escena completa), Space play /
//               pause, V the viewfinder, arrows its lens and zoom, T voice note.
//   the viewfinder held in front of the view (the right controller is placed there), so V
//               frames whatever the director is looking at.
// The controllers' models and rays are hidden. A help line at the bottom; H hides it.
public class DesktopDirector : MonoBehaviour
{
    public static bool Active { get; private set; }

    const float EyeHeight = 1.6f;
    // The panels sit below eye level, as for a headset (replay at ~25° down): on a flat screen the
    // view starts tilted down a little, with a wider field of view, so they're in it.
    const float StartPitch = 15f;
    const float FieldOfView = 75f;
    const float LookSensitivity = 0.12f;   // degrees per pixel
    const float MoveSpeed = 1.4f;          // m/s
    const float FastFactor = 3f;
    const float ScanSeconds = 0.5f;
    static readonly Vector3 ViewfinderHold = new Vector3(0.12f, -0.12f, 0.35f);

    static readonly Dictionary<string, string[]> Keys = new Dictionary<string, string[]>
    {
        { "PauseMenuToggle", new[] { "<Keyboard>/escape", "<Keyboard>/tab" } },
        { "RecordingShortcut", new[] { "<Keyboard>/r" } },
        { "SiguienteToma", new[] { "<Keyboard>/n" } },
        { "PlaybackPlayPause", new[] { "<Keyboard>/space" } },
        { "DirectorViewfinderToggle", new[] { "<Keyboard>/v" } },
        { "VoiceNote", new[] { "<Keyboard>/t" } },
    };

    Camera view;
    XROrigin rig;
    float yaw, pitch;
    bool placePending = true;
    float nextScan;
    readonly HashSet<InputAction> bound = new HashSet<InputAction>();
    GameObject help;
    float decideAt;
    bool decided;

    void Start()
    {
        decideAt = Time.realtimeSinceStartup + 1.5f;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode) => placePending = true;

    // On or off, once: a moment after start, when XR has had time to come up.
    void Decide()
    {
        decided = true;
        bool forced = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-escritorio") >= 0;
#if UNITY_EDITOR
        forced |= UnityEditor.SessionState.GetBool("Tesis.Escritorio", false);
        bool headset = !forced;
#else
        bool headset = UnityEngine.XR.XRSettings.isDeviceActive;
#endif
        if (!forced && headset)
        {
            enabled = false;
            return;
        }

        if (forced)
            StopXR();
        Active = true;
        Debug.Log("[Escritorio] desktop director: no headset, mouse and keyboard");
        BuildHelp();
    }

    static void StopXR()
    {
        var settings = UnityEngine.XR.Management.XRGeneralSettings.Instance;
        if (settings == null || settings.Manager == null || settings.Manager.activeLoader == null)
            return;
        settings.Manager.StopSubsystems();
        settings.Manager.DeinitializeLoader();
    }

    void Update()
    {
        if (!decided)
        {
            if (Time.realtimeSinceStartup < decideAt)
                return;
            Decide();
            if (!Active)
                return;
        }

        if (view == null || !view.isActiveAndEnabled || rig == null)
            FindView();
        if (view == null)
            return;

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + ScanSeconds;
            // Every scan, not once: the XR rig switches interactors back on (the viewfinder's
            // teleport pause, the rig's input manager).
            WakeControllers();
            HideControllers();
            PrepareCanvases();
            BindKeys();
#if DIRECTOR_BUILD
            if (DirectorViewfinderController.Instance != null)
                DirectorViewfinderController.Instance.EnsureBuilt();
#endif
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.hKey.wasPressedThisFrame && help != null)
            help.SetActive(!help.activeSelf);
    }

    void LateUpdate()
    {
        if (!Active || view == null)
            return;

        if (placePending)
        {
            placePending = false;
            PlaceAtRig();
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            Look(delta.x * LookSensitivity, -delta.y * LookSensitivity);
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && !TypingIntoField())
        {
            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += Vector3.forward;
            if (keyboard.sKey.isPressed) move += Vector3.back;
            if (keyboard.aKey.isPressed) move += Vector3.left;
            if (keyboard.dKey.isPressed) move += Vector3.right;
            if (keyboard.eKey.isPressed) move += Vector3.up;
            if (keyboard.qKey.isPressed) move += Vector3.down;
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            Move(move, Time.unscaledDeltaTime * (fast ? FastFactor : 1f));
        }

        view.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        HoldViewfinder();
    }

    static bool TypingIntoField()
    {
        var selected = UnityEngine.EventSystems.EventSystem.current != null ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null);
    }

    // degrees right and up
    public void Look(float right, float up)
    {
        yaw += right;
        pitch = Mathf.Clamp(pitch - up, -85f, 85f);
    }

    // direction: x right, y up, z forward, relative to where the view faces (flat)
    public void Move(Vector3 direction, float seconds)
    {
        if (direction.sqrMagnitude < 0.0001f)
            return;
        Vector3 world = Quaternion.Euler(0f, yaw, 0f) * new Vector3(direction.x, 0f, direction.z) + Vector3.up * direction.y;
        view.transform.position += world.normalized * (MoveSpeed * seconds);
    }

    void FindView()
    {
        rig = FindFirstObjectByType<XROrigin>();
        view = rig != null && rig.Camera != null ? rig.Camera : Camera.main;
        if (view == null)
            return;

        // The headset's pose driver would hold the camera where the (absent) headset is.
        foreach (Behaviour driver in view.GetComponents<Behaviour>())
        {
            if (driver.GetType().Name.Contains("TrackedPoseDriver"))
                driver.enabled = false;
        }
        WakeControllers();
        HideControllers();
        placePending = true;
    }

    // With no headset the rig's XRInputModalityManager sees no tracked controller and switches
    // the controller objects off - and the viewfinder hangs on the right one (the recording
    // shortcut's buzz too). Stop it and keep them on (hidden, placed by HoldViewfinder).
    // True if any was off.
    static bool WakeControllers()
    {
        bool woke = false;
        foreach (XRInputModalityManager manager in FindObjectsByType<XRInputModalityManager>(FindObjectsSortMode.None))
        {
            manager.enabled = false;
            foreach (GameObject controller in new[] { manager.leftController, manager.rightController })
            {
                if (controller != null && !controller.activeSelf)
                {
                    controller.SetActive(true);
                    woke = true;
                }
            }
        }
        return woke;
    }

    // Eye height over the rig, facing the way it faces (a take's start, after ShotStartPositioner).
    void PlaceAtRig()
    {
        Transform origin = rig != null ? rig.transform : view.transform.parent;
        if (origin == null)
            return;
        view.transform.position = origin.position + Vector3.up * EyeHeight;
        yaw = origin.eulerAngles.y;
        pitch = Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) ? StartPitch : 0f;
        view.fieldOfView = FieldOfView;
    }

    void HideControllers()
    {
        foreach (string name in new[] { "Left Controller", "Right Controller" })
        {
            GameObject controller = GameObject.Find(name);
            if (controller == null)
                continue;
            foreach (Renderer r in controller.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
            foreach (Behaviour b in controller.GetComponentsInChildren<Behaviour>(true))
            {
                // Their rays too: the interactors (they'd hover and catch the panels in front of
                // the view, and the mouse clicks with them) and the curve visuals that turn the
                // line renderers back on.
                string type = b.GetType().Name;
                if (type.Contains("TrackedPoseDriver") || type.Contains("LineVisual") || type.Contains("CurveVisual") || b is XRBaseInteractor)
                    b.enabled = false;
            }
        }
    }

    // The viewfinder follows the right controller: keep it in front of the view.
    void HoldViewfinder()
    {
        GameObject right = GameObject.Find("Right Controller");
        if (right == null)
            return;
        right.transform.SetPositionAndRotation(view.transform.TransformPoint(ViewfinderHold), view.transform.rotation);
    }

    void PrepareCanvases()
    {
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
        {
            if (canvas.renderMode != RenderMode.WorldSpace || !canvas.isRootCanvas)
                continue;
            canvas.worldCamera = view;
            if (canvas.GetComponent<GraphicRaycaster>() == null)
                canvas.gameObject.AddComponent<GraphicRaycaster>();
        }
    }

    void BindKeys()
    {
        foreach (InputAction action in InputSystem.ListEnabledActions())
        {
            if (bound.Contains(action))
                continue;
            if (action.name == "DirectorViewfinderZoom")
            {
                action.Disable();
                action.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                    .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
                action.Enable();
                bound.Add(action);
                continue;
            }
            if (!Keys.TryGetValue(action.name, out string[] keys))
                continue;
            action.Disable();
            foreach (string key in keys)
                action.AddBinding(key);
            action.Enable();
            bound.Add(action);
            Debug.Log($"[Escritorio] {action.name} also on {string.Join(", ", keys)}");
        }
        bound.RemoveWhere(a => a == null);
    }

    void BuildHelp()
    {
        help = new GameObject("DesktopHelp", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        DontDestroyOnLoad(help);
        Canvas canvas = help.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        CanvasScaler scaler = help.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 900f);

        var strip = new GameObject("Strip", typeof(RectTransform), typeof(Image));
        strip.transform.SetParent(help.transform, false);
        var stripRect = (RectTransform)strip.transform;
        stripRect.anchorMin = new Vector2(0f, 0f);
        stripRect.anchorMax = new Vector2(1f, 0f);
        stripRect.pivot = new Vector2(0.5f, 0f);
        stripRect.sizeDelta = new Vector2(0f, 54f);
        Image stripImage = strip.GetComponent<Image>();
        stripImage.color = new Color(0f, 0f, 0f, 0.55f);
        stripImage.raycastTarget = false;

        TextMeshProUGUI text = GlassUI.Label(help.transform, "Escritorio · clic derecho + mouse: mirar · WASD: moverte · Q / E: bajar / subir · Shift: rápido · clic: botones · " +
            "Esc: menú · R: grabar · N: siguiente toma · Espacio: reproducir · V: visor (flechas: lente y zoom) · T: nota de voz · H: ocultar esta ayuda",
            16f, false, new Color(1f, 1f, 1f, 0.85f), TextAlignmentOptions.BottomLeft);
        var rect = (RectTransform)text.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.offsetMin = new Vector2(16f, 10f);
        rect.offsetMax = new Vector2(-16f, 44f);
        text.enableWordWrapping = true;
        text.raycastTarget = false;
    }
}
