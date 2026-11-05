using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Remote-rehearsal flow on top of RehearsalSessionState (the synced model, on this same
// object): the director's selected take, session start/end and the command channel. The
// director writes; the actor follows - loading the selected take's scene, and going back to
// Actor_HomeMenu when the director ends the rehearsal or disconnects.
//
// The take is two properties of RehearsalSessionStateModel: sceneNumber and shotNumber, where
// shotNumber = FullSceneShot (0) is the whole scene ("Escena completa"). The actor gets the
// scene straight from the model; it only derives it from the shot (ShotListDatabase) when the
// room's model predates sceneNumber. The shot number used to be encoded in a
// RealtimeTransform's position.x (from a time the RealtimeModel source generator seemed not
// to run in this project), polled every frame against a baseline taken on connect.
public class RehearsalSessionSync : MonoBehaviour
{
    public const int NoShotSelected = RehearsalSessionState.NoShotSelected;
    public const int NoSceneSelected = RehearsalSessionState.NoSceneSelected;
    public const int FullSceneShot = RehearsalSessionState.FullSceneShot;

    public static RehearsalSessionSync Instance { get; private set; }

    RehearsalSessionState sessionState;
#if DIRECTOR_BUILD
    InputAction debugCalibrateAction;
#else
    Coroutine pendingSelectionCoroutine;
#endif

    void Awake()
    {
        Instance = this;
        sessionState = GetComponent<RehearsalSessionState>();
        if (sessionState == null)
            Debug.LogError("RehearsalSessionSync: no RehearsalSessionState on this object - the rehearsal won't sync.");

#if !DIRECTOR_BUILD
        // Permanent, for the app's entire lifetime - deliberately never unsubscribes and
        // reacts to every future change, not just the first. This used to be a one-shot
        // subscription owned by ActorHomeMenuController, which unsubscribed itself after
        // handling the first shot and was destroyed once the actor left the home menu
        // scene - so any shot the director picked later (e.g. from the pause menu) fired
        // into an empty room, silently. Moved here specifically so it survives every scene
        // change for as long as the app runs.
        //
        // Same "permanent, never unsubscribed" rule for the end/disconnect/command listeners -
        // these have to keep working across every scene change for the whole app lifetime.
        if (sessionState != null)
        {
            sessionState.SelectionChanged += OnSelectionChangedForActor;
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

    public bool IsRecording => sessionState != null && sessionState.IsRecording;

    public void SetRecording(bool recording)
    {
        if (sessionState != null)
            sessionState.SetRecording(recording);
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
        if (command == SessionCommand.SiguienteToma)
            Debug.Log($"[Escena] OnCommand(SiguienteToma) on the actor - no action yet");
        else
            Debug.Log($"[SessionSync] OnCommand({command}) on the actor - no action yet");
    }
#endif

#if DIRECTOR_BUILD
    // Director: the one entry point into Ensayo Remoto. Also clears the synced take, so an
    // actor who joins before the director picks one waits instead of being pulled into
    // whatever take was left over from an earlier rehearsal.
    public void BeginRemoteRehearsal()
    {
        if (sessionState != null)
            sessionState.SetState(RehearsalSessionState.StateActive);

        SelectTake(NoSceneSelected, NoShotSelected);
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
    // Every change after this client received the model - never the value the room already
    // held on joining (see RehearsalSessionState.SelectionChanged). A pick that changes both
    // the scene and the shot raises this twice, and sceneNumber can land after shotNumber in
    // the same update - so the pair is read once, on the next frame, when both are in.
    void OnSelectionChangedForActor()
    {
        if (pendingSelectionCoroutine == null)
            pendingSelectionCoroutine = StartCoroutine(ApplySelectionNextFrame());
    }

    System.Collections.IEnumerator ApplySelectionNextFrame()
    {
        yield return null;
        pendingSelectionCoroutine = null;

        // Only in Ensayo Remoto - never from the home menu or Ensayar Solo. "No shot" (the
        // reset at the start of every rehearsal) isn't a selection.
        if (Bootstrap.Mode != Bootstrap.AppMode.Remote || sessionState == null)
            yield break;

        int shotNumber = sessionState.ShotNumber;
        if (shotNumber != NoShotSelected)
            LoadTake(sessionState.SceneNumber, shotNumber);
    }

    // Actor: called right after pressing Ensayo Remoto. If the director's rehearsal is
    // already running with a take picked (a shot or the whole scene), join it directly
    // instead of waiting for the next change - otherwise an actor joining late would wait
    // until the director happened to pick a new take. Requires a connected director (the
    // model still has an owner), so stale values left over in the room from an earlier
    // session are ignored.
    public void JoinRemoteRehearsal()
    {
        if (sessionState == null
            || sessionState.State != RehearsalSessionState.StateActive
            || !sessionState.HasOwner)
            return;

        int currentShotNumber = sessionState.ShotNumber;
        if (currentShotNumber == NoShotSelected)
            return;

        if (currentShotNumber == FullSceneShot)
            Debug.Log($"[Escena] late join into the full scene {sessionState.SceneNumber}");
        LoadTake(sessionState.SceneNumber, currentShotNumber);
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

    // Loads the take's scene. The scene comes from the model; it's derived from the shot only
    // when the model has none (a room whose model predates sceneNumber), which can't work for
    // the whole scene (shot 0 isn't a row in Shots).
    void LoadTake(int sceneNumber, int shotNumber)
    {
        if (sceneNumber <= 0)
        {
            ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
            int? derived = database != null && shotNumber != FullSceneShot ? database.GetSceneNumberForShot(shotNumber) : null;
            if (derived == null)
            {
                Debug.LogError($"RehearsalSessionSync: no scene in the model and none can be derived for shot {shotNumber}.");
                return;
            }

            sceneNumber = derived.Value;
        }

        if (!SceneSelectController.SceneNameByNumber.TryGetValue(sceneNumber, out string sceneName))
        {
            Debug.LogError($"RehearsalSessionSync: no content scene for scene number {sceneNumber}.");
            return;
        }

        Bootstrap.SelectTake(sceneNumber, shotNumber);
        SceneManager.LoadSceneAsync(sceneName);
    }
#endif

    // Director only - see RehearsalSessionState.SetSelection. Scene first, then the shot.
    public void SelectTake(int sceneNumber, int shotNumber)
    {
        if (sessionState != null)
            sessionState.SetSelection(sceneNumber, shotNumber);
    }
}
