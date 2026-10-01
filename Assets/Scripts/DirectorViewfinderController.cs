using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if DIRECTOR_BUILD
using System.Collections;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
#endif

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Director-only "virtual camera":
// a small camera rigidly parented to the right controller, rendering to a RenderTexture
// shown on a handheld world-space panel (like checking a phone screen), toggled
// visible/hidden with the right controller's Grip button, with zoom on the same hand's
// joystick (push up to zoom in, down to zoom out, adjusting the camera's Field of View).
//
// The panel also has a small button bar: "Guardar framing" saves the camera's current
// world position/rotation/FOV for the active shot, and "Framings" opens a short list of
// previously saved ones for that shot with a "Cargar" button each. Loading one snaps the
// camera to that saved pose and LOCKS it there (ignoring the hand) until the director
// physically moves the controller again - see UpdateLock.
//
// Right hand chosen deliberately, after checking rather than assuming: neither controller
// in this project's XR rig is actually wired up for UI ray/poke interaction yet - both
// "Left Controller" and "Right Controller" carry the identical, symmetric stock Near-Far
// Interactor from the XRI Starter Assets prefab (confirmed by inspecting both in the
// Editor - same settings apart from m_Handedness). So there's no existing "UI hand" to
// avoid conflicting with. Picked right hand because it's the less loaded one overall - left
// hand already carries Menu, Play/Pause (Inspección de Escenas), and the free-fly joystick.
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
    // (Near-Far Interactor/LineVisual, stock XRI) is on Default by default, so it showed up
    // as a flickering streak inside the viewfinder's own feed too. Moving it to its own
    // layer lets the viewfinder camera exclude just the ray, independently of the panel
    // layer above - every other camera (including the director's own main camera, whose
    // culling mask is "Everything") still renders it completely normally.
    const string RayVisualLayerName = "ControllerRayVisual";

    // Placeholder offset, not tuned - positions the panel roughly like a phone screen held
    // up close to the hand. Only a position now (see UpdatePanelFacing) - rotation is
    // computed live every frame instead of guessed as a fixed local angle.
    static readonly Vector3 PanelLocalPosition = new Vector3(0f, 0.02f, 0.05f);
    const float PanelWidth = 0.09f;
    const float PanelHeight = 0.0675f; // 4:3, matches the RenderTexture's aspect ratio

    // Narrow (zoomed in) to wide (normal) Field of View range, in degrees.
    const float MinFieldOfView = 20f;
    const float MaxFieldOfView = 80f;
    const float ZoomSpeedDegreesPerSecond = 30f; // full 60° range in 2s at full deflection
    const float ZoomJoystickDeadzone = 0.15f;

    // A loaded framing stays fixed in world space until the controller itself moves past
    // these thresholds - small enough to feel deliberate (not triggered by hand tremor),
    // large enough that picking the controller back up releases the lock promptly.
    const float UnlockDistanceMeters = 0.03f;
    const float UnlockAngleDegrees = 5f;

    const int PanelIconFontSize = 28;
    const float PanelButtonSize = 90f; // canvas units, same scale as the 640x480 feed
    const float PanelButtonBarHeight = 90f;
    const float PanelRowSpacing = 10f;

    const int FramingFontSize = 22;
    const float FramingRowHeight = 64f;
    const float FramingLoadButtonWidth = 140f;
    static readonly Vector3 FramingListLocalOffset = new Vector3(0f, 0.09f, 0f); // above the main panel, placeholder like every other offset here

    static readonly Color PanelButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.9f);
    static readonly Color PanelButtonTextColor = new Color(0.95f, 0.95f, 0.98f, 1f);
    static readonly Color PanelRowColor = new Color(0.2f, 0.2f, 0.2f, 1f);

#if DIRECTOR_BUILD
    const int FindControllerMaxAttempts = 30; // ~0.5s at 60fps

    InputAction gripToggleAction;
    InputAction zoomAction;
    Font uiFont;
    GameObject viewfinderRoot;
    GameObject framingListObject;
    Transform panelTransform;
    Transform framingListTransform;
    Transform rightControllerTransform;
    Camera viewfinderCamera;
    RenderTexture renderTexture;
    Coroutine buildCoroutine;
    GameObject disabledTurnObject;
    float currentFieldOfView;
    bool isVisible;

    // Set when a saved framing is loaded - the camera holds this world pose regardless of
    // hand movement until UpdateLock detects the controller itself has moved enough to
    // count as the director taking manual control back.
    bool isLocked;
    Vector3 lockedPosition;
    Quaternion lockedRotation;
    Vector3 controllerPositionAtLock;
    Quaternion controllerRotationAtLock;
#endif

    void Awake()
    {
#if DIRECTOR_BUILD
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Same curly-brace "usage" binding convention as the recording shortcut and
        // Play/Pause, plus the XR Device Simulator's equivalent for in-Editor testing.
        gripToggleAction = new InputAction("DirectorViewfinderToggle", InputActionType.Button);
        gripToggleAction.AddBinding("<XRController>{RightHand}/{gripButton}");
        gripToggleAction.AddBinding("<XRSimulatedController>{RightHand}/gripButton");
        gripToggleAction.performed += OnGripPressed;
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
#if DIRECTOR_BUILD
        gripToggleAction.performed -= OnGripPressed;
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

        // framingListObject is a child of viewfinderRoot, already destroyed above.
        framingListObject = null;
        framingListTransform = null;

        if (renderTexture != null)
            renderTexture.Release();
        renderTexture = null;

        // The GameObject itself belongs to the scene that just unloaded - already gone,
        // just drop the reference instead of re-enabling it.
        disabledTurnObject = null;

        isVisible = false;
        isLocked = false;
    }

    void Update()
    {
        if (viewfinderRoot == null || !isVisible)
            return;

        UpdateZoom();
        UpdateLock();
        UpdatePanelFacing();
    }

    void UpdateZoom()
    {
        if (viewfinderCamera == null)
            return;

        float input = zoomAction.ReadValue<Vector2>().y;
        if (Mathf.Abs(input) < ZoomJoystickDeadzone)
            return;

        currentFieldOfView = Mathf.Clamp(
            currentFieldOfView - input * ZoomSpeedDegreesPerSecond * Time.deltaTime,
            MinFieldOfView, MaxFieldOfView);
        viewfinderCamera.fieldOfView = currentFieldOfView;
    }

    // While locked (a saved framing was just loaded), the camera is still a child of the
    // controller - without this, it would just sit at whatever local offset was implied by
    // the saved world pose at the moment it was loaded, and immediately start drifting
    // again as the hand moves. Re-applying the saved world pose every frame keeps it
    // genuinely fixed regardless of hand movement. The lock releases - re-gluing the camera
    // to the hand's current pose - once the controller itself has moved past the unlock
    // thresholds, which reads as "pick the camera back up to take control again".
    void UpdateLock()
    {
        if (!isLocked || viewfinderCamera == null)
            return;

        viewfinderCamera.transform.SetPositionAndRotation(lockedPosition, lockedRotation);

        if (rightControllerTransform == null)
            return;

        float moved = Vector3.Distance(rightControllerTransform.position, controllerPositionAtLock);
        float rotated = Quaternion.Angle(rightControllerTransform.rotation, controllerRotationAtLock);
        if (moved <= UnlockDistanceMeters && rotated <= UnlockAngleDegrees)
            return;

        isLocked = false;
        viewfinderCamera.transform.localPosition = Vector3.zero;
        viewfinderCamera.transform.localRotation = Quaternion.identity;
    }

    // Recomputes the panel's (and, if open, the framing list's) rotation every frame
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
        FacePlayer(framingListTransform, headCamera);
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

        AssignRayVisualLayer(rightController);
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

    // Moves both controllers' ray visuals (Near-Far Interactor/LineVisual, stock XRI, on
    // Default by default) onto their own dedicated layer, so the viewfinder camera can
    // exclude the ray independently of the panel layer, without touching the panel's own
    // layer. Both hands, not just the one holding the viewfinder - the director can point
    // the left-hand ray at something while reviewing the right-hand camera, and that ray
    // would show up in the feed just the same. Changing a GameObject's render layer only
    // affects which cameras render it, not XRI's own interaction raycasting (a separate
    // "Interaction Layer Mask" system) - so this has no effect on how the ray interacts.
    void AssignRayVisualLayer(GameObject rightController)
    {
        int rayLayer = LayerMask.NameToLayer(RayVisualLayerName);
        if (rayLayer < 0)
        {
            Debug.LogWarning($"DirectorViewfinderController: layer '{RayVisualLayerName}' not found - the controller ray will be visible inside the viewfinder feed.");
            return;
        }

        AssignLayerToLineVisual(rightController, rayLayer);

        GameObject leftController = GameObject.Find("Left Controller");
        if (leftController != null)
            AssignLayerToLineVisual(leftController, rayLayer);
    }

    void AssignLayerToLineVisual(GameObject controller, int layer)
    {
        Transform lineVisual = controller.transform.Find("Near-Far Interactor/LineVisual");
        if (lineVisual != null)
            lineVisual.gameObject.layer = layer;
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
    // AssignRayVisualLayer), so the director's main camera still sees both completely
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

        viewfinderCamera = cameraObject.GetComponent<Camera>();
        viewfinderCamera.clearFlags = mainCamera.clearFlags;
        viewfinderCamera.backgroundColor = mainCamera.backgroundColor;
        viewfinderCamera.cullingMask = mainCamera.cullingMask & ~excludedMask;
        currentFieldOfView = Mathf.Clamp(mainCamera.fieldOfView, MinFieldOfView, MaxFieldOfView);
        viewfinderCamera.fieldOfView = currentFieldOfView;
        viewfinderCamera.nearClipPlane = mainCamera.nearClipPlane;
        viewfinderCamera.farClipPlane = mainCamera.farClipPlane;
        viewfinderCamera.targetTexture = renderTexture;

        if (panelLayer < 0)
            Debug.LogWarning($"DirectorViewfinderController: layer '{PanelLayerName}' not found - the viewfinder panel will render inside its own feed (feedback loop).");
    }

    // World Space Canvas + RawImage bound to the RenderTexture, plus a small button bar
    // below it (Guardar framing / Framings) - same "no prefabs, built at runtime in code"
    // convention as every other panel in this project, reused here instead of introducing
    // an unlit-quad-plus-material approach nothing else uses.
    //
    // Placed on its own dedicated layer (see PanelLayerName), and every child created here
    // too (layer isn't inherited automatically in Unity) - so the viewfinder camera can
    // exclude the whole panel, buttons included, from its culling mask. Without this the
    // camera would recursively render its own display.
    void BuildPanel(Transform parent)
    {
        int panelLayer = LayerMask.NameToLayer(PanelLayerName);
        float pixelsToMeters = PanelWidth / RenderTextureWidth; // == PanelHeight / RenderTextureHeight (4:3)

        var canvasObject = new GameObject("ViewfinderPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(parent, false);
        canvasObject.transform.localPosition = PanelLocalPosition;
        panelTransform = canvasObject.transform;
        ApplyPanelLayer(canvasObject, panelLayer);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        // Sized 1:1 with the RenderTexture in canvas units (plus the button bar strip),
        // then scaled down to a physical, roughly phone-screen size in meters - same
        // pixel-size-then-world-scale approach used for every other world-space panel in
        // this project.
        RectTransform canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(RenderTextureWidth, RenderTextureHeight + PanelRowSpacing + PanelButtonBarHeight);
        canvasRect.localScale = Vector3.one * pixelsToMeters;

        var rootLayoutObject = new GameObject("Root", typeof(RectTransform), typeof(VerticalLayoutGroup));
        rootLayoutObject.transform.SetParent(canvasRect, false);
        ApplyPanelLayer(rootLayoutObject, panelLayer);
        RectTransform rootRect = (RectTransform)rootLayoutObject.transform;
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        VerticalLayoutGroup rootLayout = rootLayoutObject.GetComponent<VerticalLayoutGroup>();
        rootLayout.spacing = PanelRowSpacing;
        rootLayout.childAlignment = TextAnchor.UpperCenter;
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        var imageObject = new GameObject("Feed", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
        imageObject.transform.SetParent(rootRect, false);
        ApplyPanelLayer(imageObject, panelLayer);
        imageObject.GetComponent<LayoutElement>().preferredHeight = RenderTextureHeight;
        imageObject.GetComponent<RawImage>().texture = renderTexture;

        BuildButtonBar(rootRect, panelLayer);

        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
    }

    void BuildButtonBar(RectTransform parent, int panelLayer)
    {
        var rowObject = new GameObject("ButtonBar", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        ApplyPanelLayer(rowObject, panelLayer);
        rowObject.GetComponent<LayoutElement>().preferredHeight = PanelButtonBarHeight;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = PanelRowSpacing;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = false;
        rowLayout.childControlHeight = false;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        CreatePanelIconButton(rowObject.transform, "\U0001F4BE", OnSaveFramingClicked, panelLayer); // 💾 Guardar framing
        CreatePanelIconButton(rowObject.transform, "\U0001F4C2", OnToggleFramingListClicked, panelLayer); // 📂 Framings
    }

    Text CreatePanelIconButton(Transform parent, string symbol, UnityEngine.Events.UnityAction onClick, int panelLayer)
    {
        var buttonObject = new GameObject($"Button_{symbol}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        ApplyPanelLayer(buttonObject, panelLayer);
        RectTransform buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.sizeDelta = new Vector2(PanelButtonSize, PanelButtonSize);
        buttonObject.GetComponent<Image>().color = PanelButtonColor;
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        ApplyPanelLayer(textObject, panelLayer);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.text = symbol;
        text.fontSize = PanelIconFontSize;
        text.fontStyle = FontStyle.Bold;
        text.color = PanelButtonTextColor;
        text.alignment = TextAnchor.MiddleCenter;

        return text;
    }

    void ApplyPanelLayer(GameObject obj, int panelLayer)
    {
        if (panelLayer >= 0)
            obj.layer = panelLayer;
    }

    void OnSaveFramingClicked()
    {
        if (viewfinderCamera == null)
            return;

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("DirectorViewfinderController: no ShotListDatabase instance found, cannot save the framing.");
            return;
        }

        Transform cameraTransform = viewfinderCamera.transform;
        database.SaveCameraFraming(Bootstrap.CurrentShotNumber, cameraTransform.position, cameraTransform.rotation, viewfinderCamera.fieldOfView);
        SendHaptic();
    }

    void OnToggleFramingListClicked()
    {
        if (framingListObject != null)
        {
            Destroy(framingListObject);
            framingListObject = null;
            framingListTransform = null;
            SendHaptic();
            return;
        }

        ShowFramingList();
        SendHaptic();
    }

    // Short, unscrolled list (no cap on how many a shot can have yet) of every framing
    // saved for the active shot, most recent first - rebuilt fresh from the DB each time
    // it's opened, as its own small canvas positioned above the main panel so it doesn't
    // crowd the feed. Each row is self-contained (own background), rather than the panel
    // having one shared background, since the canvas's own height is driven by a
    // ContentSizeFitter reacting to the rows - simpler than sizing a wrapping background to
    // match.
    void ShowFramingList()
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("DirectorViewfinderController: no ShotListDatabase instance found, cannot load framings.");
            return;
        }

        List<CameraFraming> framings = database.GetCameraFramingsForShot(Bootstrap.CurrentShotNumber);
        int panelLayer = LayerMask.NameToLayer(PanelLayerName);
        float pixelsToMeters = PanelWidth / RenderTextureWidth;

        framingListObject = new GameObject("FramingList", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        framingListObject.transform.SetParent(viewfinderRoot.transform, false);
        ApplyPanelLayer(framingListObject, panelLayer);
        framingListObject.transform.localPosition = PanelLocalPosition + FramingListLocalOffset;
        framingListTransform = framingListObject.transform;

        Canvas canvas = framingListObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        RectTransform listRect = (RectTransform)framingListObject.transform;
        listRect.sizeDelta = new Vector2(RenderTextureWidth, FramingRowHeight); // height placeholder - ContentSizeFitter drives the real value
        listRect.localScale = Vector3.one * pixelsToMeters;

        VerticalLayoutGroup listLayout = framingListObject.GetComponent<VerticalLayoutGroup>();
        listLayout.spacing = PanelRowSpacing;
        listLayout.padding = new RectOffset(12, 12, 12, 12);
        listLayout.childAlignment = TextAnchor.UpperCenter;
        listLayout.childControlWidth = true;
        listLayout.childControlHeight = true;
        listLayout.childForceExpandWidth = true;
        listLayout.childForceExpandHeight = false;

        framingListObject.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (framings.Count == 0)
        {
            CreateFramingMessageRow(framingListObject.transform, "Sin framings guardados para este shot.", panelLayer);
        }
        else
        {
            foreach (CameraFraming framing in framings)
                CreateFramingRow(framingListObject.transform, framing, panelLayer);
        }
    }

    void CreateFramingMessageRow(Transform parent, string message, int panelLayer)
    {
        var textObject = new GameObject("Message", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        textObject.transform.SetParent(parent, false);
        ApplyPanelLayer(textObject, panelLayer);
        textObject.GetComponent<LayoutElement>().preferredHeight = FramingRowHeight;

        Text text = textObject.GetComponent<Text>();
        text.font = uiFont;
        text.text = message;
        text.fontSize = FramingFontSize;
        text.color = PanelButtonTextColor;
        text.alignment = TextAnchor.MiddleCenter;
    }

    void CreateFramingRow(Transform parent, CameraFraming framing, int panelLayer)
    {
        var rowObject = new GameObject($"Framing_{framing.Id}", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parent, false);
        ApplyPanelLayer(rowObject, panelLayer);
        rowObject.GetComponent<Image>().color = PanelRowColor;
        rowObject.GetComponent<LayoutElement>().preferredHeight = FramingRowHeight;

        HorizontalLayoutGroup rowLayout = rowObject.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = PanelRowSpacing;
        rowLayout.padding = new RectOffset(12, 12, 4, 4);
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = true;

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        labelObject.transform.SetParent(rowObject.transform, false);
        ApplyPanelLayer(labelObject, panelLayer);
        labelObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

        Text label = labelObject.GetComponent<Text>();
        label.font = uiFont;
        label.text = FormatFramingDateTime(framing.Timestamp);
        label.fontSize = FramingFontSize;
        label.color = PanelButtonTextColor;
        label.alignment = TextAnchor.MiddleLeft;

        var loadButtonObject = new GameObject("LoadButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        loadButtonObject.transform.SetParent(rowObject.transform, false);
        ApplyPanelLayer(loadButtonObject, panelLayer);
        loadButtonObject.GetComponent<Image>().color = PanelButtonColor;
        LayoutElement loadLayout = loadButtonObject.GetComponent<LayoutElement>();
        loadLayout.preferredWidth = FramingLoadButtonWidth;
        loadLayout.preferredHeight = FramingRowHeight - 12f;

        var loadTextObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        loadTextObject.transform.SetParent(loadButtonObject.transform, false);
        ApplyPanelLayer(loadTextObject, panelLayer);
        RectTransform loadTextRect = (RectTransform)loadTextObject.transform;
        loadTextRect.anchorMin = Vector2.zero;
        loadTextRect.anchorMax = Vector2.one;
        loadTextRect.offsetMin = Vector2.zero;
        loadTextRect.offsetMax = Vector2.zero;

        Text loadText = loadTextObject.GetComponent<Text>();
        loadText.font = uiFont;
        loadText.text = "Cargar";
        loadText.fontSize = FramingFontSize;
        loadText.fontStyle = FontStyle.Bold;
        loadText.color = PanelButtonTextColor;
        loadText.alignment = TextAnchor.MiddleCenter;

        CameraFraming capturedFraming = framing;
        loadButtonObject.GetComponent<Button>().onClick.AddListener(() => OnLoadFramingClicked(capturedFraming));
    }

    void OnLoadFramingClicked(CameraFraming framing)
    {
        if (viewfinderCamera == null)
            return;

        lockedPosition = new Vector3(framing.PositionX, framing.PositionY, framing.PositionZ);
        lockedRotation = new Quaternion(framing.RotationX, framing.RotationY, framing.RotationZ, framing.RotationW);
        currentFieldOfView = Mathf.Clamp(framing.Fov, MinFieldOfView, MaxFieldOfView);

        viewfinderCamera.transform.SetPositionAndRotation(lockedPosition, lockedRotation);
        viewfinderCamera.fieldOfView = currentFieldOfView;

        isLocked = true;
        if (rightControllerTransform != null)
        {
            controllerPositionAtLock = rightControllerTransform.position;
            controllerRotationAtLock = rightControllerTransform.rotation;
        }

        SendHaptic();
    }

    static string FormatFramingDateTime(string timestamp)
    {
        return DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToString("dd/MM/yyyy HH:mm")
            : timestamp;
    }

    void OnGripPressed(InputAction.CallbackContext context)
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) || Bootstrap.IsInspecting)
            return;

        if (viewfinderRoot == null)
            return;

        isVisible = !isVisible;
        viewfinderRoot.SetActive(isVisible);

        if (isVisible)
            PauseTurnLocomotion();
        else
            ResumeTurnLocomotion();

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
