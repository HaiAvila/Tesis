using System.Collections.Generic;
using UnityEngine;

// What CueGenerator needs from a take's body (PoseTrack, from the pods), frame by frame:
//   - each hand relative to the chest, in the body's own frame (x right, y up, z forward) -
//     found by posing a hidden copy of the replay avatar (forward kinematics), since the take
//     only stores bone rotations;
//   - each knee's bend since Acción (the lower leg's local rotation against its first frame):
//     a step is a peak of one knee;
//   - the hips' turn since Acción, from their local rotation only - the root's yaw is left out,
//     because re-anchoring the avatar to the head (AvatarPoseSync.Reanchor) can jump it.
// Needs AvatarPoseSync (the avatar template); null without it or without body frames.
public class BodyTrack
{
    // AvatarPoseSync.SyncedBones order.
    const int HipsIndex = 0;
    const int LeftLowerLegIndex = 12;
    const int RightLowerLegIndex = 13;

    public float[] Time;
    public Vector3[] LeftHand, RightHand;
    public float[] LeftKnee, RightKnee;   // degrees
    public float[] HipsYaw;               // degrees, unwrapped, 0 at the first frame

    public int Count => Time.Length;

    public static BodyTrack FromTake(List<PoseSample> frames, byte[] layout)
    {
        if (frames == null || frames.Count < 10 || AvatarPoseSync.Instance == null)
            return null;

        Animator avatar = AvatarPoseSync.Instance.CreatePlaybackAvatar();
        foreach (Renderer r in avatar.GetComponentsInChildren<Renderer>())
            r.enabled = false;

        var track = new BodyTrack();
        try
        {
            var playback = new PlaybackAvatar(avatar, frames, layout);
            Transform hips = avatar.GetBoneTransform(HumanBodyBones.Hips);
            Transform chest = avatar.GetBoneTransform(HumanBodyBones.UpperChest) ?? avatar.GetBoneTransform(HumanBodyBones.Chest);
            Transform leftHand = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform rightHand = avatar.GetBoneTransform(HumanBodyBones.RightHand);
            Vector3 hipsForwardLocal = Quaternion.Inverse(hips.rotation) * avatar.transform.forward;

            int n = frames.Count;
            track.Time = new float[n];
            track.LeftHand = new Vector3[n];
            track.RightHand = new Vector3[n];
            track.LeftKnee = new float[n];
            track.RightKnee = new float[n];
            track.HipsYaw = new float[n];

            Quaternion leftKnee0 = AvatarPoseSync.ReadFrameBone(frames[0].Datos, LeftLowerLegIndex);
            Quaternion rightKnee0 = AvatarPoseSync.ReadFrameBone(frames[0].Datos, RightLowerLegIndex);
            Quaternion hips0Inverse = Quaternion.Inverse(AvatarPoseSync.ReadFrameBone(frames[0].Datos, HipsIndex));

            for (int i = 0; i < n; i++)
            {
                byte[] data = frames[i].Datos;
                track.Time[i] = frames[i].TiempoRelativo;

                track.LeftKnee[i] = Quaternion.Angle(leftKnee0, AvatarPoseSync.ReadFrameBone(data, LeftLowerLegIndex));
                track.RightKnee[i] = Quaternion.Angle(rightKnee0, AvatarPoseSync.ReadFrameBone(data, RightLowerLegIndex));

                Vector3 turned = AvatarPoseSync.ReadFrameBone(data, HipsIndex) * hips0Inverse * Vector3.forward;
                float yaw = Mathf.Atan2(turned.x, turned.z) * Mathf.Rad2Deg;
                track.HipsYaw[i] = i == 0 ? yaw : track.HipsYaw[i - 1] + Mathf.DeltaAngle(track.HipsYaw[i - 1], yaw);

                playback.ShowFrameAt(frames[i].TiempoRelativo);
                Vector3 bodyForward = hips.rotation * hipsForwardLocal;
                bodyForward.y = 0f;
                Quaternion toBody = Quaternion.Inverse(Quaternion.LookRotation(bodyForward.sqrMagnitude > 0.0001f ? bodyForward : Vector3.forward));
                track.LeftHand[i] = toBody * (leftHand.position - chest.position);
                track.RightHand[i] = toBody * (rightHand.position - chest.position);
            }
        }
        finally
        {
            Object.DestroyImmediate(avatar.gameObject);
        }
        return track;
    }
}
