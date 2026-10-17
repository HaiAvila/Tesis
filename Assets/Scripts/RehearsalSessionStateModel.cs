using Normal.Realtime;

// Synced remote-rehearsal state, owned by the director. A plain int property (see
// RehearsalSessionState for the values) - a discrete synced value rather than the
// transform-position encoding RehearsalSessionSync uses for the shot number.
//
// createMetaModel is required for ownership: without it RequestOwnership() only logs an
// error and ownerIDSelf stays unowned, so HasOwner (late join) and OwnerLeft ("Se perdió la
// conexión con el director") could never work.
[RealtimeModel(createMetaModel: true)]
public partial class RehearsalSessionStateModel
{
    [RealtimeProperty(1, true, true)] private int _sessionState;
}
