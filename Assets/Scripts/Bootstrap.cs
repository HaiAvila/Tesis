using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Bootstrap : MonoBehaviour
{
    public static int CurrentShotNumber;

    // Explicit session to load when a content scene opens in Inspección de Escenas - set
    // when the director picked a specific session from the multi-session list or jumped in
    // from Favoritos. Null means "no specific pick", which falls back to the shot's most
    // recent session (the original, single-session behavior). Always consumed (reset to
    // null) by RecordingPlaybackController right after it's read, so a stale value can
    // never leak into an unrelated later shot selection.
    public static int? CurrentSessionId;

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

    public static void SetMode(AppMode mode, string reason)
    {
        if (Mode == mode)
            return;

        Debug.Log($"[Solo] mode {Mode} -> {mode} ({reason})");
        Mode = mode;
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

        LoadHomeMenu();
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
