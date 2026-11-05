using System.Collections.Generic;
using UnityEngine;

// The actor's avatar in a replay: its own copy of the Y Bot (AvatarPoseSync.CreatePlaybackAvatar),
// separate from the live one. It shows what the director saw during the take, frame by frame
// from PoseTrack (see AvatarPoseSync.TryCaptureFrame), at the actor's recorded size.
//
// A take with no body data (recorded before the pose track existed, or without pods) only has
// the head's path. The avatar then stands with its arms down under that path, feet on the floor.
public class PlaybackAvatar
{
    const float EyesForwardOfHeadBone = 0.08f;
    const float ArmsDownDegrees = 80f;
    const float FallbackEyeHeight = 1.7f;

    readonly GameObject gameObject;
    readonly Transform root;
    readonly Transform head;
    readonly Transform[] bones = new Transform[AvatarPoseSync.SyncedBones.Length];
    readonly List<PoseSample> frames;
    readonly Quaternion headRest;
    readonly float rootAboveSoles;
    float floorY;
    bool hasFloor;

    public bool HasBodyData => frames != null && frames.Count > 0;

    public PlaybackAvatar(Animator avatar, List<PoseSample> poseFrames, byte[] layout)
    {
        gameObject = avatar.gameObject;
        root = avatar.transform;
        head = avatar.GetBoneTransform(HumanBodyBones.Head);
        frames = poseFrames;

        for (int i = 0; i < bones.Length; i++)
            bones[i] = avatar.GetBoneTransform(AvatarPoseSync.SyncedBones[i]);

        AvatarPoseSync.ApplyLayout(layout, root, avatar);

        // Nothing else poses it. Without an update the Animator would only get in the way.
        root.rotation = Quaternion.identity;
        rootAboveSoles = root.position.y - PodAvatar.BakedBounds(avatar).min.y;
        avatar.enabled = false;

        if (!HasBodyData)
        {
            Transform left = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform right = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
            left.rotation = Quaternion.AngleAxis(ArmsDownDegrees, Vector3.forward) * left.rotation;
            right.rotation = Quaternion.AngleAxis(-ArmsDownDegrees, Vector3.forward) * right.rotation;
        }

        headRest = head.rotation;
    }

    public void Destroy()
    {
        if (gameObject != null)
            Object.Destroy(gameObject);
    }

    public void ShowFrameAt(float time)
    {
        int count = frames.Count;
        if (time <= frames[0].TiempoRelativo)
        {
            Apply(frames[0].Datos, frames[0].Datos, 0f);
            return;
        }

        if (time >= frames[count - 1].TiempoRelativo)
        {
            Apply(frames[count - 1].Datos, frames[count - 1].Datos, 0f);
            return;
        }

        // The last frame at or before this time.
        int low = 0, high = count - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (frames[middle].TiempoRelativo <= time)
                low = middle;
            else
                high = middle;
        }

        float span = frames[high].TiempoRelativo - frames[low].TiempoRelativo;
        Apply(frames[low].Datos, frames[high].Datos, span > 0f ? (time - frames[low].TiempoRelativo) / span : 0f);
    }

    void Apply(byte[] a, byte[] b, float t)
    {
        AvatarPoseSync.ReadFrameRoot(a, out Vector3 positionA, out float yawA, out Quaternion headA);
        AvatarPoseSync.ReadFrameRoot(b, out Vector3 positionB, out float yawB, out Quaternion headB);

        root.SetPositionAndRotation(Vector3.Lerp(positionA, positionB, t), Quaternion.Euler(0f, Mathf.LerpAngle(yawA, yawB, t), 0f));
        head.localRotation = Quaternion.Slerp(headA, headB, t);

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null)
                bones[i].localRotation = Quaternion.Slerp(AvatarPoseSync.ReadFrameBone(a, i), AvatarPoseSync.ReadFrameBone(b, i), t);
        }
    }

    // No body data: under the recorded head, turned its way, on the floor.
    public void ShowUnderHead(Vector3 headPosition, Quaternion headRotation)
    {
        Vector3 forward = headRotation * Vector3.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        root.rotation = Quaternion.LookRotation(forward, Vector3.up);
        head.rotation = headRotation * headRest;

        Vector3 target = headPosition - forward * EyesForwardOfHeadBone;
        if (SceneFloor.TryFindFloorBelow(target, out Vector3 floor))
        {
            floorY = floor.y;
            hasFloor = true;
        }

        Vector3 shift = target - head.position;
        Vector3 position = root.position + new Vector3(shift.x, 0f, shift.z);
        position.y = (hasFloor ? floorY : headPosition.y - FallbackEyeHeight) + rootAboveSoles;
        root.position = position;
    }
}
