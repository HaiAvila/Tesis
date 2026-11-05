using System;
using Normal.Realtime;
using UnityEngine;

// Synced remote-rehearsal state, written only by the director (who requests ownership
// before every write). Lives on the same persistent RehearsalSession object as
// RehearsalSessionSync, which is the only thing that talks to it.
//
// Detecting the director leaving: Normcore's Room has no client-left event, but when a
// client leaves the room the server clears ownership of every model it owned. So the
// director's ownership of this model going back to "unowned" means the director
// disconnected or closed the app - no timeout involved.
//
// Command channel (SendCommand / OnCommand): the director sets commandId, then increments
// commandCounter. Every other client fires OnCommand when the counter goes above its own
// baseline, then moves the baseline up to it. The baseline starts at whatever counter the
// client finds when it receives the model (joining the room) and is reset again when the
// actor enters Ensayo Remoto (ResetCommandBaseline) - so a late joiner never replays a
// command that was sent before it arrived. Several increments arriving at once fire only
// the latest command (the ones in between are logged as skipped). The sender doesn't fire
// OnCommand for its own commands.
public class RehearsalSessionState : RealtimeComponent<RehearsalSessionStateModel>
{
    public const int StateNone = 0;
    public const int StateActive = 1;
    public const int StateEnded = 2;

    public const int NoShotSelected = -1;
    // shotNumber value for "the whole scene" (Escena completa): one continuous take of every
    // shot of sceneNumber.
    public const int FullSceneShot = 0;
    // A room whose model predates sceneNumber reads 0 there - anything <= 0 counts as unset.
    public const int NoSceneSelected = -1;

    // Raised on every client (the writer included) when the selected scene or shot changes
    // after this client received the model - once per property, so a pick that changes
    // both raises it twice. Whatever the room already held when joining doesn't raise it - a
    // late joiner reads SceneNumber/ShotNumber explicitly instead (see
    // RehearsalSessionSync.JoinRemoteRehearsal), and only while the rehearsal is live.
    public event Action SelectionChanged;

    const int NoOwner = -1;

    // Raised on every client whenever the state becomes Ended.
    public event Action RehearsalEnded;

    // Raised on clients other than the previous owner when that owner's ownership is
    // cleared, i.e. the director left the room.
    public event Action OwnerLeft;

    // Raised on receivers (not the sender) for each new command above the baseline.
    public event Action<SessionCommand> OnCommand;

    // Raised on the sender itself, for each command it sends - for things on the director's
    // own machine that must react too (in testing, the Editor is both the director and the
    // machine with the pods, and OnCommand never fires for its own commands).
    public static event Action<SessionCommand> CommandSentLocally;

    // After a disconnect the model is a fresh one, not in any datastore: writing to it throws.
    bool Online => model != null && realtime != null && realtime.connected;

    int lastKnownOwnerID = NoOwner;
    int commandBaseline;
    int lastSentCounter = -1;

    public int State => model != null ? model.sessionState : StateNone;

    public bool HasOwner => model != null && model.ownerIDSelf != NoOwner;

    public int ShotNumber => model != null ? model.shotNumber : NoShotSelected;

    public int SceneNumber => model != null ? model.sceneNumber : NoSceneSelected;

    // HasOwner: a director who left mid-take can't clear it, and shouldn't block anyone.
    public bool IsRecording => model != null && model.isRecording && HasOwner;

    // Director only, same ownership pattern as SetState. Scene first, then the shot.
    public void SetSelection(int sceneNumber, int shotNumber)
    {
        if (!Online)
        {
            Debug.LogWarning($"[SessionSync] SetSelection(scene {sceneNumber}, shot {shotNumber}) ignored: not connected to a room yet.");
            return;
        }

        model.RequestOwnership();
        model.sceneNumber = sceneNumber;
        model.shotNumber = shotNumber;
        Debug.Log($"[SessionSync] write sceneNumber={sceneNumber}, shotNumber={shotNumber}" + (shotNumber == FullSceneShot ? " (escena completa)" : ""));
    }

    // Director only. Requests ownership first, same explicit pattern as every other synced
    // write in this project (RehearsalSessionSync.SelectTake, ActorPositionSync).
    public void SetState(int state)
    {
        if (!Online)
            return;

        model.RequestOwnership();
        model.sessionState = state;
        Debug.Log($"[SessionSync] write sessionState={state}");
    }

    // Director only, same ownership pattern as SetState.
    public void SetRecording(bool recording)
    {
        if (!Online)
            return;

        model.RequestOwnership();
        model.isRecording = recording;
    }

    // Director only. commandId first, then the counter - receivers react to the counter.
    public void SendCommand(SessionCommand command)
    {
        if (!Online)
        {
            // This machine may be the one it's for (the pods here, in testing).
            Debug.LogWarning($"[SessionSync] SendCommand({command}) not sent: not connected to a room.");
            CommandSentLocally?.Invoke(command);
            return;
        }

        model.RequestOwnership();
        model.commandId = (int)command;
        int counter = model.commandCounter + 1;
        lastSentCounter = counter;
        commandBaseline = counter;
        model.commandCounter = counter;
        Debug.Log($"[SessionSync] write command {command} (commandId={(int)command}, commandCounter={counter})");
        CommandSentLocally?.Invoke(command);
    }

    // Called when entering Ensayo Remoto: nothing sent before this moment will fire.
    public void ResetCommandBaseline()
    {
        commandBaseline = model != null ? model.commandCounter : 0;
        Debug.Log($"[SessionSync] command baseline reset to {commandBaseline}");
    }

    protected override void OnRealtimeModelReplaced(RehearsalSessionStateModel previousModel, RehearsalSessionStateModel currentModel)
    {
        if (previousModel != null)
        {
            previousModel.sessionStateDidChange -= OnSessionStateDidChange;
            previousModel.ownerIDSelfDidChange -= OnOwnerIDDidChange;
            previousModel.commandCounterDidChange -= OnCommandCounterDidChange;
            previousModel.shotNumberDidChange -= OnShotNumberDidChange;
            previousModel.sceneNumberDidChange -= OnSceneNumberDidChange;
        }

        if (currentModel != null)
        {
            if (currentModel.isFreshModel)
            {
                currentModel.sessionState = StateNone;
                currentModel.sceneNumber = NoSceneSelected;
                currentModel.shotNumber = NoShotSelected;
            }

            lastKnownOwnerID = currentModel.ownerIDSelf;
            commandBaseline = currentModel.commandCounter;
            Debug.Log($"[SessionSync] joined: sessionState={currentModel.sessionState}, sceneNumber={currentModel.sceneNumber}, shotNumber={currentModel.shotNumber}, commandCounter={currentModel.commandCounter} (baseline), owner={currentModel.ownerIDSelf}");

            currentModel.sessionStateDidChange += OnSessionStateDidChange;
            currentModel.ownerIDSelfDidChange += OnOwnerIDDidChange;
            currentModel.commandCounterDidChange += OnCommandCounterDidChange;
            currentModel.shotNumberDidChange += OnShotNumberDidChange;
            currentModel.sceneNumberDidChange += OnSceneNumberDidChange;
        }
    }

    void OnShotNumberDidChange(RehearsalSessionStateModel changedModel, int value)
    {
        Debug.Log($"[SessionSync] received shotNumber={value}");
        SelectionChanged?.Invoke();
    }

    void OnSceneNumberDidChange(RehearsalSessionStateModel changedModel, int value)
    {
        Debug.Log($"[SessionSync] received sceneNumber={value}");
        SelectionChanged?.Invoke();
    }

    void OnSessionStateDidChange(RehearsalSessionStateModel changedModel, int value)
    {
        Debug.Log($"[SessionSync] received sessionState={value}");
        if (value == StateEnded)
            RehearsalEnded?.Invoke();
    }

    void OnOwnerIDDidChange(RealtimeModel changedModel, int ownerID)
    {
        int previousOwnerID = lastKnownOwnerID;
        lastKnownOwnerID = ownerID;
        Debug.Log($"[SessionSync] received owner {previousOwnerID} -> {ownerID}");

        bool ownerWasSomeoneElse = previousOwnerID != NoOwner && previousOwnerID != realtime.clientID;
        if (ownerID == NoOwner && ownerWasSomeoneElse)
            OwnerLeft?.Invoke();
    }

    void OnCommandCounterDidChange(RehearsalSessionStateModel changedModel, int counter)
    {
        var command = (SessionCommand)changedModel.commandId;

        if (counter == lastSentCounter)
        {
            Debug.Log($"[SessionSync] command {command} counter={counter}: own command, not fired here");
            return;
        }

        if (counter <= commandBaseline)
        {
            Debug.Log($"[SessionSync] command {command} ignored: counter={counter} <= baseline={commandBaseline}");
            return;
        }

#if !DIRECTOR_BUILD
        // Ensayar Solo (or the home menu) ignores the director's commands. The baseline still
        // moves up, so nothing from now is replayed later either.
        if (Bootstrap.Mode != Bootstrap.AppMode.Remote)
        {
            Debug.Log($"[SessionSync] command {command} ignored: not in Ensayo Remoto (counter={counter}, baseline={commandBaseline})");
            commandBaseline = counter;
            return;
        }
#endif

        int skipped = counter - commandBaseline - 1;
        Debug.Log($"[SessionSync] command {command} fired: counter={counter}, baseline={commandBaseline}" + (skipped > 0 ? $", {skipped} earlier command(s) skipped" : ""));
        commandBaseline = counter;
        OnCommand?.Invoke(command);
    }
}
