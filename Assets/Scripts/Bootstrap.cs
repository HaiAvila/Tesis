using System;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Bootstrap : MonoBehaviour
{
    // The take being loaded or played: a scene, and a shot of it - or
    // RehearsalSessionState.FullSceneShot (0) for the whole scene ("Escena completa"). Set
    // only through SelectTake, so the three always agree.
    public static int CurrentSceneNumber { get; private set; } = RehearsalSessionState.NoSceneSelected;
    public static int CurrentShotNumber { get; private set; } = RehearsalSessionState.NoShotSelected;

    // The shot whose Shot{N}_Start the take starts on (ShotStartPositioner, StartMarkAligner,
    // StartMarkTape, FallGuard): the shot itself, or for the whole scene its first shot (the
    // lowest shot_number).
    public static int StartShotNumber { get; private set; } = RehearsalSessionState.NoShotSelected;

    public static bool IsFullScene => CurrentShotNumber == RehearsalSessionState.FullSceneShot;

    public static void SelectTake(int sceneNumber, int shotNumber)
    {
        CurrentSceneNumber = sceneNumber;
        CurrentShotNumber = shotNumber;
        StartShotNumber = shotNumber;

        if (shotNumber != RehearsalSessionState.FullSceneShot)
            return;

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        int? firstShot = database != null ? database.GetFirstShotOfScene(sceneNumber) : null;
        if (firstShot == null)
        {
            Debug.LogError($"[Escena] scene {sceneNumber} has no shots - no start mark for the full scene.");
            return;
        }

        StartShotNumber = firstShot.Value;
        Debug.Log($"[Escena] full scene {sceneNumber} selected: starts on shot {StartShotNumber}");
    }

    // Explicit session to load when a content scene opens in Inspección de Escenas - set
    // when the director picked a specific session from the multi-session list or jumped in
    // from Favoritos. Null means "no specific pick", which falls back to the shot's most
    // recent session (the original, single-session behavior). Always consumed (reset to
    // null) by RecordingPlaybackController right after it's read, so a stale value can
    // never leak into an unrelated later shot selection.
    public static int? CurrentSessionId;

    // Inspección of a shot without opening any take: to walk the set and leave notes. Read once
    // by RecordingPlaybackController.
    public static bool InspectWithoutTake;

    // What the user is doing right now - one explicit value instead of the two booleans
    // (IsInspecting, ActorJoinedRemoteRehearsal) that used to be combined, and overloaded:
    // the actor's Ensayar Solo reused the director's "inspecting" flag, so solo showed the
    // replay panel and the session picker.
    //
    //   None    - home menu / not in any flow.
    //   Remote  - Ensayo Remoto, either role: from the button press through SceneSelect
    //             (director) or the waiting screen (actor) and every take.
    //   Solo    - the actor's Ensayar Solo: only the set, no director tools, ignores the
    //             director entirely (see SoloRehearsalController).
    //   Inspect - the director's Inspección de Escenas (and Favoritos → Ver).
    //
    // Set explicitly at every entry point (the home menu buttons, Favoritos) and reset to
    // None at every exit (initial connect, pause menu Menú Principal, the actor being sent
    // home, a disconnect during Remote). Pause menu → Selección de Escenas is *not* an exit:
    // it stays in the same flow. Every director tool checks for its own mode.
    public enum AppMode { None, Remote, Solo, Inspect }

    public static AppMode Mode { get; private set; } = AppMode.None;

    // (previous, new) - raised after the mode changes.
    public static event Action<AppMode, AppMode> ModeChanged;

    public static void SetMode(AppMode mode, string reason)
    {
        if (Mode == mode)
            return;

        Debug.Log($"[Solo] mode {Mode} -> {mode} ({reason})");
        AppMode previous = Mode;
        Mode = mode;
        ModeChanged?.Invoke(previous, mode);
    }

    static readonly string[] ContentSceneNames = { "Scene1_mountain", "Scene2_spaceship", "Scene3_apartment" };

    // SceneSelect has no camera or XR rig of its own, so it's loaded on top of MenuBackdrop
    // (which has the XR Origin) - same pattern as the home menus. Without the rig the
    // headset would have no tracked view and no controllers to point at the shot list.
    public static void LoadSceneSelect()
    {
        SceneManager.LoadScene("MenuBackdrop");
        SceneManager.LoadScene("SceneSelect", LoadSceneMode.Additive);
    }

    public static bool IsContentScene(string sceneName)
    {
        foreach (string contentScene in ContentSceneNames)
        {
            if (contentScene == sceneName)
                return true;
        }

        return false;
    }

    public Realtime realtime;

    const string ConnectionLostNotice = "Se perdió la conexión. Reconectando...";

    // Connect and disconnect are followed for the whole session, not just the first connect.
    // Reconnecting is Normcore's own AutoReconnect component on the Realtime object
    // (exponential backoff, so a flaky network doesn't get the app rate-limited) - Bootstrap
    // only decides what the user sees.
    bool hasConnectedOnce;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        // Decides which panels are shown when several are up - see PanelFocusManager.
        if (GetComponent<PanelFocusManager>() == null)
            gameObject.AddComponent<PanelFocusManager>();

        // Actor only: puts the shot's start under where the actor stands - see StartMarkAligner.
        if (GetComponent<StartMarkAligner>() == null)
            gameObject.AddComponent<StartMarkAligner>();

        // Actor only: draws the Quest's real play area on the floor - see PlayAreaOutline.
        if (GetComponent<PlayAreaOutline>() == null)
            gameObject.AddComponent<PlayAreaOutline>();

        // Both roles: says when the connection is the problem - see ConnectionStatus.
        if (GetComponent<ConnectionStatus>() == null)
            gameObject.AddComponent<ConnectionStatus>();

        // Actor only: which take this is - see ActorTakeInfo.
        if (GetComponent<ActorTakeInfo>() == null)
            gameObject.AddComponent<ActorTakeInfo>();

        // Director only: spoken notes, and listening to them - see VoiceNoteRecorder.
        if (GetComponent<VoiceNoteRecorder>() == null)
            gameObject.AddComponent<VoiceNoteRecorder>();
        if (GetComponent<VoiceNotesPanel>() == null)
            gameObject.AddComponent<VoiceNotesPanel>();
        if (GetComponent<TakeAudioRecorder>() == null)
            gameObject.AddComponent<TakeAudioRecorder>();

        // Both roles: the director and the actor talking - see VoiceChat.
        if (GetComponent<VoiceChat>() == null)
            gameObject.AddComponent<VoiceChat>();

        // Both roles: what each one gets to know about the pods - see PodStatusController.
        if (GetComponent<PodStatusController>() == null)
            gameObject.AddComponent<PodStatusController>();

        // Both roles: back to the start mark instead of falling into the void - see FallGuard.
        if (GetComponent<FallGuard>() == null)
            gameObject.AddComponent<FallGuard>();

        // Both roles, every take: the yellow tape "T" at the shot's start - see StartMarkTape.
        if (GetComponent<StartMarkTape>() == null)
            gameObject.AddComponent<StartMarkTape>();

        // Every role: the takes and cue timelines, kept the same as the server's - see TakeSync.
        if (GetComponent<SyncLink>() == null)
            gameObject.AddComponent<SyncLink>();
        if (GetComponent<TakeSync>() == null)
            gameObject.AddComponent<TakeSync>();

#if SERVER_BUILD
        // The server .exe: no headset or menus, a window with the pods and the avatar - see ServerApp.
        if (GetComponent<ServerApp>() == null)
            gameObject.AddComponent<ServerApp>();
#endif

        if (realtime == null)
            realtime = GetComponentInChildren<Realtime>();

        realtime.didConnectToRoom += OnConnectedToRoom;
        realtime.didDisconnectFromRoom += OnDisconnectedFromRoom;
    }

    private void OnDestroy()
    {
        if (realtime == null)
            return;

        realtime.didConnectToRoom -= OnConnectedToRoom;
        realtime.didDisconnectFromRoom -= OnDisconnectedFromRoom;
    }

    private void OnConnectedToRoom(Realtime rt)
    {
        // A reconnect: whoever lost the connection mid-rehearsal was already sent home, and
        // anything else (menus, Inspección, Ensayar Solo) works offline and was never
        // interrupted - so nothing to reload.
        if (hasConnectedOnce)
        {
            Debug.Log($"[Bootstrap] reconnected to room '{rt.room.name}' as client {rt.clientID}.");
            return;
        }

        hasConnectedOnce = true;
        Debug.Log($"[Bootstrap] connected to room '{rt.room.name}' as client {rt.clientID}.");

        // Neutral state on the very first entry into the app - never left to whatever a
        // static field happens to hold.
        SetMode(AppMode.None, "first connect");

#if SERVER_BUILD
        // The server stays where it is: its window is built over Bootstrap's own scene.
        Debug.Log("[Bootstrap] server: no menus to load");
#else
        LoadHomeMenu();
#endif
    }

    // Only Ensayo Remoto depends on the other device, so only it is interrupted: the user goes
    // home with a notice while AutoReconnect works. When the director is the one who dropped,
    // the actor is also sent home on its side, by RehearsalSessionState.OwnerLeft. Solo and
    // Inspección keep running (they work offline; Solo's model is written again on
    // reconnect - see SoloRehearsalController).
    private void OnDisconnectedFromRoom(Realtime rt)
    {
        bool wasInRemoteRehearsal = Mode == AppMode.Remote;
        Debug.Log($"[Bootstrap] disconnected from the room (in Ensayo Remoto: {wasInRemoteRehearsal}).");

        if (!wasInRemoteRehearsal)
            return;

#if DIRECTOR_BUILD
        // A take in progress lives only on this device: Guardar or Descartar first, then home.
        if (RehearsalRecorder.Instance != null && RehearsalRecorder.Instance.FinishAfterConnectionLoss(AfterConnectionLossDecision))
            return;
#endif
        GoHomeAfterConnectionLoss();
    }

#if DIRECTOR_BUILD
    // Guardar opens the saved take right here for review, as Inspección would; Descartar goes home.
    static void AfterConnectionLossDecision(int? savedSessionId)
    {
        if (!savedSessionId.HasValue || Mode != AppMode.Remote)
        {
            GoHomeAfterConnectionLoss();
            return;
        }

        SetMode(AppMode.Inspect, "reviewing the take kept after a connection loss");
        CurrentSessionId = savedSessionId;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }
#endif

    static void GoHomeAfterConnectionLoss()
    {
        if (Mode != AppMode.Remote)
            return;

        SetMode(AppMode.None, "disconnected during Ensayo Remoto");
#if DIRECTOR_BUILD
        DirectorHomeMenuController.PendingNotice = ConnectionLostNotice;
#else
        ActorHomeMenuController.PendingNotice = ConnectionLostNotice;
#endif
        LoadHomeMenu();
    }

    // Actor_HomeMenu/Director_HomeMenu have no camera of their own - they rely on
    // MenuBackdrop being loaded first.
    static void LoadHomeMenu()
    {
        SceneManager.LoadScene("MenuBackdrop");

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu", LoadSceneMode.Additive);
#else
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
#endif
    }
}
