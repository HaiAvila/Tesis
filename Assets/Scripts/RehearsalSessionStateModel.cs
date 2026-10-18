using Normal.Realtime;

// Synced remote-rehearsal state, owned by the director: session state, the selected shot
// and the command channel. Plain int properties (see RehearsalSessionState for the values).
// The shot used to be encoded in a RealtimeTransform's position.x; it's a real property now.
//
// createMetaModel is required for ownership: without it RequestOwnership() only logs an
// error and ownerIDSelf stays unowned, so HasOwner (late join) and OwnerLeft ("Se perdió la
// conexión con el director") could never work.
//
// Command channel: commandId is the last SessionCommand sent, commandCounter how many have
// ever been sent in this room. The director sets commandId first and then increments
// commandCounter, so a receiver reacting to the counter change reads the matching id
// (both arrive in the same update; commandId has the lower property id). See
// RehearsalSessionState.SendCommand.
[RealtimeModel(createMetaModel: true)]
public partial class RehearsalSessionStateModel
{
    [RealtimeProperty(1, true, true)] private int _sessionState;
    [RealtimeProperty(2, true, true)] private int _commandId;
    [RealtimeProperty(3, true, true)] private int _commandCounter;
    [RealtimeProperty(4, true, true)] private int _shotNumber;
}
