using Normal.Realtime;
using UnityEngine;

// The actor's XR rig, written by the actor's build. See ActorRigSync.
//
// rigYaw: the rig's yaw in the set, in degrees. The rig turns on "Fijar inicio aquí" (X), when a
//   take loads and with the joystick, none of which the actor's body does: the pods can't tell.
// rigPosition: where the rig stands in the set. With rigYaw, it places the actor's real room there.
// limitPoints: the Quest's limit around the rig, in the rig's frame: x, z as floats per corner.
//   The director draws it in red, so they can see where the actor can really walk.
// startSetCounter: how many times the actor pressed X, so the director can be told.
// calibrateCounter: how many times the actor pressed Y to calibrate, for the machine with the
//   pods. Not in the session model's command channel: that model's ownership is how the actor
//   detects that the director left, so only the director may own it.
// takeCounter: how many times the actor pressed B to start or stop the take (an actor who is
//   their own director); the director's recorder acts on it.
[RealtimeModel(createMetaModel: true)]
public partial class ActorRigModel
{
    [RealtimeProperty(1, true, true)] private float _rigYaw;
    [RealtimeProperty(2, true, true)] private int _startSetCounter;
    [RealtimeProperty(3, true, true)] private int _calibrateCounter;
    [RealtimeProperty(4, true, true)] private Vector3 _rigPosition;
    [RealtimeProperty(5, true, true)] private byte[] _limitPoints;
    [RealtimeProperty(6, true, true)] private int _takeCounter;
}
