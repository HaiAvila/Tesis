using System;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

// Encodes the director's selected shot number as a synced Transform position
// (position.x) instead of a custom [RealtimeModel]. This project's Unity 6.3 +
// Normcore combination fails to run the RealtimeModel Roslyn source generator
// (selectedShotNumberDidChange never gets generated, compile error every time,
// confirmed even after a full reimport and updating Normcore to 3.6.1). A
// RealtimeTransform is precompiled into the Normcore package - no code
// generation involved - and is already proven working in this project via
// NetworkedCube, so it sidesteps the issue entirely.
[RequireComponent(typeof(RealtimeTransform))]
public class RehearsalSessionSync : MonoBehaviour
{
    public const int NoShotSelected = -1;

    public static RehearsalSessionSync Instance { get; private set; }

    public event Action<int> ShotSelected;

    RealtimeTransform realtimeTransform;
    int lastKnownShotNumber;

    void Awake()
    {
        Instance = this;
        realtimeTransform = GetComponent<RealtimeTransform>();
        lastKnownShotNumber = Mathf.RoundToInt(transform.position.x);

        // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
        Debug.Log($"RehearsalSessionSync: Awake, lastKnownShotNumber={lastKnownShotNumber}");

        // Whatever this synced transform holds right as the connection settles - including
        // a stale value from Normcore's room persistence, carried over from an earlier test
        // session in the same room - is the starting point, not something to react to.
        // Unsubscribes after its one use, same as Bootstrap's own didConnectToRoom handler.
        Realtime realtime = FindFirstObjectByType<Realtime>();
        if (realtime != null)
            realtime.didConnectToRoom += OnConnectedToRoom;

#if !DIRECTOR_BUILD
        // Permanent, for the app's entire lifetime - deliberately never unsubscribes and
        // reacts to every future change, not just the first. This used to be a one-shot
        // subscription owned by ActorHomeMenuController, which unsubscribed itself after
        // handling the first shot and was destroyed once the actor left the home menu
        // scene - so any shot the director picked later (e.g. from the pause menu) fired
        // into an empty room, silently. Moved here specifically so it survives every scene
        // change for as long as the app runs.
        ShotSelected += OnShotSelectedForActor;
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void OnConnectedToRoom(Realtime rt)
    {
        rt.didConnectToRoom -= OnConnectedToRoom;
        lastKnownShotNumber = Mathf.RoundToInt(transform.position.x);
    }

#if !DIRECTOR_BUILD
    void OnShotSelectedForActor(int shotNumber)
    {
        LoadSceneForShot(shotNumber);
    }
#endif

    void Update()
    {
        int currentShotNumber = Mathf.RoundToInt(transform.position.x);
        if (currentShotNumber == lastKnownShotNumber)
            return;

        // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
        Debug.Log($"RehearsalSessionSync: position.x changed {lastKnownShotNumber} -> {currentShotNumber} (isOwnedLocallySelf={realtimeTransform.isOwnedLocallySelf})");

        lastKnownShotNumber = currentShotNumber;

        if (currentShotNumber != NoShotSelected)
            ShotSelected?.Invoke(currentShotNumber);
    }

    public void SelectShot(int shotNumber)
    {
        realtimeTransform.RequestOwnership();
        transform.position = new Vector3(shotNumber, 0f, 0f);

        // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
        Debug.Log($"RehearsalSessionSync: SelectShot({shotNumber}) called, isOwnedLocallySelf={realtimeTransform.isOwnedLocallySelf}");
    }

    public void LoadSceneForShot(int shotNumber)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError($"RehearsalSessionSync: no ShotListDatabase instance found, cannot resolve which scene shot {shotNumber} belongs to.");
            return;
        }

        int? sceneNumber = database.GetSceneNumberForShot(shotNumber);
        if (sceneNumber == null || !SceneSelectController.SceneNameByNumber.TryGetValue(sceneNumber.Value, out string sceneName))
        {
            Debug.LogError($"RehearsalSessionSync: could not resolve a scene for shot {shotNumber}.");
            return;
        }

        // TEMPORARY diagnostic - remove once the "Ensayo Remoto" handoff is confirmed fixed.
        Debug.Log($"RehearsalSessionSync: LoadSceneForShot({shotNumber}) resolved scene '{sceneName}', loading now.");

        Bootstrap.CurrentShotNumber = shotNumber;
        SceneManager.LoadSceneAsync(sceneName);
    }
}
