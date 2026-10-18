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

    // True while picking a shot in SceneSelect is for solo review (Inspección de Escenas,
    // or the actor practicing alone), not a live synced rehearsal with a director/actor on
    // the other end. Every entry point into SceneSelect must set this explicitly - it's
    // never inferred - and every return to the home menu resets it to false (neutral), so
    // a value from an earlier session can never survive into a new one unnoticed.
    public static bool IsInspecting;

    // Actor only: true while the actor has joined a remote rehearsal - pressed Ensayo Remoto
    // and is either waiting or in a content scene. RehearsalSessionSync only lets the
    // director's shot selections (and end/disconnect) move the actor while this is set, so
    // an actor on the home menu or in Ensayar Solo is never pulled anywhere. Same rule as
    // IsInspecting: set explicitly at the one entry point (ActorHomeMenuController's Ensayo
    // Remoto button) and cleared at every exit - initial connect, Ensayar Solo, pause menu
    // Menú Principal, and the director ending the rehearsal or disconnecting.
    public static bool ActorJoinedRemoteRehearsal;

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

        // Neutral state on the very first entry into the app - see IsInspecting's own
        // comment for why this must never be left to whatever a static field defaults to.
        IsInspecting = false;
        ActorJoinedRemoteRehearsal = false;

        LoadHomeMenu();
    }

    // Only Ensayo Remoto needs the room, so only it is interrupted: the user goes home with a
    // notice while AutoReconnect works. When the director is the one who dropped, the actor
    // is also sent home on its side, by RehearsalSessionState.OwnerLeft.
    private void OnDisconnectedFromRoom(Realtime rt)
    {
        bool wasInRemoteRehearsal = IsInRemoteRehearsal();
        Debug.Log($"[Bootstrap] disconnected from the room (in Ensayo Remoto: {wasInRemoteRehearsal}).");

        if (!wasInRemoteRehearsal)
            return;

        ActorJoinedRemoteRehearsal = false;
        IsInspecting = false;
#if DIRECTOR_BUILD
        DirectorHomeMenuController.PendingNotice = ConnectionLostNotice;
#else
        ActorHomeMenuController.PendingNotice = ConnectionLostNotice;
#endif
        LoadHomeMenu();
    }

    static bool IsInRemoteRehearsal()
    {
#if DIRECTOR_BUILD
        // From the moment Ensayo Remoto was picked: SceneSelect (choosing the shot) or a take.
        string activeScene = SceneManager.GetActiveScene().name;
        bool inRemoteFlow = IsContentScene(activeScene) || SceneManager.GetSceneByName("SceneSelect").isLoaded;
        return inRemoteFlow && !IsInspecting;
#else
        // Waiting on the home menu or in a take.
        return ActorJoinedRemoteRehearsal;
#endif
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
