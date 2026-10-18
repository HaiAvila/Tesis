using UnityEngine;
using UnityEngine.SceneManagement;

// Persistent (on the "SoloSession" object under Bootstrap, next to SoloSession). Drives the
// actor's Ensayar Solo (Bootstrap.Mode == Solo): writes the loaded take into SoloSession,
// and clears it on leaving solo. Solo is its own mode - the director's tools and events
// never act in it (each checks Bootstrap.Mode) - and it never touches the director-owned
// RehearsalSessionStateModel.
//
// Uses the room connection Bootstrap already has. If the model isn't there yet when a take
// loads (still connecting) or comes back after a reconnect, the take is written again as
// soon as SoloSession.ModelReady fires.
public class SoloRehearsalController : MonoBehaviour
{
    int currentSceneNumber = SoloSession.NoNumber;
    int currentShotNumber = SoloSession.NoNumber;

    bool InTake => currentShotNumber != SoloSession.NoNumber;

    void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        Bootstrap.ModeChanged += OnModeChanged;
    }

    void Start()
    {
        if (SoloSession.Instance != null)
            SoloSession.Instance.ModelReady += OnModelReady;
        else
            Debug.LogError("[Solo] no SoloSession found - solo won't be synced.");
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Bootstrap.ModeChanged -= OnModeChanged;
        if (SoloSession.Instance != null)
            SoloSession.Instance.ModelReady -= OnModelReady;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (Bootstrap.Mode != Bootstrap.AppMode.Solo)
            return;

        if (!Bootstrap.IsContentScene(scene.name))
        {
            if (InTake)
                LeaveTake("left the take");
            return;
        }

        EnterTake(Bootstrap.CurrentShotNumber);
    }

    void OnModeChanged(Bootstrap.AppMode previous, Bootstrap.AppMode current)
    {
        if (previous != Bootstrap.AppMode.Solo)
            return;

        if (InTake)
            LeaveTake($"mode {previous} -> {current}");

        if (SoloSession.Instance != null)
            SoloSession.Instance.End();
    }

    void OnModelReady()
    {
        if (Bootstrap.Mode == Bootstrap.AppMode.Solo && InTake && SoloSession.Instance != null)
            SoloSession.Instance.Begin(currentSceneNumber, currentShotNumber);
    }

    void EnterTake(int shotNumber)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        int? sceneNumber = database != null ? database.GetSceneNumberForShot(shotNumber) : null;

        currentShotNumber = shotNumber;
        currentSceneNumber = sceneNumber ?? SoloSession.NoNumber;
        Debug.Log($"[Solo] entered take: scene {currentSceneNumber}, shot {currentShotNumber}");

        if (SoloSession.Instance != null)
            SoloSession.Instance.Begin(currentSceneNumber, currentShotNumber);
    }

    void LeaveTake(string reason)
    {
        Debug.Log($"[Solo] left take: scene {currentSceneNumber}, shot {currentShotNumber} ({reason})");
        currentSceneNumber = SoloSession.NoNumber;
        currentShotNumber = SoloSession.NoNumber;
    }
}
