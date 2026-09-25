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
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    // Called right before the actor starts listening for a selection (e.g. clicking
    // "Ensayo Remoto"). Without this, whatever value already sits on this synced
    // transform - room persistence from an earlier session, or a leftover from an
    // earlier "Ensayo Remoto" round this same session - gets compared against as if it
    // were the current baseline, so a director re-selecting that same shot number
    // produces no detectable change and the actor waits forever. Resetting here means
    // only a genuinely new selection made after this point ever fires the event.
    public void ResetBaseline()
    {
        lastKnownShotNumber = Mathf.RoundToInt(transform.position.x);
    }

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
