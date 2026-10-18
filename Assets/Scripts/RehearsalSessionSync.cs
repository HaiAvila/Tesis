using System;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Encodes the director's selected shot number as a synced Transform position
// (position.x) instead of a custom [RealtimeModel]. This project's Unity 6.3 +
// Normcore combination fails to run the RealtimeModel Roslyn source generator
// (selectedShotNumberDidChange never gets generated, compile error every time,
// confirmed even after a full reimport and updating Normcore to 3.6.1). A
// RealtimeTransform is precompiled into the Normcore package - no code
// generation involved - and is already proven working in this project via
// NetworkedCube, so it sidesteps the issue entirely.
//
// (Later finding: the generator does run in this project - RehearsalSessionStateModel is a
// proper [RealtimeModel] and compiles fine. The shot number was left on the proven
// transform path rather than migrated; the rehearsal's active/ended state uses the model.)
//
// Session state (active/ended) lives on RehearsalSessionState, on this same object: the
// director sets it at every entry to and exit from Ensayo Remoto, and the actor is sent back
// to Actor_HomeMenu when it ends or when the director disconnects.
[RequireComponent(typeof(RealtimeTransform))]
public class RehearsalSessionSync : MonoBehaviour
{
    public const int NoShotSelected = -1;

    public static RehearsalSessionSync Instance { get; private set; }

    public event Action<int> ShotSelected;

    RealtimeTransform realtimeTransform;
    RehearsalSessionState sessionState;
    int lastKnownShotNumber;
#if DIRECTOR_BUILD
    InputAction debugCalibrateAction;
#endif

    void Awake()
    {
        Instance = this;
        realtimeTransform = GetComponent<RealtimeTransform>();
        sessionState = GetComponent<RehearsalSessionState>();
        if (sessionState == null)
            Debug.LogError("RehearsalSessionSync: no RehearsalSessionState on this object - rehearsal start/end won't sync.");
        lastKnownShotNumber = Mathf.RoundToInt(transform.position.x);

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

        // Same "permanent, never unsubscribed" rule as the shot listener above - these
        // have to keep working across every scene change for the whole app lifetime.
        if (sessionState != null)
        {
            sessionState.RehearsalEnded += OnRehearsalEndedForActor;
            sessionState.OwnerLeft += OnDirectorLeftForActor;
            sessionState.OnCommand += OnCommandForActor;
        }
#endif

#if DIRECTOR_BUILD
        // TEMPORARY debug - C sends Calibrate, to test the command channel before any real
        // button sends commands. Remove once a real sender exists.
        debugCalibrateAction = new InputAction("DebugSendCalibrate", InputActionType.Button, "<Keyboard>/c");
        debugCalibrateAction.performed += _ => SendCommand(SessionCommand.Calibrate);
        debugCalibrateAction.Enable();
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        debugCalibrateAction?.Disable();
        debugCalibrateAction?.Dispose();
#endif
    }

    // Command channel - see RehearsalSessionState. SendCommand is for the director.
    public void SendCommand(SessionCommand command)
    {
        if (sessionState != null)
            sessionState.SendCommand(command);
    }

    public void ResetCommandBaseline()
    {
        if (sessionState != null)
            sessionState.ResetCommandBaseline();
    }

#if !DIRECTOR_BUILD
    // No command does anything yet - this only proves the actor receives each one once.
    void OnCommandForActor(SessionCommand command)
    {
        Debug.Log($"[SessionSync] OnCommand({command}) on the actor - no action yet");
    }
#endif

    void OnConnectedToRoom(Realtime rt)
    {
        rt.didConnectToRoom -= OnConnectedToRoom;
        lastKnownShotNumber = Mathf.RoundToInt(transform.position.x);
    }

#if DIRECTOR_BUILD
    // Director: the one entry point into Ensayo Remoto. Also clears the synced shot, so an
    // actor who joins before the director picks one waits instead of being pulled into
    // whatever shot was left over from an earlier rehearsal.
    public void BeginRemoteRehearsal()
    {
        if (sessionState != null)
            sessionState.SetState(RehearsalSessionState.StateActive);

        SelectShot(NoShotSelected);
    }

    // Director: call at every exit from Ensayo Remoto. A no-op unless the director is
    // actually in one (IsInspecting false), so it's safe to call from paths that are also
    // reachable during Inspección de Escenas. Not called for pause menu Selección de
    // Escenas - that's still the same rehearsal.
    public void EndRemoteRehearsal()
    {
        if (Bootstrap.IsInspecting || sessionState == null)
            return;

        sessionState.SetState(RehearsalSessionState.StateEnded);
    }
#else
    void OnShotSelectedForActor(int shotNumber)
    {
        // Only while the actor has joined a remote rehearsal - never from the home menu or
        // Ensayar Solo.
        if (!Bootstrap.ActorJoinedRemoteRehearsal)
            return;

        LoadSceneForShot(shotNumber);
    }

    // Actor: called right after pressing Ensayo Remoto. If the director's rehearsal is
    // already running with a shot picked, join it directly instead of waiting for the next
    // shot change - otherwise an actor joining late would wait until the director happened
    // to pick a new shot. Requires a connected director (the model still has an owner), so
    // stale values left over in the room from an earlier session are ignored.
    public void JoinRemoteRehearsal()
    {
        if (sessionState == null
            || sessionState.State != RehearsalSessionState.StateActive
            || !sessionState.HasOwner)
            return;

        int currentShotNumber = Mathf.RoundToInt(transform.position.x);
        if (currentShotNumber != NoShotSelected)
            LoadSceneForShot(currentShotNumber);
    }

    void OnRehearsalEndedForActor()
    {
        if (Bootstrap.ActorJoinedRemoteRehearsal)
            SendActorHome("El director terminó el ensayo");
    }

    void OnDirectorLeftForActor()
    {
        if (Bootstrap.ActorJoinedRemoteRehearsal)
            SendActorHome("Se perdió la conexión con el director");
    }

    // Exit point: clears the joined flag, then the same home-menu load as the pause menu's
    // Menú Principal, with a notice shown once on arrival.
    void SendActorHome(string notice)
    {
        Bootstrap.ActorJoinedRemoteRehearsal = false;
        Bootstrap.IsInspecting = false;
        ActorHomeMenuController.PendingNotice = notice;

        SceneManager.LoadScene("MenuBackdrop");
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
    }
#endif

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
