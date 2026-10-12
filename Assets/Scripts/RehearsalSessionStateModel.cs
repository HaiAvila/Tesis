using Normal.Realtime;

// Synced remote-rehearsal state, owned by the director. A plain int property (see
// RehearsalSessionState for the values) - a discrete synced value rather than the
// transform-position encoding RehearsalSessionSync uses for the shot number.
[RealtimeModel]
public partial class RehearsalSessionStateModel
{
    [RealtimeProperty(1, true, true)] private int _sessionState;
}
