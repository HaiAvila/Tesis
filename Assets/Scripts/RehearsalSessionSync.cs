using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Remote-rehearsal flow on top of RehearsalSessionState (the synced model, on this same
// object): the director's selected shot, session start/end and the command channel. The
// director writes; the actor follows - loading the selected shot's scene, and going back to
// Actor_HomeMenu when the director ends the rehearsal or disconnects.
//
// The shot number is a property of RehearsalSessionStateModel. It used to be encoded in a
// RealtimeTransform's position.x (from a time the RealtimeModel source generator seemed not
// to run in this project), polled every frame against a baseline taken on connect.
public class RehearsalSessionSync : MonoBehaviour
{
    public const int NoShotSelected = RehearsalSessionState.NoShotSelected;

    public static RehearsalSessionSync Instance { get; private set; }

    public event Action<int> ShotSelected;

    RehearsalSessionState sessionState;
#if DIRECTOR_BUILD
    InputAction debugCalibrateAction;
#endif

    void Awake()
    {
        Instance = this;
        sessionState = GetComponent<RehearsalSessionState>();
        if (sessionState == null)
            Debug.LogError("RehearsalSessionSync: no RehearsalSessionState on this object - the rehearsal won't sync.");
        else
            sessionState.ShotChanged += OnShotChanged;

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
    // actually in one (Remote mode), so it's safe to call from paths that are also
    // reachable during Inspección de Escenas. Not called for pause menu Selección de
    // Escenas - that's still the same rehearsal.
    public void EndRemoteRehearsal()
    {
        if (Bootstrap.Mode != Bootstrap.AppMode.Remote || sessionState == null)
            return;

        sessionState.SetState(RehearsalSessionState.StateEnded);
    }
#else
    void OnShotSelectedForActor(int shotNumber)
    {
        // Only in Ensayo Remoto - never from the home menu or Ensayar Solo.
        if (Bootstrap.Mode != Bootstrap.AppMode.Remote)
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

        int currentShotNumber = sessionState.ShotNumber;
        if (currentShotNumber != NoShotSelected)
            LoadSceneForShot(currentShotNumber);
    }

    void OnRehearsalEndedForActor()
    {
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote)
            SendActorHome("El director terminó el ensayo");
    }

    void OnDirectorLeftForActor()
    {
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote)
            SendActorHome("Se perdió la conexión con el director");
    }

    // Exit point: resets the mode, then the same home-menu load as the pause menu's
    // Menú Principal, with a notice shown once on arrival.
    void SendActorHome(string notice)
    {
        Bootstrap.SetMode(Bootstrap.AppMode.None, "sent home: " + notice);
        ActorHomeMenuController.PendingNotice = notice;

        SceneManager.LoadScene("MenuBackdrop");
        SceneManager.LoadScene("Actor_HomeMenu", LoadSceneMode.Additive);
    }
#endif

    // Every change after this client received the model - never the value the room already
    // held on joining (see RehearsalSessionState.ShotChanged). "No shot" (the reset at the
    // start of every rehearsal) isn't a selection.
    void OnShotChanged(int shotNumber)
    {
        if (shotNumber != NoShotSelected)
            ShotSelected?.Invoke(shotNumber);
    }

    // Director only - see RehearsalSessionState.SetShot.
    public void SelectShot(int shotNumber)
    {
        if (sessionState != null)
            sessionState.SetShot(shotNumber);
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
