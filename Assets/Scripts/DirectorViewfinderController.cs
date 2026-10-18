using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if DIRECTOR_BUILD
using System.Collections;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
#endif

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Director-only "virtual camera":
// a small camera rigidly parented to the right controller, rendering to a RenderTexture
// shown on a handheld world-space panel (like checking a phone screen), toggled
// visible/hidden with a tap of the right controller's Grip button.
//
// Zoom uses real cinema vocabulary, two controls that complement rather than replace each
// other:
// - Joystick left/right (not held) cycles through named focal lengths - 24/35/50/85/135mm,
//   the standard lens-kit spread - each mapped to its physical Field of View via Unity's
//   own physical camera model (not a hand-rolled formula): a 36x24mm full-frame sensor and
//   the camera's focalLength, with fieldOfView derived automatically. See ApplyFocalLength.
// - Holding Grip down while moving the joystick up/down fine-tunes the focal length
//   continuously around whatever the current value is (named or already fine-tuned),
//   instead of jumping between the named stops. Releasing Grip after fine-tuning does NOT
//   toggle the panel - only a plain tap (no joystick movement during the hold) does. See
//   OnGripStarted/OnGripReleased and UpdateZoom.
// The current value (e.g. "50mm", or "47mm" mid fine-tune) is always shown live on the
// panel - see UpdateFocalLengthLabel.
//
// The feed also has rule-of-thirds and 16:9 framing guide lines overlaid directly on the
// Canvas (BuildFramingOverlay) - viewfinder-only, not baked into the rendered feed itself.
//
// The panel is display-only - no buttons, no raycaster. It used to have a framing button bar
// (save / load a framing), removed because the right controller's poke interactor, right
// under the panel, kept pressing it by accident.
//
// Right hand because both controllers carry the identical stock XRI interactors, and the
// right one is the less loaded overall - the left hand already carries Menu, Play/Pause
// (Inspección de Escenas), and the free-fly joystick.
// Grip was unused on both hands, but the right joystick is normally Turn/snap-turn (stock
// XRI locomotion) - disabled for as long as the panel is visible (see PauseTurnLocomotion),
// the same way PauseMenuController already suspends Move/Turn while its own panel is open.
//
// Only active during Ensayo Remoto - a content scene, director build, IsInspecting false.
// Never during Inspección de Escenas or any menu.
public class DirectorViewfinderController : MonoBehaviour
{
    const int RenderTextureWidth = 640;
    const int RenderTextureHeight = 480;

    // Added as a project user layer (index 8) via the Editor - see BuildCamera/BuildPanel
    // for why: without it, the viewfinder camera recursively renders its own display panel.
    const string PanelLayerName = "DirectorViewfinderPanel";

    // Added as a project user layer (index 9) via the Editor - the XR rig's controller ray
    // (its LineRenderers - Near-Far and Teleport, stock XRI) is on Default by default, so it showed up
    // as a flickering streak inside the viewfinder's own feed too. Moving it to its own
    // layer lets the viewfinder camera exclude just the ray, independently of the panel
    // layer above - every other camera (including the director's own main camera, whose
    // culling mask is "Everything") still renders it completely normally.
    const string RayVisualLayerName = XRUISetup.ControllerRayLayerName;

    // RecordingPlaybackController's recorded-camera monitor layer (index 10) - excluded too,
    // in case "Ver replay" puts the monitor up while the viewfinder is live.
    const string PlaybackMonitorLayerName = "PlaybackMonitor";

    // Placeholder offset, not tuned - positions the panel roughly like a phone screen held
    // up close to the hand. Only a position now (see UpdatePanelFacing) - rotation is
    // computed live every frame instead of guessed as a fixed local angle. The panel was
    // doubled from 9cm to 18cm wide (the feed was too small to judge framing), and y keeps
    // the feed exactly where it sat when the panel still had its button bar underneath.
    static readonly Vector3 PanelLocalPosition = new Vector3(0f, 0.075f, 0.05f);
    const float PanelWidth = 0.18f;
    const float PanelHeight = 0.135f; // 4:3, matches the RenderTexture's aspect ratio

    // Standard lens-kit spread, in mm - joystick left/right cycles through these. FOV for
    // each is derived by Unity's own physical camera model (see ApplyFocalLength), not a
    // hand-rolled formula - a 35mm full-frame sensor (36x24mm) is the reference, same as
    // these names mean in real photography/cinema.
    static readonly float[] NamedFocalLengthsMM = { 24f, 35f, 50f, 85f, 135f };
    const float DefaultFocalLengthMM = 50f; // "normal" lens - sensible starting point
    const float MinFocalLengthMM = 24f;
    const float MaxFocalLengthMM = 135f;

    // Joystick X, not holding Grip: discrete cycling through NamedFocalLengthsMM. Edge-
    // triggered with hysteresis (trigger past CycleTriggerThreshold, don't re-arm until back
    // under CycleRearmThreshold) so one flick is one step, not a repeat-fire while held.
    const float CycleTriggerThreshold = 0.6f;
    const float CycleRearmThreshold = 0.25f;

    // Joystick Y, while holding Grip: continuous fine-tune around the current value.
    const float FineTuneSpeedMMPerSecond = 40f; // full 111mm range in ~2.8s at full deflection
    const float FineTuneDeadzone = 0.15f;

    const int FocalLengthLabelFontSize = 24;
    const float FocalLengthLabelHeight = 44f;
    static readonly Vector2 FocalLengthLabelMargin = new Vector2(10f, 10f);

    const float ThirdsLineThickness = 2f;
    const float AspectMarkerThickness = 3f;
    const float TargetAspectRatio = 16f / 9f;
    static readonly Color ThirdsLineColor = new Color(1f, 1f, 1f, 0.5f);
    static readonly Color AspectMarkerColor = new Color(1f, 0.85f, 0.2f, 0.65f);
    static readonly Color FocalLengthLabelBackgroundColor = new Color(0f, 0f, 0f, 0.55f);

    // "● REC" badge, top-left of the feed while RehearsalRecorder is recording - drawn on the
    // panel like a real camera's screen, so it never ends up in the rendered image itself.
    const int RecBadgeFontSize = 28;
    static readonly Vector2 RecBadgeSize = new Vector2(150f, 48f);
    static readonly Vector2 RecBadgeMargin = new Vector2(10f, -10f);
    static readonly Color RecBadgeBackgroundColor = new Color(0f, 0f, 0f, 0.55f);
    static readonly Color RecBadgeTextColor = new Color(1f, 0.15f, 0.15f, 1f);

    static readonly Color PanelTextColor = new Color(0.95f, 0.95f, 0.98f, 1f);

#if DIRECTOR_BUILD
    const int FindControllerMaxAttempts = 30; // ~0.5s at 60fps

    InputAction gripToggleAction;
    InputAction zoomAction;
    Font uiFont;
    GameObject viewfinderRoot;
    Transform panelTransform;
    Transform rightControllerTransform;
    Camera viewfinderCamera;
    RenderTexture renderTexture;
    Coroutine buildCoroutine;
    GameObject disabledTurnObject;
    ControllerInputActionManager pausedRightInputManager;
    NearFarInteractor rightNearFarInteractor;
    Text focalLengthLabel;
    GameObject recBadge;
    float currentFocalLengthMM;
    bool isVisible;

    // Grip is now a dual-purpose control: a plain tap toggles the panel, but holding it
    // down while moving the joystick fine-tunes zoom instead - see OnGripStarted/
    // OnGripReleased/UpdateZoom. didFineTuneDuringGripHold is what tells OnGripReleased
    // whether this particular hold was "a tap" or "a zoom adjustment", so it only toggles
    // on the former.
    bool isGripHeld;
    bool didFineTuneDuringGripHold;

    // Hysteresis state for the joystick-X discrete cycling - see CycleTriggerThreshold/
    // CycleRearmThreshold.
    bool cycleArmed = true;

    // Read by the tutorial to tell when the director raised the viewfinder or zoomed.
    public bool IsVisible => isVisible;
    public float CurrentFocalLengthMM => currentFocalLengthMM;
    public static bool IsNamedFocalLength(float mm) => Mathf.Abs(NamedFocalLengthsMM[FindClosestNamedIndex(mm)] - mm) < 0.5f;
#endif

    public static DirectorViewfinderController Instance { get; private set; }

    // Current state of the virtual camera for RehearsalRecorder, which samples it on the same
    // tick as the actor. World-space pose of the camera itself. Works while the panel is
    // hidden too: the camera's transform keeps following the hand even when its GameObject
    // is inactive, so a take where the viewfinder is never raised still gets real samples
    // (visorActive false).
    // Returns false only when no viewfinder camera exists right now (e.g. outside Ensayo
    // Remoto, or the first frames of a scene before it's built).
    public bool TryGetCameraSample(out Vector3 position, out Quaternion rotation, out float focalLengthMM, out bool visorActive)
    {
#if DIRECTOR_BUILD
        if (viewfinderCamera != null)
        {
            Transform cameraTransform = viewfinderCamera.transform;
            position = cameraTransform.position;
            rotation = cameraTransform.rotation;
            focalLengthMM = currentFocalLengthMM;
            visorActive = isVisible;
            return true;
        }
#endif
        position = Vector3.zero;
        rotation = Quaternion.identity;
        focalLengthMM = 0f;
        visorActive = false;
        return false;
    }

    void Awake()
    {
        Instance = this;

#if DIRECTOR_BUILD
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Same curly-brace "usage" binding convention as the recording shortcut and
        // Play/Pause, plus the XR Device Simulator's equivalent for in-Editor testing.
        // started/canceled (press/release), not performed - a tap toggles the panel, but a
        // hold is reserved for fine-tuning zoom (see OnGripStarted/OnGripReleased), so the
        // toggle decision can only be made once the hold is over.
        gripToggleAction = new InputAction("DirectorViewfinderToggle", InputActionType.Button);
        gripToggleAction.AddBinding("<XRController>{RightHand}/{gripButton}");
        gripToggleAction.AddBinding("<XRSimulatedController>{RightHand}/gripButton");
        gripToggleAction.started += OnGripStarted;
        gripToggleAction.canceled += OnGripReleased;
        gripToggleAction.Enable();

        // Same right-hand joystick convention as DirectorFreeFlyCamera's left-hand one
        // (Value/Vector2, real + simulator bindings) - only read while the panel is
        // visible (see Update), so it's harmless that this stays enabled at all times.
        zoomAction = new InputAction("DirectorViewfinderZoom", InputActionType.Value, expectedControlType: "Vector2");
        zoomAction.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
        zoomAction.AddBinding("<XRSimulatedController>{RightHand}/primary2DAxis");
        zoomAction.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        gripToggleAction.started -= OnGripStarted;
        gripToggleAction.canceled -= OnGripReleased;
        gripToggleAction.Disable();
        gripToggleAction.Dispose();

        zoomAction.Disable();
        zoomAction.Dispose();

        SceneManager.sceneLoaded -= OnSceneLoaded;

        if (renderTexture != null)
            renderTexture.Release();
#endif
    }

#if DIRECTOR_BUILD
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Whatever existed belonged to the scene that just unloaded.
        CleanUp();

        if (!Bootstrap.IsContentScene(scene.name) || Bootstrap.IsInspecting)
            return;

        buildCoroutine = StartCoroutine(BuildViewfinderWhenReady());
    }

    void CleanUp()
    {
        if (buildCoroutine != null)
            StopCoroutine(buildCoroutine);
        buildCoroutine = null;

        if (viewfinderRoot != null)
            Destroy(viewfinderRoot);
        viewfinderRoot = null;
        panelTransform = null;
        viewfinderCamera = null;
        rightControllerTransform = null;
        focalLengthLabel = null;
        recBadge = null;

        if (renderTexture != null)
            renderTexture.Release();
        renderTexture = null;

        // The GameObject itself belongs to the scene that just unloaded - already gone,
        // just drop the reference instead of re-enabling it.
        disabledTurnObject = null;
        rightNearFarInteractor = null;
        pausedRightInputManager = null;

        isVisible = false;
        isGripHeld = false;
        didFineTuneDuringGripHold = false;
        cycleArmed = true;
    }

    void Update()
    {
        if (viewfinderRoot == null || !isVisible)
            return;

        UpdateZoom();
        UpdatePanelFacing();
        UpdateRecBadge();
    }

    // Grip held: joystick Y fine-tunes continuously, X is ignored entirely (the whole stick
    // is reserved for fine-tuning while holding). Grip not held: joystick X cycles through
    // the named stops, Y is ignored. The two gestures never mix in the same frame, so
    // there's no ambiguity about which one a given stick movement means.
    void UpdateZoom()
    {
        if (viewfinderCamera == null)
            return;

        Vector2 axis = zoomAction.ReadValue<Vector2>();

        if (isGripHeld)
        {
            if (Mathf.Abs(axis.y) < FineTuneDeadzone)
                return;

            // Stick up = longer focal length = more zoom, like pushing a zoom rocker toward
            // "tele".
            didFineTuneDuringGripHold = true;
            currentFocalLengthMM = Mathf.Clamp(
                currentFocalLengthMM + axis.y * FineTuneSpeedMMPerSecond * Time.deltaTime,
                MinFocalLengthMM, MaxFocalLengthMM);
            ApplyFocalLength();
            return;
        }

        UpdateFocalLengthCycle(axis.x);
    }

    void UpdateFocalLengthCycle(float xAxis)
    {
        if (!cycleArmed)
        {
            if (Mathf.Abs(xAxis) < CycleRearmThreshold)
                cycleArmed = true;
            return;
        }

        if (xAxis > CycleTriggerThreshold)
            StepFocalLength(1);
        else if (xAxis < -CycleTriggerThreshold)
            StepFocalLength(-1);
    }

    // Steps from whichever named stop is CLOSEST to the current value (which may itself be
    // a fine-tuned, non-named value) - clamped at the ends of the list, not wrapping: a real
    // lens kit doesn't cycle past its longest lens back to its widest.
    void StepFocalLength(int direction)
    {
        int index = Mathf.Clamp(FindClosestNamedIndex(currentFocalLengthMM) + direction, 0, NamedFocalLengthsMM.Length - 1);
        currentFocalLengthMM = NamedFocalLengthsMM[index];
        ApplyFocalLength();
        cycleArmed = false;
        SendHaptic();
    }

    static int FindClosestNamedIndex(float mm)
    {
        int closestIndex = 0;
        float closestDiff = Mathf.Abs(NamedFocalLengthsMM[0] - mm);

        for (int i = 1; i < NamedFocalLengthsMM.Length; i++)
        {
            float diff = Mathf.Abs(NamedFocalLengthsMM[i] - mm);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    // The single place that pushes currentFocalLengthMM onto the camera - focalLength
    // drives Unity's physical camera model (see BuildCamera), which recomputes fieldOfView
    // from it automatically, so fieldOfView is never set directly anywhere in this script.
    void ApplyFocalLength()
    {
        if (viewfinderCamera == null)
            return;

        viewfinderCamera.focalLength = currentFocalLengthMM;
        UpdateFocalLengthLabel();
    }

    void UpdateFocalLengthLabel()
    {
        if (focalLengthLabel != null)
            focalLengthLabel.text = $"{Mathf.RoundToInt(currentFocalLengthMM)}mm";
    }

    // Recomputes the panel's rotation every frame
    // instead of a fixed local angle relative to the controller - a guessed fixed angle
    // turned out to face the wrong way (the director reported seeing a mirrored, slanted
    // view, i.e. the panel's back). Reuses the exact same LookRotation convention as every
    // other panel in this project (pause menu, decision popup, playback panel, Favoritos):
    // forward = direction AWAY from the viewer, toward where the panel sits, which is the
    // orientation those panels are confirmed to render correctly (readable, not mirrored)
    // with. Only position stays hand-anchored (still a child of the controller); rotation
    // is independent so it always faces the director no matter how the hand is turned.
    void UpdatePanelFacing()
    {
        Camera headCamera = Camera.main;
        if (headCamera == null)
            return;

        FacePlayer(panelTransform, headCamera);
    }

    static void FacePlayer(Transform target, Camera headCamera)
    {
        if (target == null)
            return;

        Vector3 forward = target.position - headCamera.transform.position;
        if (forward.sqrMagnitude < 0.0001f)
            return;

        target.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    // "Right Controller" isn't reliably found by GameObject.Find() at the exact instant
    // sceneLoaded fires - confirmed via testing (logged "no 'Right Controller' GameObject
    // found") that the XR rig's controller hierarchy isn't fully active in the hierarchy
    // yet at that precise moment, unlike every other GameObject.Find("...Controller") call
    // in this project, which only ever runs later, on a button press. Poll a few frames
    // instead of assuming it's there immediately.
    IEnumerator BuildViewfinderWhenReady()
    {
        for (int attempt = 0; attempt < FindControllerMaxAttempts; attempt++)
        {
            if (GameObject.Find("Right Controller") != null)
                break;
            yield return null;
        }

        buildCoroutine = null;
        BuildViewfinder();
    }

    void BuildViewfinder()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        if (rightController == null)
        {
            Debug.LogWarning("DirectorViewfinderController: no 'Right Controller' GameObject found, cannot build the viewfinder.");
            return;
        }

        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("DirectorViewfinderController: no Camera.main found, cannot match its culling mask.");
            return;
        }

        // Both hands' rays onto ControllerRayVisual, which BuildCamera excludes from the feed.
        XRUISetup.MoveControllerRaysToOwnLayer();
        Transform rightInteractor = rightController.transform.Find("Near-Far Interactor");
        rightNearFarInteractor = rightInteractor != null ? rightInteractor.GetComponent<NearFarInteractor>() : null;
        rightControllerTransform = rightController.transform;

        renderTexture = new RenderTexture(RenderTextureWidth, RenderTextureHeight, 16);
        renderTexture.Create();

        viewfinderRoot = new GameObject("DirectorViewfinder");
        viewfinderRoot.transform.SetParent(rightController.transform, false);
        viewfinderRoot.transform.localPosition = Vector3.zero;
        viewfinderRoot.transform.localRotation = Quaternion.identity;

        BuildCamera(viewfinderRoot.transform, mainCamera);
        BuildPanel(viewfinderRoot.transform);

        // Hidden until the director "raises" it with Grip - never visible by default, so it
        // doesn't block the view during normal rehearsal. Deactivating the root also stops
        // the camera from rendering while hidden, for free.
        viewfinderRoot.SetActive(false);
        isVisible = false;
    }

    // Rigidly parented at the controller's own transform (zero local offset) - the simplest
    // way to guarantee the feed follows the hand's position/rotation exactly, matching
    // "aim where you point your hand" for this first pass.
    //
    // Excludes PanelLayer and RayVisualLayer from its culling mask (everything else is
    // copied from the main camera unchanged). Without the panel exclusion, the viewfinder
    // camera can see its own display panel, which shows the viewfinder's own feed, which
    // the camera then sees again, and so on: a recursive feedback tunnel. Without the ray
    // exclusion, the controller's own ray visual shows up as a flickering streak inside the
    // feed. Only the panel and the ray are on those layers (see BuildPanel and
    // XRUISetup.MoveControllerRaysToOwnLayer), so the director's main camera still sees both completely
    // normally.
    void BuildCamera(Transform parent, Camera mainCamera)
    {
        var cameraObject = new GameObject("ViewfinderCamera", typeof(Camera));
        cameraObject.transform.SetParent(parent, false);
        cameraObject.transform.localPosition = Vector3.zero;
        cameraObject.transform.localRotation = Quaternion.identity;

        int panelLayer = LayerMask.NameToLayer(PanelLayerName);
        int rayLayer = LayerMask.NameToLayer(RayVisualLayerName);

        int excludedMask = 0;
        if (panelLayer >= 0)
            excludedMask |= 1 << panelLayer;
        if (rayLayer >= 0)
            excludedMask |= 1 << rayLayer;

        // The feed shows only the scene: the other panels (pause menu, replay panel, stop-
        // recording popup, Favoritos, the REC indicator) sit on the UI layer - see
        // XRUISetup.MoveToUILayer - and the recorded-camera monitor on its own layer.
        excludedMask |= 1 << XRUISetup.UILayer;
        int monitorLayer = LayerMask.NameToLayer(PlaybackMonitorLayerName);
        if (monitorLayer >= 0)
            excludedMask |= 1 << monitorLayer;

        viewfinderCamera = cameraObject.GetComponent<Camera>();
        viewfinderCamera.clearFlags = mainCamera.clearFlags;
        viewfinderCamera.backgroundColor = mainCamera.backgroundColor;
        viewfinderCamera.cullingMask = mainCamera.cullingMask & ~excludedMask;
        viewfinderCamera.nearClipPlane = mainCamera.nearClipPlane;
        viewfinderCamera.farClipPlane = mainCamera.farClipPlane;
        viewfinderCamera.targetTexture = renderTexture;

        // Physical camera model: fieldOfView becomes a value Unity derives from
        // focalLength/sensorSize/gateFit, not something set directly - 36x24mm is the
        // standard full-frame 35mm stills/cinema sensor the named focal lengths (24/35/50/
        // 85/135mm) assume. Horizontal gate fit matches how those names are conventionally
        // quoted (fit by sensor width), rather than Unity's default vertical fit.
        viewfinderCamera.usePhysicalProperties = true;
        viewfinderCamera.sensorSize = new Vector2(36f, 24f);
        viewfinderCamera.gateFit = Camera.GateFitMode.Horizontal;
        currentFocalLengthMM = DefaultFocalLengthMM;
        viewfinderCamera.focalLength = currentFocalLengthMM;

        if (panelLayer < 0)
            Debug.LogWarning($"DirectorViewfinderController: layer '{PanelLayerName}' not found - the viewfinder panel will render inside its own feed (feedback loop).");
    }

    // World Space Canvas + RawImage bound to the RenderTexture - same "no prefabs, built at
    // runtime in code" convention as every other panel in this project, reused here instead
    // of introducing an unlit-quad-plus-material approach nothing else uses. Display-only:
    // no GraphicRaycaster, so it never catches the ray or the poke interactor.
    //
    // Placed on its own dedicated layer (see PanelLayerName), and every child created here
    // too (layer isn't inherited automatically in Unity) - so the viewfinder camera can
    // exclude the whole panel from its culling mask. Without this the camera would
    // recursively render its own display.
    void BuildPanel(Transform parent)
    {
        int panelLayer = LayerMask.NameToLayer(PanelLayerName);
        float pixelsToMeters = PanelWidth / RenderTextureWidth; // == PanelHeight / RenderTextureHeight (4:3)

        var canvasObject = new GameObject("ViewfinderPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(parent, false);
        canvasObject.transform.localPosition = PanelLocalPosition;
        panelTransform = canvasObject.transform;
        ApplyPanelLayer(canvasObject, panelLayer);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        // Sized 1:1 with the RenderTexture in canvas units, then scaled down to a physical
        // size in meters - same pixel-size-then-world-scale approach used for every other
        // world-space panel in this project.
        RectTransform canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(RenderTextureWidth, RenderTextureHeight);
        canvasRect.localScale = Vector3.one * pixelsToMeters;

        var imageObject = new GameObject("Feed", typeof(RectTransform), typeof(RawImage));
        imageObject.transform.SetParent(canvasRect, false);
        ApplyPanelLayer(imageObject, panelLayer);
        RectTransform feedRect = (RectTransform)imageObject.transform;
        feedRect.anchorMin = Vector2.zero;
        feedRect.anchorMax = Vector2.one;
        feedRect.offsetMin = Vector2.zero;
        feedRect.offsetMax = Vector2.zero;

        RawImage feed = imageObject.GetComponent<RawImage>();
        feed.texture = renderTexture;
        feed.raycastTarget = false;

        BuildFramingOverlay(feedRect, panelLayer);
    }

    void ApplyPanelLayer(GameObject obj, int panelLayer)
    {
        if (panelLayer >= 0)
            obj.layer = panelLayer;
    }

    // Rule-of-thirds grid (2 horizontal + 2 vertical lines) and 16:9 aspect markers,
    // overlaid directly on the Canvas on top of the feed - viewfinder-only guide lines, not
    // baked into the camera's rendered image itself. Plus the focal-length readout (e.g.
    // "50mm"/"47mm"), always reflecting whatever ApplyFocalLength last set, named or
    // fine-tuned. All purely visual - raycastTarget is off throughout.
    void BuildFramingOverlay(RectTransform feedRect, int panelLayer)
    {
        var overlayObject = new GameObject("Overlay", typeof(RectTransform));
        overlayObject.transform.SetParent(feedRect, false);
        ApplyPanelLayer(overlayObject, panelLayer);
        RectTransform overlayRect = (RectTransform)overlayObject.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        // Rule of thirds - two lines each way, at 1/3 and 2/3.
        CreateOverlayLine(overlayRect, panelLayer, ThirdsLineColor, vertical: true, position: 1f / 3f, thickness: ThirdsLineThickness);
        CreateOverlayLine(overlayRect, panelLayer, ThirdsLineColor, vertical: true, position: 2f / 3f, thickness: ThirdsLineThickness);
        CreateOverlayLine(overlayRect, panelLayer, ThirdsLineColor, vertical: false, position: 1f / 3f, thickness: ThirdsLineThickness);
        CreateOverlayLine(overlayRect, panelLayer, ThirdsLineColor, vertical: false, position: 2f / 3f, thickness: ThirdsLineThickness);

        // 16:9 markers - the feed itself is 4:3 (640x480), so a 16:9 crop centered in it is
        // narrower than the full height; these two horizontal lines mark where that crop's
        // top/bottom edges would fall, same idea as a real camera's aspect-guide overlay.
        float cropHeightFraction = (RenderTextureWidth / TargetAspectRatio) / RenderTextureHeight;
        float marginFraction = (1f - cropHeightFraction) / 2f;
        CreateOverlayLine(overlayRect, panelLayer, AspectMarkerColor, vertical: false, position: marginFraction, thickness: AspectMarkerThickness);
        CreateOverlayLine(overlayRect, panelLayer, AspectMarkerColor, vertical: false, position: 1f - marginFraction, thickness: AspectMarkerThickness);

        BuildFocalLengthLabel(overlayRect, panelLayer);
        BuildRecBadge(overlayRect, panelLayer);
    }

    // A thin full-span Image acting as a line - vertical: true spans top-to-bottom at a
    // horizontal fraction of the feed; vertical: false spans left-to-right at a vertical
    // fraction. position is 0-1 (0 = left/bottom, 1 = right/top).
    void CreateOverlayLine(RectTransform parent, int panelLayer, Color color, bool vertical, float position, float thickness)
    {
        var lineObject = new GameObject(vertical ? "ThirdsV" : "ThirdsH", typeof(RectTransform), typeof(Image));
        lineObject.transform.SetParent(parent, false);
        ApplyPanelLayer(lineObject, panelLayer);

        RectTransform lineRect = (RectTransform)lineObject.transform;
        if (vertical)
        {
            lineRect.anchorMin = new Vector2(position, 0f);
            lineRect.anchorMax = new Vector2(position, 1f);
            lineRect.offsetMin = new Vector2(-thickness / 2f, 0f);
            lineRect.offsetMax = new Vector2(thickness / 2f, 0f);
        }
        else
        {
            lineRect.anchorMin = new Vector2(0f, position);
            lineRect.anchorMax = new Vector2(1f, position);
            lineRect.offsetMin = new Vector2(0f, -thickness / 2f);
            lineRect.offsetMax = new Vector2(0f, thickness / 2f);
        }

        Image image = lineObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    // Bottom-left HUD-style readout, like a real camera's on-screen focal-length display -
    // a small dark chip behind the text for legibility against whatever footage is behind
    // it.
    void BuildFocalLengthLabel(RectTransform parent, int panelLayer)
    {
        var chipObject = new GameObject("FocalLengthChip", typeof(RectTransform), typeof(Image));
        chipObject.transform.SetParent(parent, false);
        ApplyPanelLayer(chipObject, panelLayer);
        RectTransform chipRect = (RectTransform)chipObject.transform;
        chipRect.anchorMin = Vector2.zero;
        chipRect.anchorMax = Vector2.zero;
        chipRect.pivot = Vector2.zero;
        chipRect.anchoredPosition = FocalLengthLabelMargin;
        chipRect.sizeDelta = new Vector2(120f, FocalLengthLabelHeight);

        Image chipImage = chipObject.GetComponent<Image>();
        chipImage.color = FocalLengthLabelBackgroundColor;
        chipImage.raycastTarget = false;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(chipRect, false);
        ApplyPanelLayer(textObject, panelLayer);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        focalLengthLabel = textObject.GetComponent<Text>();
        focalLengthLabel.font = uiFont;
        focalLengthLabel.text = $"{Mathf.RoundToInt(currentFocalLengthMM)}mm";
        focalLengthLabel.fontSize = FocalLengthLabelFontSize;
        focalLengthLabel.fontStyle = FontStyle.Bold;
        focalLengthLabel.color = PanelTextColor;
        focalLengthLabel.alignment = TextAnchor.MiddleCenter;
        focalLengthLabel.raycastTarget = false;
    }

    // Top-left, mirroring the focal-length chip bottom-left. Hidden until a recording starts
    // - UpdateRecBadge follows RehearsalRecorder.IsRecording.
    void BuildRecBadge(RectTransform parent, int panelLayer)
    {
        recBadge = new GameObject("RecBadge", typeof(RectTransform), typeof(Image));
        recBadge.transform.SetParent(parent, false);
        ApplyPanelLayer(recBadge, panelLayer);
        RectTransform badgeRect = (RectTransform)recBadge.transform;
        badgeRect.anchorMin = new Vector2(0f, 1f);
        badgeRect.anchorMax = new Vector2(0f, 1f);
        badgeRect.pivot = new Vector2(0f, 1f);
        badgeRect.anchoredPosition = RecBadgeMargin;
        badgeRect.sizeDelta = RecBadgeSize;

        Image badgeImage = recBadge.GetComponent<Image>();
        badgeImage.color = RecBadgeBackgroundColor;
        badgeImage.raycastTarget = false;

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(badgeRect, false);
        ApplyPanelLayer(textObject, panelLayer);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text label = textObject.GetComponent<Text>();
        label.font = uiFont;
        label.text = "● REC";
        label.fontSize = RecBadgeFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = RecBadgeTextColor;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;

        recBadge.SetActive(false);
    }

    void UpdateRecBadge()
    {
        if (recBadge == null)
            return;

        bool isRecording = RehearsalRecorder.Instance != null && RehearsalRecorder.Instance.IsRecording;
        if (recBadge.activeSelf != isRecording)
            recBadge.SetActive(isRecording);
    }

    // Doesn't toggle on press anymore - just starts tracking the hold, so UpdateZoom can
    // tell whether the joystick moved while Grip was down. The actual toggle decision
    // happens on release, in OnGripReleased.
    void OnGripStarted(InputAction.CallbackContext context)
    {
        isGripHeld = true;
        didFineTuneDuringGripHold = false;
    }

    void OnGripReleased(InputAction.CallbackContext context)
    {
        isGripHeld = false;

        // This hold was used to fine-tune zoom, not as a toggle tap - leave the panel's
        // visibility exactly as it was.
        if (didFineTuneDuringGripHold)
            return;

        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) || Bootstrap.IsInspecting)
            return;

        if (viewfinderRoot == null)
            return;

        isVisible = !isVisible;
        viewfinderRoot.SetActive(isVisible);

        if (isVisible)
        {
            PauseTurnLocomotion();
            PauseRightTeleport();
        }
        else
        {
            ResumeTurnLocomotion();
            ResumeRightTeleport();
        }

        SendHaptic();
    }

    // The zoom joystick is the same physical stick as stock Turn/snap-turn locomotion -
    // suspended for as long as the panel is visible, same idea as PauseMenuController
    // suspending Move/Turn while its own panel is open, just scoped to Turn only (Move is
    // the left joystick, untouched).
    void PauseTurnLocomotion()
    {
        GameObject turn = GameObject.Find("Turn");
        if (turn != null && turn.activeSelf)
        {
            turn.SetActive(false);
            disabledTurnObject = turn;
        }
    }

    void ResumeTurnLocomotion()
    {
        if (disabledTurnObject != null)
            disabledTurnObject.SetActive(true);
        disabledTurnObject = null;
    }

    // Stick up is also the stock rig's right-hand "Teleport Mode", so zooming in would pop
    // the teleport ray on every push. The right ControllerInputActionManager is what reacts
    // to it (activating the Teleport Interactor), so it's switched off while the viewfinder
    // is up. Disabling it only unsubscribes its callbacks - the Near-Far ray and UI keep
    // working - and re-enabling it hides the Teleport Interactor and restores its actions.
    void PauseRightTeleport()
    {
        if (rightControllerTransform == null)
            return;

        ControllerInputActionManager manager = rightControllerTransform.GetComponent<ControllerInputActionManager>();
        if (manager == null || !manager.enabled)
            return;

        manager.enabled = false;
        pausedRightInputManager = manager;

        // In case the stick was already up when the viewfinder was raised: the manager had
        // swapped the Near-Far ray out for the teleport ray, and with it disabled nothing
        // would swap them back.
        Transform teleportInteractor = rightControllerTransform.Find("Teleport Interactor");
        if (teleportInteractor != null && teleportInteractor.gameObject.activeSelf)
        {
            teleportInteractor.gameObject.SetActive(false);
            if (rightNearFarInteractor != null)
                rightNearFarInteractor.gameObject.SetActive(true);
        }
    }

    void ResumeRightTeleport()
    {
        if (pausedRightInputManager != null)
            pausedRightInputManager.enabled = true;
        pausedRightInputManager = null;
    }

    void SendHaptic()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        if (rightController == null)
            return;

        HapticImpulsePlayer hapticPlayer = rightController.GetComponent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
#endif
}
