using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

// Actor only (Ensayo Remoto and Ensayar Solo): draws the real play area - the Quest's
// boundary - as a light-blue line on the virtual floor, so the actor sees how much real room
// they have and where it sits in the set before walking into the boundary's passthrough.
//
// The boundary comes from OpenXR through XRInputSubsystem.TryGetBoundaryPoints (the Editor reports
// none, and nothing is drawn). On a Quest the app starts on a tiny Stationary boundary, seen as a
// tiny square, even after a Roomscale one was drawn; walking out of it lets the actor create a
// new one. IsTiny lets TutorialController say so. The points are in tracking space, i.e. relative to the rig, so
// the outline is parented to the XR Origin at its feet: when the rig moves - "Fijar inicio
// aquí" (StartMarkAligner) or joystick locomotion - the real room moves within the set and
// the line follows. Rebuilt on every take and whenever the headset reports a new boundary.
//
// Drawn like the start mark: thin unlit UI images on flat world-space canvases, no
// colliders, no raycast targets.
public class PlayAreaOutline : MonoBehaviour
{
    // Meta asks for at least 2 × 2 m to draw a Roomscale boundary, and a Stationary one is about
    // 1 × 1 m.
    const float TinySideMeters = 1.5f;

    public static PlayAreaOutline Instance { get; private set; }

    public bool HasReading { get; private set; }
    public float Width { get; private set; }
    public float Depth { get; private set; }
    public bool IsTiny => HasReading && Mathf.Min(Width, Depth) < TinySideMeters;

#if !DIRECTOR_BUILD
    const float LineWidth = 0.03f;
    const float FloorOffset = 0.03f; // a touch above the start mark (2.5 cm) so they never fight
    static readonly Color LineColor = new Color(0.25f, 0.8f, 1f, 0.6f);

    readonly List<Vector3> boundaryPoints = new List<Vector3>();
    XRInputSubsystem subsystem;
    GameObject outlineRoot;
    Coroutine buildCoroutine;
    bool loggedNoBoundary;

    void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (subsystem != null)
            subsystem.boundaryChanged -= OnBoundaryChanged;
    }

    static bool InActorTake =>
        (Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo)
        && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (buildCoroutine != null)
            StopCoroutine(buildCoroutine);
        buildCoroutine = null;

        // The previous outline belonged to the previous scene's rig.
        outlineRoot = null;

        bool actorFlow = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
        if (actorFlow && Bootstrap.IsContentScene(scene.name))
            buildCoroutine = StartCoroutine(BuildNextFrame());
    }

    System.Collections.IEnumerator BuildNextFrame()
    {
        yield return null;
        buildCoroutine = null;
        Build();
    }

    void OnBoundaryChanged(XRInputSubsystem changedSubsystem)
    {
        if (InActorTake)
            Build();
    }

    // Back from the Quest's own boundary screens, in case boundaryChanged didn't fire.
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && InActorTake)
            Build();
    }

    void Build()
    {
        if (outlineRoot != null)
            Destroy(outlineRoot);
        outlineRoot = null;
        HasReading = false;

        XRInputSubsystem input = FindInputSubsystem();
        if (input != subsystem)
        {
            if (subsystem != null)
                subsystem.boundaryChanged -= OnBoundaryChanged;
            subsystem = input;
            if (subsystem != null)
                subsystem.boundaryChanged += OnBoundaryChanged;
        }

        if (subsystem == null || !subsystem.TryGetBoundaryPoints(boundaryPoints) || boundaryPoints.Count < 3)
        {
            if (!loggedNoBoundary)
                Debug.Log("[PlayArea] the headset reported no play area - nothing drawn (normal in the Editor; on the Quest it needs a Roomscale boundary).");
            loggedNoBoundary = true;
            return;
        }

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        if (origin == null || origin.CameraFloorOffsetObject == null)
            return;

        // Into the rig root's frame, at its feet: the tracking-space points are relative to the
        // camera floor offset object, whose height depends on the tracking origin mode, so only
        // their horizontal position is kept.
        Transform rig = origin.transform;
        Transform trackingSpace = origin.CameraFloorOffsetObject.transform;

        outlineRoot = new GameObject("PlayAreaOutline");
        outlineRoot.transform.SetParent(rig, false);

        var local = new List<Vector3>(boundaryPoints.Count);
        foreach (Vector3 point in boundaryPoints)
        {
            Vector3 p = rig.InverseTransformPoint(trackingSpace.TransformPoint(point));
            local.Add(new Vector3(p.x, FloorOffset, p.z));
        }

        Vector3 min = local[0], max = local[0];
        for (int i = 0; i < local.Count; i++)
        {
            CreateEdge(outlineRoot.transform, local[i], local[(i + 1) % local.Count]);
            min = Vector3.Min(min, local[i]);
            max = Vector3.Max(max, local[i]);
        }

        loggedNoBoundary = false;
        Width = max.x - min.x;
        Depth = max.z - min.z;
        HasReading = true;
        Debug.Log($"[PlayArea] play area drawn: {local.Count} corners, about {max.x - min.x:F1} × {max.z - min.z:F1} m");
    }

    static void CreateEdge(Transform parent, Vector3 from, Vector3 to)
    {
        Vector3 along = to - from;
        along.y = 0f;
        float length = along.magnitude;
        if (length < 0.01f)
            return;

        var edge = new GameObject("Edge", typeof(RectTransform), typeof(Canvas), typeof(Image));
        edge.transform.SetParent(parent, false);
        edge.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        // Flat on the floor, facing up; canvas "up" runs along the edge. 1 canvas unit = 1 mm.
        var rect = (RectTransform)edge.transform;
        rect.localPosition = (from + to) / 2f;
        rect.localRotation = Quaternion.LookRotation(Vector3.down, along / length);
        rect.localScale = Vector3.one * 0.001f;
        rect.sizeDelta = new Vector2(LineWidth, length) * 1000f;

        Image image = edge.GetComponent<Image>();
        image.color = LineColor;
        image.raycastTarget = false;
    }

    static XRInputSubsystem FindInputSubsystem()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (XRInputSubsystem candidate in subsystems)
        {
            if (candidate.running)
                return candidate;
        }
        return subsystems.Count > 0 ? subsystems[0] : null;
    }
#endif
}
