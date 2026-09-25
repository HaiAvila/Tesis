using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Bootstrap : MonoBehaviour
{
    public static int CurrentShotNumber;

    // True while picking a shot in SceneSelect is for solo review (Inspección de Escenas,
    // or the actor practicing alone), not a live synced rehearsal with a director/actor on
    // the other end. Every entry point into SceneSelect must set this explicitly - it's
    // never inferred - and every return to the home menu resets it to false (neutral), so
    // a value from an earlier session can never survive into a new one unnoticed.
    public static bool IsInspecting;

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

        SceneManager.LoadScene("MenuBackdrop");

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu", LoadSceneMode.Additive);
#else
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
#endif
    }
}
