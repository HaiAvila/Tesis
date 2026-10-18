using Normal.Realtime;

// The actor's Ensayar Solo, synced so the PC server can follow it later (play cues for the
// shot, record it). Owned by the actor while in solo; separate from the director-owned
// RehearsalSessionStateModel, which solo never touches.
//
// mode is Bootstrap.AppMode as an int (Solo while active, 0 = none). sceneNumber/shotNumber
// are the take that's loaded (-1 = none). commandId + commandCounter are a command channel
// with the same contract as the director's: commandId first, then the counter + 1; receivers
// fire only above their own baseline (see SoloSession).
[RealtimeModel(createMetaModel: true)]
public partial class SoloSessionModel
{
    [RealtimeProperty(1, true, true)] private int _mode;
    [RealtimeProperty(2, true, true)] private int _sceneNumber;
    [RealtimeProperty(3, true, true)] private int _shotNumber;
    [RealtimeProperty(4, true, true)] private int _commandId;
    [RealtimeProperty(5, true, true)] private int _commandCounter;
}
