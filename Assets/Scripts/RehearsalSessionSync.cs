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
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        int currentShotNumber = Mathf.RoundToInt(transform.position.x);
        if (currentShotNumber == lastKnownShotNumber)
            return;

        lastKnownShotNumber = currentShotNumber;

        if (currentShotNumber != NoShotSelected)
            ShotSelected?.Invoke(currentShotNumber);
    }

    public void SelectShot(int shotNumber)
    {
        realtimeTransform.RequestOwnership();
        transform.position = new Vector3(shotNumber, 0f, 0f);
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

        Bootstrap.CurrentShotNumber = shotNumber;
        SceneManager.LoadSceneAsync(sceneName);
    }
}
