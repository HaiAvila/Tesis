using System;
using System.Collections.Generic;
using UnityEngine;

// Drives a Humanoid avatar (Y Bot) from the 9 pods. Used by the PodAvatarTest scene for now;
// written so the real app can reuse it (AvatarModel, P1-6).
//
// Each pod reading, in Unity's frame (PodReceiver, conversion 1), is
//     Q = R · B · M
//   B = the bone's world rotation (relative to the avatar),
//   R = the pod's heading: a yaw, because the BNO's game rotation vector has no compass,
//       so its "forward" is wherever that pod faced at power-up - different for every pod,
//   M = how the pod is taped on the limb (and how the chip sits in the pod), constant.
// One pose can't separate R from M, so the calibration takes two:
//   1. T-pose (arms out, palms down, facing the screen): B = T, the avatar's own rest pose.
//      Q1 = R · T · M.
//   2. Arms down at the sides + a slight squat (a bit of forward lean): each bone turns by a
//      known delta D about a horizontal axis, B = D · T. The pod sees Q2 · Q1⁻¹ = R · D · R⁻¹,
//      a turn about R · axis(D), so R is the yaw that takes axis(D) to the observed axis. Only
//      the axis direction matters, not how far the actor bends.
//   Then M = (R · T)⁻¹ · Q1, and every frame B = R⁻¹ · Q · M⁻¹.
// The avatar faces the way the actor faced during the T-pose.
//
// The hips have no pod: they take the yaw of the chest pod (pod 9), and stay in place (the
// headset will move them later). Bones without a pod (spine, shoulders, hands, feet, head)
// keep their rest pose relative to their parent. Pods arrive at ~19 Hz, so each bone eases
// toward its target instead of snapping.
public class PodAvatar : MonoBehaviour
{
    [Serializable]
    public class PodBone
    {
        [Tooltip("The pod's firmware POD_ID.")]
        public int podId;
        public HumanBodyBones bone;
    }

    public PodReceiver receiver;
    [Tooltip("The Humanoid avatar (Y Bot) - its Animator.")]
    public Animator avatar;

    [Tooltip("Which pod drives which bone. Defaults from Docs/pod-map.md; change it here, no recompiling.")]
    public List<PodBone> mapping = DefaultMapping();

    [Tooltip("How fast each bone follows its pod (higher = snappier, lower = smoother).")]
    public float smoothing = 18f;
    [Tooltip("Seconds to get into each calibration pose.")]
    public float countdownSeconds = 4f;
    [Tooltip("Seconds each pose is held and averaged.")]
    public float captureSeconds = 1f;

    // A usable second pose turns a bone at least this much, about a mostly horizontal axis.
    const float MinSecondPoseAngle = 10f;
    const float MaxVerticalAxis = 0.6f;

    public static List<PodBone> DefaultMapping() => new List<PodBone>
    {
        new PodBone { podId = 1, bone = HumanBodyBones.LeftLowerArm },
        new PodBone { podId = 2, bone = HumanBodyBones.RightLowerArm },
        new PodBone { podId = 3, bone = HumanBodyBones.LeftUpperArm },
        new PodBone { podId = 4, bone = HumanBodyBones.RightUpperArm },
        new PodBone { podId = 5, bone = HumanBodyBones.LeftUpperLeg },
        new PodBone { podId = 6, bone = HumanBodyBones.RightUpperLeg },
        new PodBone { podId = 7, bone = HumanBodyBones.LeftLowerLeg },
        new PodBone { podId = 8, bone = HumanBodyBones.RightLowerLeg },
        new PodBone { podId = 9, bone = HumanBodyBones.Chest },
    };

    // How each bone turns from the T-pose into the second pose, in the avatar's frame (it
    // faces +Z, its left is -X). Arms: from out to the side to straight down. Thighs: knees
    // forward. Shins: knees forward over the feet, so they lean back. Chest: a slight lean
    // forward. In Unity, Euler(0, 0, 90) * right = up and Euler(90, 0, 0) * up = forward.
    public static Quaternion SecondPoseDelta(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.LeftLowerArm:
                return Quaternion.AngleAxis(90f, Vector3.forward);   // -X -> -Y
            case HumanBodyBones.RightUpperArm:
            case HumanBodyBones.RightLowerArm:
                return Quaternion.AngleAxis(-90f, Vector3.forward);  // +X -> -Y
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.RightUpperLeg:
                return Quaternion.AngleAxis(-45f, Vector3.right);    // -Y toward +Z
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.RightLowerLeg:
                return Quaternion.AngleAxis(30f, Vector3.right);     // -Y toward -Z
            case HumanBodyBones.Spine:
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest:
                return Quaternion.AngleAxis(20f, Vector3.right);     // +Y toward +Z
            default:
                return Quaternion.identity;                          // can't be calibrated
        }
    }

    // --- per pod -------------------------------------------------------------------

    public class PodCalibration
    {
        public bool ok;
        public string problem;
        public Quaternion heading = Quaternion.identity;
        public Quaternion mount = Quaternion.identity;
        public float secondPoseAngle;
    }

    class BoneState
    {
        public PodBone entry;
        public Transform transform;
        public Quaternion rest;      // T-pose world rotation, relative to the avatar
        public Quaternion smoothed;  // what's shown, relative to the avatar
        public bool hasSmoothed;
        public int depth;
    }

    readonly List<BoneState> bones = new List<BoneState>();
    readonly Dictionary<int, PodCalibration> calibrations = new Dictionary<int, PodCalibration>();
    Transform hips;
    Quaternion hipsRest;
    BoneState chest;

    public IReadOnlyDictionary<int, PodCalibration> Calibrations => calibrations;
    public bool IsCalibrated => calibrations.Count > 0;

    // --- calibration state machine ----------------------------------------------------

    public enum Phase { Idle, CountdownTPose, CaptureTPose, CountdownSecond, CaptureSecond }
    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public float PhaseTimeLeft => Mathf.Max(0f, phaseEnd - Time.time);

    float phaseEnd;
    readonly Dictionary<int, QuaternionAverage> tPose = new Dictionary<int, QuaternionAverage>();
    readonly Dictionary<int, QuaternionAverage> secondPose = new Dictionary<int, QuaternionAverage>();

    void Start()
    {
        if (receiver == null)
            receiver = FindFirstObjectByType<PodReceiver>();
        if (avatar == null || !avatar.isHuman)
        {
            Debug.LogError("[PodAvatar] needs a Humanoid Animator (the Y Bot).");
            enabled = false;
            return;
        }

        // Nothing animates it but us.
        avatar.runtimeAnimatorController = null;

        Quaternion root = Quaternion.Inverse(avatar.transform.rotation);
        hips = avatar.GetBoneTransform(HumanBodyBones.Hips);
        hipsRest = root * hips.rotation;

        foreach (PodBone entry in mapping)
        {
            Transform t = avatar.GetBoneTransform(entry.bone);
            if (t == null)
            {
                Debug.LogWarning($"[PodAvatar] the avatar has no {entry.bone} bone - pod {entry.podId} ignored.");
                continue;
            }

            var state = new BoneState { entry = entry, transform = t, rest = root * t.rotation, depth = Depth(t) };
            bones.Add(state);
            if (entry.bone == HumanBodyBones.Chest || entry.bone == HumanBodyBones.UpperChest)
                chest = state;
        }

        // Parents first, so a child's world rotation is set after its parent moved.
        bones.Sort((a, b) => a.depth.CompareTo(b.depth));
        Debug.Log($"[PodAvatar] ready: {bones.Count} bones mapped ({string.Join(", ", bones.ConvertAll(b => $"pod {b.entry.podId} -> {b.entry.bone}"))}). Press Calibrar.");
    }

    static int Depth(Transform t)
    {
        int d = 0;
        for (Transform p = t.parent; p != null; p = p.parent) d++;
        return d;
    }

    public void StartCalibration()
    {
        if (!enabled || receiver == null)
            return;

        tPose.Clear();
        secondPose.Clear();
        SetPhase(Phase.CountdownTPose, countdownSeconds);
        Debug.Log("[PodAvatar] calibration started: T-pose (arms out, palms down, facing the screen).");
    }

    void SetPhase(Phase phase, float seconds)
    {
        CurrentPhase = phase;
        phaseEnd = Time.time + seconds;
    }

    void Update()
    {
        switch (CurrentPhase)
        {
            case Phase.CountdownTPose:
                if (Time.time >= phaseEnd) SetPhase(Phase.CaptureTPose, captureSeconds);
                break;
            case Phase.CaptureTPose:
                Sample(tPose);
                if (Time.time >= phaseEnd)
                {
                    Debug.Log($"[PodAvatar] T-pose captured for pods {string.Join(", ", tPose.Keys)}. Now: arms down, slight squat.");
                    SetPhase(Phase.CountdownSecond, countdownSeconds);
                }
                break;
            case Phase.CountdownSecond:
                if (Time.time >= phaseEnd) SetPhase(Phase.CaptureSecond, captureSeconds);
                break;
            case Phase.CaptureSecond:
                Sample(secondPose);
                if (Time.time >= phaseEnd)
                {
                    Solve();
                    CurrentPhase = Phase.Idle;
                }
                break;
        }
    }

    void Sample(Dictionary<int, QuaternionAverage> into)
    {
        foreach (BoneState b in bones)
        {
            int id = b.entry.podId;
            if (!receiver.HasOrientation(id))
                continue;
            if (!into.TryGetValue(id, out QuaternionAverage avg))
                into[id] = avg = new QuaternionAverage();
            avg.Add(receiver.GetRotation(id));
        }
    }

    void Solve()
    {
        calibrations.Clear();
        int good = 0;
        foreach (BoneState b in bones)
        {
            int id = b.entry.podId;
            var cal = new PodCalibration();
            calibrations[id] = cal;

            if (!tPose.TryGetValue(id, out QuaternionAverage q1Avg) || !secondPose.TryGetValue(id, out QuaternionAverage q2Avg))
            {
                cal.problem = "sin datos durante la calibración";
                Debug.LogWarning($"[PodAvatar] pod {id} ({b.entry.bone}): no data during {(tPose.ContainsKey(id) ? "the second pose" : "the T-pose")}.");
                continue;
            }

            Quaternion q1 = q1Avg.Result, q2 = q2Avg.Result;
            Quaternion delta = SecondPoseDelta(b.entry.bone);
            if (delta == Quaternion.identity)
            {
                cal.problem = "este hueso no tiene un movimiento conocido en la segunda pose";
                continue;
            }

            ToAxisAngle(delta, out float expectedAngle, out Vector3 expectedAxis);
            ToAxisAngle(q2 * Quaternion.Inverse(q1), out float angle, out Vector3 axis);
            cal.secondPoseAngle = angle;

            if (angle < MinSecondPoseAngle)
                cal.problem = $"casi no se movió en la segunda pose ({angle:F0}°)";
            else if (Mathf.Abs(axis.y) > MaxVerticalAxis)
                cal.problem = "en la segunda pose giró sobre la vertical, no se inclinó";
            if (cal.problem != null)
            {
                Debug.LogWarning($"[PodAvatar] pod {id} ({b.entry.bone}): {cal.problem} (turn of {angle:F0}° about {axis.ToString("F2")}, expected ~{expectedAngle:F0}° about {expectedAxis.ToString("F2")}).");
                continue;
            }

            Vector3 flatExpected = new Vector3(expectedAxis.x, 0f, expectedAxis.z);
            Vector3 flatObserved = new Vector3(axis.x, 0f, axis.z);
            cal.heading = Quaternion.AngleAxis(Vector3.SignedAngle(flatExpected, flatObserved, Vector3.up), Vector3.up);
            cal.mount = Quaternion.Inverse(cal.heading * b.rest) * q1;
            cal.ok = true;
            b.hasSmoothed = false;
            good++;
            Debug.Log($"[PodAvatar] pod {id} ({b.entry.bone}): calibrated. Heading {Signed(cal.heading.eulerAngles.y):F0}°, second-pose turn {angle:F0}° (expected ~{expectedAngle:F0}°), mount euler {cal.mount.eulerAngles.ToString("F0")}.");
        }

        Debug.Log($"[PodAvatar] calibration done: {good} of {bones.Count} pods.");
    }

    static void ToAxisAngle(Quaternion q, out float angle, out Vector3 axis)
    {
        q.ToAngleAxis(out angle, out axis);
        if (angle > 180f)
        {
            angle = 360f - angle;
            axis = -axis;
        }
    }

    static float Signed(float a) => a > 180f ? a - 360f : a;

    // --- pose ------------------------------------------------------------------------------

    // The bone's rotation from its pod right now (relative to the avatar), if calibrated.
    public bool TryGetBoneRotation(int podId, HumanBodyBones bone, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (!calibrations.TryGetValue(podId, out PodCalibration cal) || !cal.ok || !receiver.HasOrientation(podId))
            return false;

        rotation = Quaternion.Inverse(cal.heading) * receiver.GetRotation(podId) * Quaternion.Inverse(cal.mount);
        return true;
    }

    void LateUpdate()
    {
        if (!IsCalibrated)
            return;

        Quaternion root = avatar.transform.rotation;
        float blend = 1f - Mathf.Exp(-smoothing * Time.deltaTime);

        foreach (BoneState b in bones)
        {
            if (!TryGetBoneRotation(b.entry.podId, b.entry.bone, out Quaternion target))
                continue;

            b.smoothed = b.hasSmoothed ? Quaternion.Slerp(b.smoothed, target, blend) : target;
            b.hasSmoothed = true;
        }

        // Hips: the chest's yaw since the T-pose, so turning the body turns the whole avatar.
        if (chest != null && chest.hasSmoothed)
        {
            Vector3 f = chest.smoothed * Quaternion.Inverse(chest.rest) * Vector3.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            hips.rotation = root * Quaternion.AngleAxis(yaw, Vector3.up) * hipsRest;
        }

        foreach (BoneState b in bones)
        {
            if (b.hasSmoothed)
                b.transform.rotation = root * b.smoothed;
        }
    }

    // Average of a few rotations: align signs to the first (q and -q are the same rotation),
    // sum, normalize. Fine for samples close together, like a pose held still.
    class QuaternionAverage
    {
        Vector4 sum;
        Quaternion first;
        int count;

        public void Add(Quaternion q)
        {
            if (count == 0)
                first = q;
            Vector4 v = new Vector4(q.x, q.y, q.z, q.w);
            if (Quaternion.Dot(first, q) < 0f)
                v = -v;
            sum += v;
            count++;
        }

        public Quaternion Result
        {
            get
            {
                Vector4 n = sum.normalized;
                return new Quaternion(n.x, n.y, n.z, n.w);
            }
        }
    }
}
