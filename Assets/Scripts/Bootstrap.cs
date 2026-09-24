using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Bootstrap : MonoBehaviour
{
    public static int CurrentShotNumber;

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

#if DIRECTOR_BUILD
        SceneManager.LoadScene("Director_HomeMenu");
#else
        SceneManager.LoadScene("Actor_HomeMenu");
#endif
    }
}
