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

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        if (realtime == null)
            realtime = GetComponentInChildren<Realtime>();

        realtime.didConnectToRoom += OnConnectedToRoom;
    }

    private void OnConnectedToRoom(Realtime rt)
    {
        realtime.didConnectToRoom -= OnConnectedToRoom;

        // Neutral state on the very first entry into the app - see IsInspecting's own
        // comment for why this must never be left to whatever a static field defaults to.
        IsInspecting = false;
        ActorJoinedRemoteRehearsal = false;

        SceneManager.LoadScene("MenuBackdrop");

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu", LoadSceneMode.Additive);
#else
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
#endif
    }
}
