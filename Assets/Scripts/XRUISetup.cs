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
}
