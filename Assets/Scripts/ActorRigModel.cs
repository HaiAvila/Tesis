using Normal.Realtime;

// The actor's XR rig, written by the actor's build. See ActorRigSync.
//
// rigYaw: the rig's yaw in the set, in degrees. The rig turns on "Fijar inicio aquí" (X), when a
//   take loads and with the joystick, none of which the actor's body does: the pods can't tell.
// startSetCounter: how many times the actor pressed X, so the director can be told.
// calibrateCounter: how many times the actor pressed Y to calibrate, for the machine with the
//   pods. Not in the session model's command channel: that model's ownership is how the actor
//   detects that the director left, so only the director may own it.
[RealtimeModel(createMetaModel: true)]
public partial class ActorRigModel
{
    [RealtimeProperty(1, true, true)] private float _rigYaw;
    [RealtimeProperty(2, true, true)] private int _startSetCounter;
    [RealtimeProperty(3, true, true)] private int _calibrateCounter;
}
