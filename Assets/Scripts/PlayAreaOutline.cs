using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

// The Quest's boundary, on both builds, in a take.
//
// Actor (Ensayo Remoto and Ensayar Solo): it isn't drawn, since the Quest shows its own limit and
// a line on the floor added nothing. TutorialController reads IsTiny to say when it needs
// redrawing, and ActorRigSync sends it to the director.
//
// Director (Ensayo Remoto): both limits on the set's floor, in the UI layer so the virtual camera
// doesn't film them. Their own in blue, on their rig. The actor's in red, placed with the actor's
// synced rig pose: when the actor presses X or moves with the joystick, their real room moves
// within the set and the line goes with it.
//
// The boundary comes from OpenXR through XRInputSubsystem.TryGetBoundaryPoints (the Editor reports
// none). On a Quest the app starts on a tiny Stationary boundary, even after a Roomscale one was
// drawn; walking out of it lets the user create a new one. Read on every take, whenever the
// headset reports a new boundary, and when coming back from the Quest's own screens. The points
// are in tracking space, kept in the rig's frame, at floor level.
//
// Drawn like the start mark: thin unlit UI images on flat world-space canvases, no colliders, no
// raycast targets.
public class PlayAreaOutline : MonoBehaviour
{
    // Meta asks for at least 2 × 2 m to draw a Roomscale boundary, and a Stationary one is about
    // 1 × 1 m.
    const float TinySideMeters = 1.5f;

    const float LineWidth = 0.03f;
    const float FloorOffset = 0.03f; // a touch above the start mark (2.5 cm) so they never fight
    const int BytesPerPoint = 8;
    const int MaxPoints = 64;

    static readonly Color OwnLimitColor = new Color(0.25f, 0.6f, 1f, 0.75f);
    static readonly Color ActorLimitColor = new Color(1f, 0.2f, 0.2f, 0.85f);

    public static PlayAreaOutline Instance { get; private set; }

    public bool HasReading { get; private set; }
    public float Width { get; private set; }
    public float Depth { get; private set; }
    public bool IsTiny => HasReading && Mathf.Min(Width, Depth) < TinySideMeters;

    // In the rig's frame; y is ignored. Goes up by one with each new reading.
    public IReadOnlyList<Vector3> RigLocalPoints => rigLocalPoints;
    public int LimitVersion { get; private set; }

    readonly List<Vector3> boundaryPoints = new List<Vector3>();
    readonly List<Vector3> rigLocalPoints = new List<Vector3>();
    XRInputSubsystem subsystem;
    Coroutine readCoroutine;
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

    static bool InTake
    {
        get
        {
            if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
                return false;
#if DIRECTOR_BUILD
            return Bootstrap.Mode == Bootstrap.AppMode.Remote;
#else
            return Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
#endif
        }
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (readCoroutine != null)
            StopCoroutine(readCoroutine);
        readCoroutine = null;

        if (InTake)
            readCoroutine = StartCoroutine(ReadNextFrame());
    }

    System.Collections.IEnumerator ReadNextFrame()
    {
        yield return null;
        readCoroutine = null;
        Read();
    }

    void OnBoundaryChanged(XRInputSubsystem changedSubsystem)
    {
        if (InTake)
            Read();
    }

    // Back from the Quest's own boundary screens, in case boundaryChanged didn't fire.
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && InTake)
            Read();
    }

    void Read()
    {
        HasReading = false;
        rigLocalPoints.Clear();

        XRInputSubsystem input = FindInputSubsystem();
        if (input != subsystem)
        {
            if (subsystem != null)
                subsystem.boundaryChanged -= OnBoundaryChanged;
            subsystem = input;
            if (subsystem != null)
                subsystem.boundaryChanged += OnBoundaryChanged;
        }

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        bool hasBoundary = subsystem != null && subsystem.TryGetBoundaryPoints(boundaryPoints) && boundaryPoints.Count >= 3;
        if (!hasBoundary || origin == null || origin.CameraFloorOffsetObject == null)
        {
            if (!loggedNoBoundary)
                Debug.Log("[PlayArea] the headset reported no play area (normal in the Editor).");
            loggedNoBoundary = true;
            LimitVersion++;
            OnPointsChanged();
            return;
        }

        // Into the rig root's frame: the tracking-space points are relative to the camera floor
        // offset object, whose height depends on the tracking origin mode, so only their
        // horizontal position is kept.
        Transform rig = origin.transform;
        Transform trackingSpace = origin.CameraFloorOffsetObject.transform;
        foreach (Vector3 point in boundaryPoints)
        {
            Vector3 p = rig.InverseTransformPoint(trackingSpace.TransformPoint(point));
            rigLocalPoints.Add(new Vector3(p.x, 0f, p.z));
        }

        Vector3 min = rigLocalPoints[0], max = rigLocalPoints[0];
        foreach (Vector3 point in rigLocalPoints)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        loggedNoBoundary = false;
        Width = max.x - min.x;
        Depth = max.z - min.z;
        HasReading = true;
        LimitVersion++;
        Debug.Log($"[PlayArea] play area: {rigLocalPoints.Count} corners, about {Width:F1} × {Depth:F1} m");
        OnPointsChanged();
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

    public static byte[] EncodePoints(IReadOnlyList<Vector3> points)
    {
        int count = Mathf.Min(points.Count, MaxPoints);
        var data = new byte[count * BytesPerPoint];
        for (int i = 0; i < count; i++)
        {
            Array.Copy(BitConverter.GetBytes(points[i].x), 0, data, i * BytesPerPoint, 4);
            Array.Copy(BitConverter.GetBytes(points[i].z), 0, data, i * BytesPerPoint + 4, 4);
        }
        return data;
    }

#if DIRECTOR_BUILD
    // On uneven ground a flat line would keep vanishing under the bumps, so it's drawn on top.
    static Material lineMaterial;

    static Material LineMaterial
    {
        get
        {
            if (lineMaterial == null)
            {
                lineMaterial = new Material(Graphic.defaultGraphicMaterial);
                lineMaterial.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            return lineMaterial;
        }
    }

    GameObject ownLine;
    GameObject actorLine;
    byte[] drawnActorPoints;

    void OnPointsChanged()
    {
        if (ownLine != null)
            Destroy(ownLine);
        ownLine = null;

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        if (HasReading && origin != null)
            ownLine = BuildLine("OwnLimit", origin.transform, rigLocalPoints, OwnLimitColor);
    }

    void Update()
    {
        ActorRigSync rig = ActorRigSync.Instance;
        byte[] points = rig != null && rig.HasActor && InTake ? rig.LimitPoints : null;
        if (points == null || points.Length < BytesPerPoint * 3)
        {
            if (actorLine != null)
                Destroy(actorLine);
            actorLine = null;
            drawnActorPoints = null;
            return;
        }

        if (actorLine == null || !ReferenceEquals(points, drawnActorPoints))
        {
            if (actorLine != null)
                Destroy(actorLine);

            var decoded = new List<Vector3>(points.Length / BytesPerPoint);
            for (int o = 0; o + BytesPerPoint <= points.Length; o += BytesPerPoint)
                decoded.Add(new Vector3(BitConverter.ToSingle(points, o), 0f, BitConverter.ToSingle(points, o + 4)));

            actorLine = BuildLine("ActorLimit", null, decoded, ActorLimitColor);
            drawnActorPoints = points;
        }

        actorLine.transform.SetPositionAndRotation(rig.RigPosition, Quaternion.Euler(0f, rig.RigYaw, 0f));
    }

    static GameObject BuildLine(string name, Transform parent, IReadOnlyList<Vector3> points, Color color)
    {
        var root = new GameObject(name);
        if (parent != null)
            root.transform.SetParent(parent, false);

        for (int i = 0; i < points.Count; i++)
            CreateEdge(root.transform, points[i], points[(i + 1) % points.Count], color);

        XRUISetup.MoveToUILayer(root);
        return root;
    }

    static void CreateEdge(Transform parent, Vector3 from, Vector3 to, Color color)
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
        rect.localPosition = (from + to) / 2f + Vector3.up * FloorOffset;
        rect.localRotation = Quaternion.LookRotation(Vector3.down, along / length);
        rect.localScale = Vector3.one * 0.001f;
        rect.sizeDelta = new Vector2(LineWidth, length) * 1000f;

        Image image = edge.GetComponent<Image>();
        image.color = color;
        image.material = LineMaterial;
        image.raycastTarget = false;
    }
#else
    void OnPointsChanged()
    {
    }
#endif
}
