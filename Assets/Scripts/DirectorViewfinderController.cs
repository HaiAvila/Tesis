using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if DIRECTOR_BUILD
using System.Collections;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
#endif

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Director-only, first pass of the
// "virtual camera" feature: a small camera rigidly parented to the right controller,
// rendering to a RenderTexture shown on a handheld world-space panel (like checking a phone
// screen), toggled visible/hidden with the right controller's Grip button. No zoom or
// framing controls yet - this step only proves the live feed follows the hand correctly.
//
// Right hand chosen deliberately, after checking rather than assuming: neither controller
// in this project's XR rig is actually wired up for UI ray/poke interaction yet - both
// "Left Controller" and "Right Controller" carry the identical, symmetric stock Near-Far
// Interactor from the XRI Starter Assets prefab (confirmed by inspecting both in the
// Editor - same settings apart from m_Handedness). So there's no existing "UI hand" to
// avoid conflicting with. Picked right hand because it's the less loaded one overall - left
// hand already carries Menu, Play/Pause (Inspección de Escenas), and the free-fly joystick.
// Grip is unused on both hands until now.
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

    // Placeholder offset, not tuned - positions the panel roughly like a phone screen held
    // up close to the hand. Only a position now (see UpdatePanelFacing) - rotation is
    // computed live every frame instead of guessed as a fixed local angle.
    static readonly Vector3 PanelLocalPosition = new Vector3(0f, 0.02f, 0.05f);
    const float PanelWidth = 0.09f;
    const float PanelHeight = 0.0675f; // 4:3, matches the RenderTexture's aspect ratio

#if DIRECTOR_BUILD
    const int FindControllerMaxAttempts = 30; // ~0.5s at 60fps

    InputAction gripToggleAction;
    GameObject viewfinderRoot;
    Transform panelTransform;
    RenderTexture renderTexture;
    Coroutine buildCoroutine;
    bool isVisible;
#endif

    void Awake()
    {
#if DIRECTOR_BUILD
        // Same curly-brace "usage" binding convention as the recording shortcut and
        // Play/Pause, plus the XR Device Simulator's equivalent for in-Editor testing.
        gripToggleAction = new InputAction("DirectorViewfinderToggle", InputActionType.Button);
        gripToggleAction.AddBinding("<XRController>{RightHand}/{gripButton}");
        gripToggleAction.AddBinding("<XRSimulatedController>{RightHand}/gripButton");
        gripToggleAction.performed += OnGripPressed;
        gripToggleAction.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
#endif
    }

    void OnDestroy()
    {
#if DIRECTOR_BUILD
        gripToggleAction.performed -= OnGripPressed;
        gripToggleAction.Disable();
        gripToggleAction.Dispose();

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

        if (renderTexture != null)
            renderTexture.Release();
        renderTexture = null;

        isVisible = false;
    }

    // Recomputes the panel's rotation every frame instead of a fixed local angle relative
    // to the controller - a guessed fixed angle turned out to face the wrong way (the
    // director reported seeing a mirrored, slanted view, i.e. the panel's back). Reuses the
    // exact same LookRotation convention as every other panel in this project (pause menu,
    // decision popup, playback panel, Favoritos): forward = direction AWAY from the
    // viewer, toward where the panel sits, which is the orientation those panels are
    // confirmed to render correctly (readable, not mirrored) with. Only the panel's
    // position stays hand-anchored (it's still a child of the controller); the rotation is
    // independent so it always faces the director no matter how the hand is turned.
    void Update()
    {
        if (viewfinderRoot == null || !isVisible || panelTransform == null)
            return;

        Camera headCamera = Camera.main;
        if (headCamera == null)
            return;

        Vector3 forward = panelTransform.position - headCamera.transform.position;
        if (forward.sqrMagnitude < 0.0001f)
            return;

        panelTransform.rotation = Quaternion.LookRotation(forward, Vector3.up);
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
    // Excludes the PanelLayer from its culling mask (everything else is copied from the
    // main camera unchanged) - without this, the viewfinder camera can see its own display
    // panel, which shows the viewfinder's own feed, which the camera then sees again, and
    // so on: a recursive feedback tunnel. Only the panel itself is on that layer (see
    // BuildPanel), so the director's main camera still sees it completely normally.
    void BuildCamera(Transform parent, Camera mainCamera)
    {
        var cameraObject = new GameObject("ViewfinderCamera", typeof(Camera));
        cameraObject.transform.SetParent(parent, false);
        cameraObject.transform.localPosition = Vector3.zero;
        cameraObject.transform.localRotation = Quaternion.identity;

        int panelLayer = LayerMask.NameToLayer(PanelLayerName);

        Camera viewfinderCamera = cameraObject.GetComponent<Camera>();
        viewfinderCamera.clearFlags = mainCamera.clearFlags;
        viewfinderCamera.backgroundColor = mainCamera.backgroundColor;
        viewfinderCamera.cullingMask = panelLayer >= 0
            ? mainCamera.cullingMask & ~(1 << panelLayer)
            : mainCamera.cullingMask;
        viewfinderCamera.fieldOfView = mainCamera.fieldOfView;
        viewfinderCamera.nearClipPlane = mainCamera.nearClipPlane;
        viewfinderCamera.farClipPlane = mainCamera.farClipPlane;
        viewfinderCamera.targetTexture = renderTexture;

        if (panelLayer < 0)
            Debug.LogWarning($"DirectorViewfinderController: layer '{PanelLayerName}' not found - the viewfinder panel will render inside its own feed (feedback loop).");
    }

    // World Space Canvas + RawImage bound to the RenderTexture - same "no prefabs, built at
    // runtime in code" convention as every other panel in this project, reused here instead
    // of introducing an unlit-quad-plus-material approach nothing else uses.
    //
    // Placed on its own dedicated layer (see PanelLayerName) so the viewfinder camera can
    // exclude just this panel from its culling mask - the only thing that stops it from
    // recursively rendering its own display.
    void BuildPanel(Transform parent)
    {
        var canvasObject = new GameObject("ViewfinderPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(parent, false);
        canvasObject.transform.localPosition = PanelLocalPosition;
        panelTransform = canvasObject.transform;

        int panelLayer = LayerMask.NameToLayer(PanelLayerName);
        if (panelLayer >= 0)
            canvasObject.layer = panelLayer;

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        // Sized 1:1 with the RenderTexture in canvas units, then scaled down to a physical,
        // roughly phone-screen size in meters - same pixel-size-then-world-scale approach
        // used for every other world-space panel in this project.
        RectTransform canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(RenderTextureWidth, RenderTextureHeight);
        canvasRect.localScale = new Vector3(PanelWidth / RenderTextureWidth, PanelHeight / RenderTextureHeight, 1f);

        var imageObject = new GameObject("Feed", typeof(RectTransform), typeof(RawImage));
        imageObject.transform.SetParent(canvasRect, false);
        if (panelLayer >= 0)
            imageObject.layer = panelLayer;
        RectTransform imageRect = (RectTransform)imageObject.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        imageObject.GetComponent<RawImage>().texture = renderTexture;
    }

    void OnGripPressed(InputAction.CallbackContext context)
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) || Bootstrap.IsInspecting)
            return;

        if (viewfinderRoot == null)
            return;

        isVisible = !isVisible;
        viewfinderRoot.SetActive(isVisible);
        SendHaptic();
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
