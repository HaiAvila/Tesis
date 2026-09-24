using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Bootstrap : MonoBehaviour
{
    public static int CurrentShotNumber;

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

        SceneManager.LoadScene("MenuBackdrop");

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu", LoadSceneMode.Additive);
#else
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
#endif
    }
}
