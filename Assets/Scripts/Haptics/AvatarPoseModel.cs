using Normal.Realtime;

// The actor's body pose from the pods, written by whichever machine has the pods (the server,
// or the Editor while testing) - never tied to a build role. Owned by that machine while it
// sends; Normcore clears the ownership when it leaves, which is how viewers know the pose
// stopped. See AvatarPoseSync.
//
// rotations: each synced bone's local rotation, AvatarPoseSync.SyncedBones order, 4 × int16
//   per bone (x, y, z, w × 32767). Unreliable: a lost packet is replaced by the next one.
// layout: the avatar's size - root scale (float), then the local positions of the bones the
//   body measurements move (AvatarPoseSync.LayoutBones order, 3 floats each). Reliable,
//   written once per calibration.
// rootYaw / hasRootYaw: which way the actor faced during the T-pose, from the headset and
//   relative to the rig (ActorRigSync), so it survives the rig turning: the avatar faces
//   rootYaw + the rig's yaw when the chest pod reads its T-pose heading. false = no headset
//   then; viewers fall back to the start mark's direction.
// calibrationPhase: PodAvatar.Phase as an int, so the actor's headset can show each pose.
// calibratedPods: how many pods the last calibration solved.
// podsConnected: how many pods the machine with the pods hears now, so the actor's headset knows
//   whether it can ask for a calibration. Owning the model from the start is fine: it's that
//   machine's anyway.
[RealtimeModel(createMetaModel: true)]
public partial class AvatarPoseModel
{
    [RealtimeProperty(1, false, true)] private byte[] _rotations;
    [RealtimeProperty(2, true, true)] private byte[] _layout;
    [RealtimeProperty(3, true, true)] private float _rootYaw;
    [RealtimeProperty(4, true, true)] private bool _hasRootYaw;
    [RealtimeProperty(5, true, true)] private int _calibrationPhase;
    [RealtimeProperty(6, true, true)] private int _calibratedPods;
    [RealtimeProperty(7, true, true)] private int _podsConnected;
}
