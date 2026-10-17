using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

// XR controller ray support for the runtime-built World Space panels.
//
// - EventSystem: XRUIInputModule instead of StandaloneInputModule. The rig's Near-Far
//   Interactors (UI interaction already enabled in the XRI prefab) register with it on their
//   own, and it also handles the mouse via its built-in fallback actions, so clicking in the
//   Editor keeps working.
// - Canvas: TrackedDeviceGraphicRaycaster next to the existing GraphicRaycaster. XRI is built
//   for the two to coexist: ray events are raycast at an off-screen position so the
//   GraphicRaycaster ignores them, and the tracked raycaster ignores the mouse.
public static class XRUISetup
{
    // Persistent (DontDestroyOnLoad), same as XRI's own auto-created EventSystem, so there's
    // exactly one for the whole app instead of one per scene. If an EventSystem already exists
    // without an XRUIInputModule, its other input modules are disabled - two active modules
    // would fight over the same pointer events.
    public static void EnsureEventSystem()
    {
        EventSystem existing = Object.FindFirstObjectByType<EventSystem>();
        if (existing == null)
        {
            var eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(XRUIInputModule));
            Object.DontDestroyOnLoad(eventSystemObject);
            return;
        }

        if (existing.GetComponent<XRUIInputModule>() != null)
            return;

        foreach (BaseInputModule module in existing.GetComponents<BaseInputModule>())
            module.enabled = false;
        existing.gameObject.AddComponent<XRUIInputModule>();
    }

    // For canvases with clickable elements only - a display-only canvas with this would just
    // swallow rays aimed at things behind it.
    public static void EnableRayInteraction(GameObject canvasObject)
    {
        if (canvasObject.GetComponent<TrackedDeviceGraphicRaycaster>() == null)
            canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();
    }

    // Unity's built-in "UI" layer.
    public const int UILayer = 5;

    // For panels that can be on screen while a virtual camera is rendering (the director's
    // viewfinder, the recorded-camera playback): those cameras exclude UILayer, so the panel
    // shows to the person but not inside the camera's image. The rig's ray mask already
    // includes UI, so the ray still presses it. Call once the panel is fully built - objects
    // created with new GameObject start on Default, not their parent's layer.
    public static void MoveToUILayer(GameObject root)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = UILayer;
    }

    // Project user layer (index 9) for the controller rays, which the stock rig leaves on
    // Default. Virtual cameras exclude it, so a ray never shows up inside a camera's image -
    // every LineRenderer on both controllers, not just the Near-Far ray: the Teleport
    // Interactor ray (inactive until the stick is pushed up, hence includeInactive) was
    // still showing in the viewfinder feed. Render layers only decide which cameras draw an
    // object, not what XRI's ray can interact with (that's the Interaction Layer Mask), so
    // this doesn't change how the ray works. Returns the layer, or -1 if it's missing.
    public const string ControllerRayLayerName = "ControllerRayVisual";

    public static int MoveControllerRaysToOwnLayer()
    {
        int rayLayer = LayerMask.NameToLayer(ControllerRayLayerName);
        if (rayLayer < 0)
        {
            Debug.LogWarning($"XRUISetup: layer '{ControllerRayLayerName}' not found - controller rays will show up inside virtual camera images.");
            return -1;
        }

        foreach (string controllerName in new[] { "Left Controller", "Right Controller" })
        {
            GameObject controller = GameObject.Find(controllerName);
            if (controller == null)
                continue;

            foreach (LineRenderer line in controller.GetComponentsInChildren<LineRenderer>(true))
                line.gameObject.layer = rayLayer;
        }

        return rayLayer;
    }
}
