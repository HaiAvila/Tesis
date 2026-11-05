using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

// Actor only (Ensayo Remoto and Ensayar Solo): how much real room the actor has - the Quest's
// boundary. It isn't drawn for the actor: the Quest shows its own limit, and a line on the floor
// added nothing. TutorialController reads IsTiny to say when the limit needs redrawing.
//
// The boundary comes from OpenXR through XRInputSubsystem.TryGetBoundaryPoints (the Editor reports
// none). On a Quest the app starts on a tiny Stationary boundary, even after a Roomscale one was
// drawn; walking out of it lets the actor create a new one. Read on every take, whenever the
// headset reports a new boundary, and when coming back from the Quest's own screens.
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
    readonly List<Vector3> boundaryPoints = new List<Vector3>();
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

    static bool InActorTake =>
        (Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo)
        && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (readCoroutine != null)
            StopCoroutine(readCoroutine);
        readCoroutine = null;

        bool actorFlow = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
        if (actorFlow && Bootstrap.IsContentScene(scene.name))
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
        if (InActorTake)
            Read();
    }

    // Back from the Quest's own boundary screens, in case boundaryChanged didn't fire.
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && InActorTake)
            Read();
    }

    void Read()
    {
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
                Debug.Log("[PlayArea] the headset reported no play area (normal in the Editor).");
            loggedNoBoundary = true;
            return;
        }

        Vector3 min = boundaryPoints[0], max = boundaryPoints[0];
        foreach (Vector3 point in boundaryPoints)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        loggedNoBoundary = false;
        Width = max.x - min.x;
        Depth = max.z - min.z;
        HasReading = true;
        Debug.Log($"[PlayArea] play area: {boundaryPoints.Count} corners, about {Width:F1} × {Depth:F1} m");
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
