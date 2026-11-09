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
//
// The selected take is sceneNumber + shotNumber: the director writes the scene first, then
// the shot. shotNumber = RehearsalSessionState.FullSceneShot (0) means the whole scene.
// sceneNumber came later, so it has the next free id (existing ids are never renumbered) -
// and since it can arrive after shotNumber in the same update, receivers read the pair a
// frame later (RehearsalSessionSync), not inside either change callback.
[RealtimeModel(createMetaModel: true)]
public partial class RehearsalSessionStateModel
{
    [RealtimeProperty(1, true, true)] private int _sessionState;
    [RealtimeProperty(2, true, true)] private int _commandId;
    [RealtimeProperty(3, true, true)] private int _commandCounter;
    [RealtimeProperty(4, true, true)] private int _shotNumber;
    [RealtimeProperty(5, true, true)] private int _sceneNumber;
    // The director is recording: the actor can't re-set the start until it stops.
    [RealtimeProperty(6, true, true)] private bool _isRecording;
    // When the set's scripted action (scene 3's drone) was triggered, in Normcore room time
    // (realtime.room.time, the same clock on every client); 0 = not triggered. See SetAction.
    [RealtimeProperty(7, true, true)] private double _setActionTime;
}
