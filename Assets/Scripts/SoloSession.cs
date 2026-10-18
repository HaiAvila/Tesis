using System;
using Normal.Realtime;
using UnityEngine;

// Synced state of the actor's Ensayar Solo (SoloSessionModel), on its own persistent
// "SoloSession" object under Bootstrap. Driven by SoloRehearsalController.
//
// Ownership: the actor requests it on every write while in solo and clears it on leaving,
// so the model is only ever "owned" while somebody is actually practicing.
//
// Command channel: same contract as RehearsalSessionState's. The sender sets commandId and
// then increments commandCounter. Every other client fires OnCommand only when the counter
// goes above its own baseline (taken when it receives the model, i.e. on joining the room),
// so a late joiner never replays an old command. The sender never fires its own.
public class SoloSession : RealtimeComponent<SoloSessionModel>
{
    public const int NoNumber = -1;

    public static SoloSession Instance { get; private set; }

    // Raised on receivers (not the sender) for each new command above the baseline.
    public event Action<SoloCommand> OnCommand;

    // Raised whenever a model arrives (first join, or a reconnect), so the current take can
    // be written again.
    public event Action ModelReady;

    int commandBaseline;
    int lastSentCounter = -1;

    public bool IsConnected => model != null;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    // Actor, when a take loads in solo.
    public void Begin(int sceneNumber, int shotNumber)
    {
        if (model == null)
        {
            Debug.LogWarning($"[Solo] Begin(scene {sceneNumber}, shot {shotNumber}) not written: not connected to the room yet (will write when it connects).");
            return;
        }

        model.RequestOwnership();
        model.mode = (int)Bootstrap.AppMode.Solo;
        model.sceneNumber = sceneNumber;
        model.shotNumber = shotNumber;
        Debug.Log($"[Solo] write mode=Solo, sceneNumber={sceneNumber}, shotNumber={shotNumber}");
    }

    // Actor, when leaving solo. Only clears what this client owns.
    public void End()
    {
        if (model == null || !model.isOwnedLocallySelf)
            return;

        model.mode = (int)Bootstrap.AppMode.None;
        model.sceneNumber = NoNumber;
        model.shotNumber = NoNumber;
        model.ClearOwnership();
        Debug.Log("[Solo] write mode=None, sceneNumber=-1, shotNumber=-1; ownership cleared");
    }

    public void SendCommand(SoloCommand command)
    {
        if (model == null)
        {
            Debug.LogWarning($"[Solo] command {command} not sent: not connected to the room.");
            return;
        }

        model.RequestOwnership();
        model.commandId = (int)command;
        int counter = model.commandCounter + 1;
        lastSentCounter = counter;
        commandBaseline = counter;
        model.commandCounter = counter;
        Debug.Log($"[Solo] write command {command} (commandId={(int)command}, commandCounter={counter})");
    }

    protected override void OnRealtimeModelReplaced(SoloSessionModel previousModel, SoloSessionModel currentModel)
    {
        if (previousModel != null)
            previousModel.commandCounterDidChange -= OnCommandCounterDidChange;

        if (currentModel == null)
            return;

        if (currentModel.isFreshModel)
        {
            currentModel.sceneNumber = NoNumber;
            currentModel.shotNumber = NoNumber;
        }

        commandBaseline = currentModel.commandCounter;
        Debug.Log($"[Solo] joined: mode={currentModel.mode}, sceneNumber={currentModel.sceneNumber}, shotNumber={currentModel.shotNumber}, commandCounter={currentModel.commandCounter} (baseline), owner={currentModel.ownerIDSelf}");

        currentModel.commandCounterDidChange += OnCommandCounterDidChange;
        ModelReady?.Invoke();
    }

    void OnCommandCounterDidChange(SoloSessionModel changedModel, int counter)
    {
        var command = (SoloCommand)changedModel.commandId;

        if (counter == lastSentCounter)
            return; // own command - already logged as a write

        if (counter <= commandBaseline)
        {
            Debug.Log($"[Solo] command {command} ignored: counter={counter} <= baseline={commandBaseline}");
            return;
        }

        int skipped = counter - commandBaseline - 1;
        Debug.Log($"[Solo] command {command} received: counter={counter}, baseline={commandBaseline}" + (skipped > 0 ? $", {skipped} earlier command(s) skipped" : ""));
        commandBaseline = counter;
        OnCommand?.Invoke(command);
    }
}
