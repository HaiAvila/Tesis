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
//   2. Arms swung down sideways to the body (palms to the thighs) + a slight squat with a
//      little forward lean: each bone turns about a known horizontal axis, B = N = D · T. The
//      pod sees Q2 · Q1⁻¹ = R · D · R⁻¹, a turn about R · axis(D), so R is the yaw that takes
//      axis(D) to the observed axis. Only the axis direction matters for R.
//   Then M = (R · T)⁻¹ · Q1. For the arms the second pose is exact too (upper arm against the
//   torso), so their M is the average of that and (R · N)⁻¹ · Q2 - an arm held a bit low or
//   forward in the T-pose no longer leaves the arms short of the body. The legs and chest
//   keep the T-pose's M, since nobody squats to an exact depth.
//   3. (Arms only, useThirdPoseForArms) Arms straight out in front, palms pressed together:
//      each arm points from its shoulder joint to where the wrists meet. Each arm pod's heading
//      and mounting are then fitted to all three poses at once (pattern search; the third pose
//      as a direction only, so the forearm's twist doesn't count, weighted thirdPoseWeight) -
//      a few degrees off per arm leaves the hands centimeters apart in a clap.
//   Every frame: B = R⁻¹ · Q · M⁻¹.
// The avatar faces the way the actor faced during the T-pose.
//
// Also:
// - Body measurements (height, upper arm, forearm, thigh, shin, optional joint spans) resize
//   the avatar at start: a uniform scale for the height, then each segment's joint moved
//   along the bone. Only rotations come from the pods, so lengths are what decide whether
//   two hands meet.
// - The shoulders (no pod) follow their upper arm by a share of its swing relative to the
//   chest, capped - real shoulders move forward and in when you clap or reach, and a fixed
//   shoulder left the avatar's hands centimeters apart.
// - The hips (no pod) take the chest pod's yaw. Other bones without a pod keep their rest
//   pose relative to their parent - except the feet (no pod either), which stay flat and
//   turn only with their shin's heading, like feet on the floor.
// - Grounding: the lowest sole stays on the floor, so a squat lowers the hips instead of
//   lifting the feet, and (plantFeet) the foot carrying the weight stays where it is, so the
//   hips go back in a squat instead of the feet sliding forward. In the app AvatarPoseSync
//   turns both off and places the avatar from the headset instead.
// - Pods arrive at ~19 Hz, so each bone eases toward its target instead of snapping.
public class PodAvatar : MonoBehaviour
{
    [Serializable]
    public class PodBone
    {
        [Tooltip("The pod's firmware POD_ID.")]
        public int podId;
        public HumanBodyBones bone;
    }

    // Joint-to-joint lengths in meters; 0 keeps the avatar's own.
    [Serializable]
    public class BodyMeasurements
    {
        [Tooltip("Standing height (m). Scales the whole avatar first.")]
        public float height;
        [Tooltip("Shoulder joint to elbow (m).")]
        public float upperArm;
        [Tooltip("Elbow to wrist (m).")]
        public float forearm;
        [Tooltip("Hip joint to knee (m).")]
        public float thigh;
        [Tooltip("Knee to ankle (m).")]
        public float shin;
        [Tooltip("Between the two shoulder JOINTS (m) - not the outer shoulder width. 0 = the avatar's, after the height scale.")]
        public float shoulderJointSpan;
        [Tooltip("Between the two hip JOINTS (m) - not the outer hip width. 0 = the avatar's, after the height scale.")]
        public float hipJointSpan;
    }

    public PodReceiver receiver;
    [Tooltip("The Humanoid avatar (Y Bot) - its Animator.")]
    public Animator avatar;

    [Tooltip("Which pod drives which bone. Defaults from Docs/pod-map.md; change it here, no recompiling.")]
    public List<PodBone> mapping = DefaultMapping();

    [Tooltip("The actor's body. Applied when Play starts.")]
    public BodyMeasurements body = new BodyMeasurements();

    [Header("Shoulders")]
    [Tooltip("Share of the upper arm's swing (relative to the chest) the shoulder takes.")]
    [Range(0f, 0.6f)] public float shoulderShare = 0.25f;
    [Tooltip("Most a shoulder turns away from its rest pose (degrees).")]
    public float maxShoulderAngle = 30f;

    [Header("Calibration")]
    [Tooltip("Average each arm pod's mounting over both poses (the arms-down pose is exact: arm against the torso).")]
    public bool useBothPosesForArms = true;
    [Tooltip("Third pose for the arms: arms straight out in front at shoulder height, palms pressed together. Each arm pod's heading and mounting are then fitted to all three poses, so the hands meet when the actor's do.")]
    public bool useThirdPoseForArms = true;
    [Tooltip("How much more the third pose counts than the other two in that fit.")]
    public float thirdPoseWeight = 2f;
    [Tooltip("Half the distance between the two wrist joints with the palms pressed together (m).")]
    public float palmsTogetherHalfGap = 0.025f;
    [Tooltip("How fast each bone follows its pod (higher = snappier, lower = smoother).")]
    public float smoothing = 18f;
    // The stillness filter (Nov 11: shins jiggling while standing, from balance corrections and
    // pod noise at ~11 Hz): a bone whose reading is within stillAngle of what's shown follows it
    // slowly (stillSmoothing); from moveAngle on, at full speed (smoothing) - so a real step or turn
    // has no lag. In between, blended. Applied where the pods are, so the sent, recorded and
    // replayed avatar are all filtered.
    [Tooltip("Below this difference (degrees) a bone counts as still and is smoothed heavily.")]
    public float stillAngle = 2.5f;
    [Tooltip("From this difference (degrees) a bone follows at full speed.")]
    public float moveAngle = 9f;
    [Tooltip("Smoothing rate while still (lower = steadier).")]
    public float stillSmoothing = 3f;
    [Tooltip("Seconds to get into each calibration pose.")]
    public float countdownSeconds = 4f;
    [Tooltip("Seconds each pose is held and averaged.")]
    public float captureSeconds = 1f;
    [Tooltip("Feel the calibration on the pods: CLICK at 3, 2, 1 s before each pose is captured, DOUBLE when it's captured, then DONE on each pod that calibrated and FAIL on each that didn't. Unity is the clock; the pods just play the cues.")]
    public bool calibrationCues = true;

    [Header("Body collision")]
    [Tooltip("Keep the limbs out of the body and out of each other (AvatarCollision): an overlap is pushed out until the two just touch, turning the arm at the shoulder or the leg at the hip.")]
    public bool preventInterpenetration = true;
    [Tooltip("Draw the collision capsules in the Scene view (orange = body, blue = limbs).")]
    public bool showCollisionGizmos;

    [Header("Feet and floor")]
    [Tooltip("Keep the feet flat (no pod on them): they turn only with their shin's heading.")]
    public bool flatFeet = true;
    [Tooltip("Keep the lowest sole on the floor (FloorHeight): a squat lowers the hips.")]
    public bool keepFeetOnFloor = true;
    [Tooltip("Keep the foot carrying the weight where it is, so the hips move over it (a squat sends them back). Off when something else places the avatar (the headset, in the app).")]
    public bool plantFeet = true;
    [Tooltip("How much lower a foot must be than the other to take over as the planted one (m).")]
    public float plantSwitchHeight = 0.02f;

    // World height of the floor the soles stand on: where they were at Start.
    public float FloorHeight { get; set; }

    // A usable second pose turns a bone at least this much, about a mostly horizontal axis.
    const float MinSecondPoseAngle = 10f;
    const float MaxVerticalAxis = 0.6f;
    // If the two arm mounting estimates disagree more than this, one pose went wrong: keep
    // the T-pose's.
    const float MaxArmMountDisagreement = 35f;

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

    static bool IsArm(HumanBodyBones b) =>
        b == HumanBodyBones.LeftUpperArm || b == HumanBodyBones.LeftLowerArm ||
        b == HumanBodyBones.RightUpperArm || b == HumanBodyBones.RightLowerArm;

    static bool IsLeft(HumanBodyBones b) =>
        b == HumanBodyBones.LeftUpperArm || b == HumanBodyBones.LeftLowerArm ||
        b == HumanBodyBones.LeftUpperLeg || b == HumanBodyBones.LeftLowerLeg;

    // How each bone turns from the T-pose into the second pose, in the avatar's frame (it
    // faces +Z, its left is -X), used for the heading: only the axis counts. Arms: out to the
    // side -> down. Thighs: knees forward. Shins: knees forward over the feet, so they lean
    // back. Chest: a slight lean forward. In Unity, Euler(0, 0, 90) * right = up and
    // Euler(90, 0, 0) * up = forward.
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

    // The exact arms-down rotation for the arm mounting: past straight down by armsInward, so
    // the upper arm rests against this avatar's torso.
    Quaternion ArmsDownDelta(HumanBodyBones bone) =>
        Quaternion.AngleAxis((IsLeft(bone) ? 1f : -1f) * (90f + armsInward), Vector3.forward);

    // --- per pod -------------------------------------------------------------------

    public class PodCalibration
    {
        public bool ok;
        public string problem;
        public Quaternion heading = Quaternion.identity;
        public Quaternion mount = Quaternion.identity;
        public float secondPoseAngle;
        public float armMountDisagreement = -1f;  // degrees, arms only
        public bool usedThirdPose;
        public float thirdPoseErrorBefore = -1f;  // degrees off the palms-together direction, before / after the fit
        public float thirdPoseErrorAfter = -1f;
    }

    class BoneState
    {
        public PodBone entry;
        public Transform transform;
        public Quaternion rest;      // T-pose world rotation, relative to the avatar
        public Quaternion smoothed;  // what's shown, relative to the avatar
        public bool hasSmoothed;
        public int depth;
        // Arms: the bone's long axis in its own frame, and where it points with the palms
        // pressed together in front (avatar frame).
        public Vector3 longAxisLocal;
        public Vector3 palmsTogetherDirection;
    }

    // A shoulder (clavicle) following its upper arm.
    class ShoulderState
    {
        public Transform transform;
        public Quaternion rest;          // relative to the avatar
        public BoneState upperArm;
        public Vector3 restArmDirection; // shoulder joint -> elbow in the T-pose, avatar frame
        public int depth;
    }

    readonly List<BoneState> bones = new List<BoneState>();
    readonly List<ShoulderState> shoulders = new List<ShoulderState>();
    readonly Dictionary<int, PodCalibration> calibrations = new Dictionary<int, PodCalibration>();
    Transform hips;
    Quaternion hipsRest;
    Transform leftFoot, rightFoot, leftShin, rightShin;
    Quaternion leftFootRest, rightFootRest, leftShinRest, rightShinRest; // relative to the avatar
    float soleBelowAnkle;   // in the avatar's local units (the root's scale applies)
    int plantedFoot = -1;   // 0 left, 1 right
    Vector3 plantedAt;
    BoneState chest;
    float armsInward;

    public IReadOnlyDictionary<int, PodCalibration> Calibrations => calibrations;
    public bool IsCalibrated => calibrations.Count > 0;
    // How far past straight down the arms go in the arms-down pose (degrees), from the mesh.
    public float ArmsInwardAngle => armsInward;
    // The body's collision capsules (null before Start).
    public AvatarCollision Collision => collision;
    AvatarCollision collision;

    // --- calibration state machine ----------------------------------------------------

    public enum Phase { Idle, CountdownTPose, CaptureTPose, CountdownSecond, CaptureSecond, CountdownThird, CaptureThird }
    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public float PhaseTimeLeft => Mathf.Max(0f, phaseEnd - Time.time);

    float phaseEnd;
    int nextTick;           // seconds before the capture of the next countdown CLICK; 0 = none left
    readonly Dictionary<int, QuaternionAverage> tPose = new Dictionary<int, QuaternionAverage>();
    readonly Dictionary<int, QuaternionAverage> secondPose = new Dictionary<int, QuaternionAverage>();
    readonly Dictionary<int, QuaternionAverage> thirdPose = new Dictionary<int, QuaternionAverage>();

    // Whether the calibration asks for the third pose (any arm mapped, and it's switched on).
    public bool UsesThirdPose => useThirdPoseForArms && bones.Exists(b => IsArm(b.entry.bone));

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

        ApplyBody();

        Quaternion root = Quaternion.Inverse(avatar.transform.rotation);
        hips = avatar.GetBoneTransform(HumanBodyBones.Hips);
        hipsRest = root * hips.rotation;

        leftFoot = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
        leftShin = avatar.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        rightShin = avatar.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        leftFootRest = root * leftFoot.rotation;
        rightFootRest = root * rightFoot.rotation;
        leftShinRest = root * leftShin.rotation;
        rightShinRest = root * rightShin.rotation;

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

        AddShoulder(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
        AddShoulder(HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm);

        armsInward = MeasureArmsInward();
        SetUpPalmsTogether();

        // Sized from the mesh in the rest pose, after the body measurements.
        collision = new AvatarCollision(avatar);
        Debug.Log($"[PodAvatar] body collision: {collision.Summary}");

        // Parents first, so a child's world rotation is set after its parent moved.
        bones.Sort((a, b) => a.depth.CompareTo(b.depth));
        Debug.Log($"[PodAvatar] ready: {bones.Count} bones mapped ({string.Join(", ", bones.ConvertAll(b => $"pod {b.entry.podId} -> {b.entry.bone}"))}), {shoulders.Count} shoulders following their arms, arms-down pose {90f + armsInward:F0}° from the T-pose. Press Calibrar.");
    }

    void AddShoulder(HumanBodyBones shoulder, HumanBodyBones upperArm, HumanBodyBones lowerArm)
    {
        Transform s = avatar.GetBoneTransform(shoulder);
        BoneState arm = bones.Find(b => b.entry.bone == upperArm);
        Transform elbow = avatar.GetBoneTransform(lowerArm);
        if (s == null || arm == null || elbow == null)
            return;

        Quaternion root = Quaternion.Inverse(avatar.transform.rotation);
        shoulders.Add(new ShoulderState
        {
            transform = s,
            rest = root * s.rotation,
            upperArm = arm,
            restArmDirection = (root * (elbow.position - arm.transform.position)).normalized,
            depth = Depth(s),
        });
    }

    // The third pose: arms straight out in front at shoulder height, palms pressed together.
    // Each arm (upper arm and forearm alike, the elbow is straight) points from its shoulder
    // joint to where the wrists meet: palmsTogetherHalfGap from the middle, an arm's length
    // away. Only that direction is used, not the forearm's twist, which varies from person to
    // person.
    void SetUpPalmsTogether()
    {
        Matrix4x4 toAvatar = avatar.transform.worldToLocalMatrix;
        Quaternion root = Quaternion.Inverse(avatar.transform.rotation);
        float scale = avatar.transform.lossyScale.y;

        foreach (BoneState b in bones)
        {
            if (!IsArm(b.entry.bone))
                continue;

            bool left = IsLeft(b.entry.bone);
            Transform shoulder = avatar.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            Transform elbow = avatar.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            Transform wrist = avatar.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (shoulder == null || elbow == null || wrist == null)
                continue;

            bool upper = b.entry.bone == HumanBodyBones.LeftUpperArm || b.entry.bone == HumanBodyBones.RightUpperArm;
            Vector3 axis = upper ? elbow.position - shoulder.position : wrist.position - elbow.position;
            b.longAxisLocal = Quaternion.Inverse(b.rest) * (root * axis).normalized;

            float armLength = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, wrist.position);
            float shoulderX = Mathf.Abs(toAvatar.MultiplyPoint3x4(shoulder.position).x * scale);
            float across = Mathf.Clamp((shoulderX - palmsTogetherHalfGap) / armLength, 0f, 0.9f);
            b.palmsTogetherDirection = new Vector3((left ? 1f : -1f) * across, 0f, Mathf.Sqrt(1f - across * across));
        }
    }

    static int Depth(Transform t)
    {
        int d = 0;
        for (Transform p = t.parent; p != null; p = p.parent) d++;
        return d;
    }

    // --- body measurements ------------------------------------------------------------------

    void ApplyBody()
    {
        Transform root = avatar.transform;
        Bounds before = MeshBounds();
        float avatarHeight = before.size.y;

        Transform lFoot = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rFoot = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
        soleBelowAnkle = Mathf.Min(lFoot.position.y, rFoot.position.y) - before.min.y;

        if (body.height > 0f && avatarHeight > 0.1f)
        {
            float k = body.height / avatarHeight;
            root.localScale *= k;
            soleBelowAnkle *= k;   // world, for the drop below
            Debug.Log($"[PodAvatar] height {avatarHeight:F2} m -> {body.height:F2} m (scale {k:F3})");
        }

        SetSpan(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, body.shoulderJointSpan, "shoulder joints");
        SetSpan(HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, body.hipJointSpan, "hip joints");

        SetLength(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, body.upperArm, "left upper arm");
        SetLength(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, body.upperArm, "right upper arm");
        SetLength(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, body.forearm, "left forearm");
        SetLength(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, body.forearm, "right forearm");
        SetLength(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, body.thigh, "left thigh");
        SetLength(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, body.thigh, "right thigh");
        SetLength(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, body.shin, "left shin");
        SetLength(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, body.shin, "right shin");

        // Back on the floor: the soles where they were.
        float drop = Mathf.Min(lFoot.position.y, rFoot.position.y) - soleBelowAnkle - before.min.y;
        root.position -= Vector3.up * drop;
        FloorHeight = before.min.y;
        soleBelowAnkle /= root.lossyScale.y;
    }

    // Moves the child joint along the bone so the joint-to-joint length is `length`.
    void SetLength(HumanBodyBones from, HumanBodyBones to, float length, string label)
    {
        if (length <= 0f)
            return;
        Transform a = avatar.GetBoneTransform(from), b = avatar.GetBoneTransform(to);
        if (a == null || b == null)
            return;

        Vector3 d = b.position - a.position;
        Debug.Log($"[PodAvatar] {label} {d.magnitude * 100f:F1} cm -> {length * 100f:F1} cm");
        b.position = a.position + d.normalized * length;
    }

    // Moves both joints sideways, symmetrically, so they're `span` apart.
    void SetSpan(HumanBodyBones left, HumanBodyBones right, float span, string label)
    {
        if (span <= 0f)
            return;
        Transform l = avatar.GetBoneTransform(left), r = avatar.GetBoneTransform(right);
        if (l == null || r == null)
            return;

        Vector3 across = r.position - l.position;
        Vector3 shift = across.normalized * ((span - across.magnitude) / 2f);
        Debug.Log($"[PodAvatar] {label} {across.magnitude * 100f:F1} cm apart -> {span * 100f:F1} cm");
        l.position -= shift;
        r.position += shift;
    }

    Bounds MeshBounds() => BakedBounds(avatar);

    // The avatar's mesh as posed right now, world space. Not Renderer.bounds: a skinned
    // mesh's bounds are a loose box (~6 cm too low under the feet on the Y Bot), which made the
    // height scale ~4% short and left the soles above the floor.
    public static Bounds BakedBounds(Animator avatar)
    {
        var all = new Bounds();
        bool any = false;
        var mesh = new Mesh();
        foreach (SkinnedMeshRenderer smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            smr.BakeMesh(mesh, true);
            Matrix4x4 m = smr.transform.localToWorldMatrix;
            foreach (Vector3 v in mesh.vertices)
            {
                Vector3 p = m.MultiplyPoint3x4(v);
                if (!any) { all = new Bounds(p, Vector3.zero); any = true; }
                else all.Encapsulate(p);
            }
        }
        Destroy(mesh);
        return all;
    }

    // How far past straight down the upper arm must go to rest against this avatar's torso:
    // the torso's half width at the height the elbow hangs, plus the upper arm's radius, from
    // the mesh in its T-pose (the arms are up at shoulder height then, so that height slice is
    // torso only). Avatar frame, so the scale and position don't matter.
    float MeasureArmsInward()
    {
        Transform lArm = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform lElbow = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        if (lArm == null || lElbow == null)
            return 0f;

        Matrix4x4 toAvatar = avatar.transform.worldToLocalMatrix;
        float avatarScale = avatar.transform.lossyScale.y;
        Vector3 shoulder = toAvatar.MultiplyPoint3x4(lArm.position) * avatarScale;
        float upperArm = Vector3.Distance(lArm.position, lElbow.position);
        float shoulderX = Mathf.Abs(shoulder.x);
        float elbowY = shoulder.y - upperArm;

        float torsoHalf = 0f, armTop = float.MinValue, armBottom = float.MaxValue;
        var mesh = new Mesh();
        foreach (SkinnedMeshRenderer smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            smr.BakeMesh(mesh, true);
            Matrix4x4 m = toAvatar * smr.transform.localToWorldMatrix;
            foreach (Vector3 v in mesh.vertices)
            {
                Vector3 p = m.MultiplyPoint3x4(v) * avatarScale;
                if (Mathf.Abs(p.y - elbowY) < 0.03f && Mathf.Abs(p.x) < shoulderX - 0.03f)
                    torsoHalf = Mathf.Max(torsoHalf, Mathf.Abs(p.x));
                // A slice through the upper arm, a third of the way to the elbow.
                float along = Mathf.Abs(p.x) - shoulderX;
                if (along > upperArm * 0.3f && along < upperArm * 0.4f && Mathf.Abs(p.z - shoulder.z) < 0.1f)
                {
                    armTop = Mathf.Max(armTop, p.y);
                    armBottom = Mathf.Min(armBottom, p.y);
                }
            }
        }
        Destroy(mesh);

        float armRadius = armTop > armBottom ? (armTop - armBottom) / 2f : 0.04f;
        float elbowX = torsoHalf + armRadius;
        float inward = Mathf.Asin(Mathf.Clamp((shoulderX - elbowX) / upperArm, -0.4f, 0.4f)) * Mathf.Rad2Deg;
        Debug.Log($"[PodAvatar] arms-down pose: shoulder joint {shoulderX * 100f:F1} cm from the middle, torso half width at the elbow {torsoHalf * 100f:F1} cm, arm radius {armRadius * 100f:F1} cm -> {inward:F1}° past straight down");
        return inward;
    }

    // --- calibration -------------------------------------------------------------------------

    public void StartCalibration()
    {
        if (!enabled || receiver == null)
            return;

        tPose.Clear();
        secondPose.Clear();
        thirdPose.Clear();
        SetPhase(Phase.CountdownTPose, countdownSeconds);
        Debug.Log("[PodAvatar] calibration started: T-pose (arms out, palms down, facing the screen).");
    }

    void SetPhase(Phase phase, float seconds)
    {
        CurrentPhase = phase;
        phaseEnd = Time.time + seconds;
        nextTick = phase == Phase.CountdownTPose || phase == Phase.CountdownSecond || phase == Phase.CountdownThird
            ? Mathf.Min(3, Mathf.FloorToInt(seconds)) : 0;
    }

    void SendCalibrationCue(PodReceiver.Cue cue)
    {
        if (calibrationCues && receiver != null)
            receiver.SendCue(0, cue);
    }

    // After the last pose: each pod says how it went, so the one that failed can be found on
    // the body.
    void SendCalibrationResult()
    {
        if (!calibrationCues || receiver == null)
            return;
        foreach (BoneState b in bones)
        {
            bool ok = calibrations.TryGetValue(b.entry.podId, out PodCalibration cal) && cal.ok;
            receiver.SendCue(b.entry.podId, ok ? PodReceiver.Cue.Done : PodReceiver.Cue.Fail);
        }
    }

    void Update()
    {
        if (nextTick > 0 && PhaseTimeLeft <= nextTick)
        {
            nextTick--;
            SendCalibrationCue(PodReceiver.Cue.Click);
        }

        switch (CurrentPhase)
        {
            case Phase.CountdownTPose:
                if (Time.time >= phaseEnd) SetPhase(Phase.CaptureTPose, captureSeconds);
                break;
            case Phase.CaptureTPose:
                Sample(tPose);
                if (Time.time >= phaseEnd)
                {
                    Debug.Log($"[PodAvatar] T-pose captured for pods {string.Join(", ", tPose.Keys)}. Now: arms down to the sides, slight squat.");
                    SendCalibrationCue(PodReceiver.Cue.Double);
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
                    if (UsesThirdPose)
                    {
                        Debug.Log($"[PodAvatar] second pose captured for pods {string.Join(", ", secondPose.Keys)}. Now: arms straight out in front, palms pressed together.");
                        SendCalibrationCue(PodReceiver.Cue.Double);
                        SetPhase(Phase.CountdownThird, countdownSeconds);
                    }
                    else
                    {
                        Solve();
                        SendCalibrationResult();
                        CurrentPhase = Phase.Idle;
                    }
                }
                break;
            case Phase.CountdownThird:
                if (Time.time >= phaseEnd) SetPhase(Phase.CaptureThird, captureSeconds);
                break;
            case Phase.CaptureThird:
                Sample(thirdPose);
                if (Time.time >= phaseEnd)
                {
                    Solve();
                    SendCalibrationResult();
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
        plantedFoot = -1;
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

            // Arms: the arms-down pose is exact (the arm against the torso), so average the
            // mounting over both poses - unless they disagree so much that one pose was off.
            if (useBothPosesForArms && IsArm(b.entry.bone))
            {
                Quaternion armsDown = ArmsDownDelta(b.entry.bone) * b.rest;
                Quaternion mount2 = Quaternion.Inverse(cal.heading * armsDown) * q2;
                cal.armMountDisagreement = Quaternion.Angle(cal.mount, mount2);
                if (cal.armMountDisagreement <= MaxArmMountDisagreement)
                {
                    var avg = new QuaternionAverage();
                    avg.Add(cal.mount);
                    avg.Add(mount2);
                    cal.mount = avg.Result;
                }
                else
                {
                    Debug.LogWarning($"[PodAvatar] pod {id} ({b.entry.bone}): the two poses disagree by {cal.armMountDisagreement:F0}° on how it's taped on - keeping the T-pose's. Were the arms twisted, or not against the body?");
                }
            }

            // Arms, third pose (palms pressed together in front): fit the heading and mounting to
            // all three poses at once, so the hands meet when the actor's do.
            if (useThirdPoseForArms && IsArm(b.entry.bone) && thirdPose.TryGetValue(id, out QuaternionAverage q3Avg))
            {
                Quaternion q3 = q3Avg.Result;
                Quaternion armsDown = ArmsDownDelta(b.entry.bone) * b.rest;
                cal.thirdPoseErrorBefore = ThirdPoseError(b, cal.heading, cal.mount, q3);
                FitArm(b, q1, q2, q3, armsDown, ref cal.heading, ref cal.mount);
                cal.thirdPoseErrorAfter = ThirdPoseError(b, cal.heading, cal.mount, q3);
                cal.usedThirdPose = true;
            }

            cal.ok = true;
            b.hasSmoothed = false;
            good++;
            Debug.Log($"[PodAvatar] pod {id} ({b.entry.bone}): calibrated. Heading {Signed(cal.heading.eulerAngles.y):F0}°, second-pose turn {angle:F0}° (expected ~{expectedAngle:F0}°)" +
                      (cal.armMountDisagreement >= 0f ? $", T-pose vs arms-down mounting {cal.armMountDisagreement:F0}° apart" : "") +
                      (cal.usedThirdPose ? $", palms-together direction off by {cal.thirdPoseErrorBefore:F1}° before the 3-pose fit, {cal.thirdPoseErrorAfter:F1}° after" : "") +
                      $", mount euler {cal.mount.eulerAngles.ToString("F0")}.");
        }

        Debug.Log($"[PodAvatar] calibration done: {good} of {bones.Count} pods.");
    }

    // How far the arm points from the palms-together direction in the third pose, with this
    // heading and mounting (degrees). Direction only: the forearm's twist isn't counted.
    static float ThirdPoseError(BoneState b, Quaternion heading, Quaternion mount, Quaternion q3)
    {
        Quaternion bone = Quaternion.Inverse(heading) * q3 * Quaternion.Inverse(mount);
        return Vector3.Angle(bone * b.longAxisLocal, b.palmsTogetherDirection);
    }

    // Fits an arm pod's heading (yaw) and mounting to all three poses: the T-pose and the
    // arms-down pose as whole rotations, the third as a direction only (counted
    // thirdPoseWeight times). A plain pattern search from the two-pose answer - four numbers
    // (yaw + a small turn of the mounting), steps from 8° down to 0.05°.
    void FitArm(BoneState b, Quaternion q1, Quaternion q2, Quaternion q3, Quaternion armsDown, ref Quaternion heading, ref Quaternion mount)
    {
        Quaternion mount0 = mount;
        float w2 = useBothPosesForArms ? 1f : 0f;

        float Cost(float[] v)
        {
            Quaternion r = Quaternion.AngleAxis(v[0], Vector3.up);
            Quaternion m = mount0 * Quaternion.Euler(v[1], v[2], v[3]);
            float e1 = Quaternion.Angle(r * b.rest * m, q1);
            float e2 = Quaternion.Angle(r * armsDown * m, q2);
            float e3 = ThirdPoseError(b, r, m, q3);
            return e1 * e1 + w2 * e2 * e2 + thirdPoseWeight * e3 * e3;
        }

        float[] p = { Signed(heading.eulerAngles.y), 0f, 0f, 0f };
        float best = Cost(p);
        float step = 8f;
        for (int iteration = 0; step > 0.05f && iteration < 3000; iteration++)
        {
            bool improved = false;
            for (int i = 0; i < p.Length; i++)
            {
                foreach (float s in new[] { step, -step })
                {
                    p[i] += s;
                    float c = Cost(p);
                    if (c < best) { best = c; improved = true; }
                    else p[i] -= s;
                }
            }
            if (!improved)
                step *= 0.5f;
        }

        heading = Quaternion.AngleAxis(p[0], Vector3.up);
        mount = mount0 * Quaternion.Euler(p[1], p[2], p[3]);
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

    readonly List<(int depth, Transform transform, Quaternion rotation)> writes = new List<(int, Transform, Quaternion)>();

    void LateUpdate()
    {
        if (!IsCalibrated)
            return;

        Quaternion root = avatar.transform.rotation;

        foreach (BoneState b in bones)
        {
            if (!TryGetBoneRotation(b.entry.podId, b.entry.bone, out Quaternion target))
                continue;

            if (b.hasSmoothed)
            {
                float difference = Quaternion.Angle(b.smoothed, target);
                float rate = Mathf.Lerp(stillSmoothing, smoothing, Mathf.InverseLerp(stillAngle, moveAngle, difference));
                b.smoothed = Quaternion.Slerp(b.smoothed, target, 1f - Mathf.Exp(-rate * Time.deltaTime));
            }
            else
            {
                b.smoothed = target;
            }
            b.hasSmoothed = true;
        }

        // The chest's turn since the T-pose (identity if pod 9 isn't calibrated).
        Quaternion chestDelta = chest != null && chest.hasSmoothed ? chest.smoothed * Quaternion.Inverse(chest.rest) : Quaternion.identity;

        // Hips: the chest's yaw, so turning the body turns the whole avatar.
        if (chest != null && chest.hasSmoothed)
        {
            Vector3 f = chestDelta * Vector3.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            hips.rotation = root * Quaternion.AngleAxis(yaw, Vector3.up) * hipsRest;
        }

        // Everything else in one list, parents first: chest, then shoulders, then the arms -
        // a world rotation set on a parent after its child would move the child again.
        writes.Clear();
        foreach (BoneState b in bones)
        {
            if (b.hasSmoothed)
                writes.Add((b.depth, b.transform, root * b.smoothed));
        }

        foreach (ShoulderState s in shoulders)
        {
            if (!s.upperArm.hasSmoothed)
                continue;

            // The arm's direction now, relative to the chest, against its T-pose direction:
            // the shoulder takes a share of that swing (no twist), capped.
            Vector3 armNow = s.upperArm.smoothed * Quaternion.Inverse(s.upperArm.rest) * s.restArmDirection;
            Vector3 relative = Quaternion.Inverse(chestDelta) * armNow;
            Quaternion swing = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(s.restArmDirection, relative), shoulderShare);
            swing.ToAngleAxis(out float a, out Vector3 ax);
            if (a > 180f) { a = 360f - a; ax = -ax; }
            if (a > maxShoulderAngle)
                swing = Quaternion.AngleAxis(maxShoulderAngle, ax);

            writes.Add((s.depth, s.transform, root * chestDelta * swing * s.rest));
        }

        writes.Sort((x, y) => x.depth.CompareTo(y.depth));
        foreach (var w in writes)
            w.transform.rotation = w.rotation;

        if (preventInterpenetration && collision != null)
            collision.Resolve();

        if (flatFeet)
        {
            SetFootFlat(leftFoot, leftShin, leftFootRest, leftShinRest);
            SetFootFlat(rightFoot, rightShin, rightFootRest, rightShinRest);
        }
        if (keepFeetOnFloor || plantFeet)
            Ground();
    }

    // A foot without a pod: flat, turned only by its shin's turn about the vertical (toes out,
    // the body turning), so a bent knee doesn't tip the toes into the floor.
    void SetFootFlat(Transform foot, Transform shin, Quaternion footRest, Quaternion shinRest)
    {
        if (foot == null || shin == null)
            return;
        Quaternion root = avatar.transform.rotation;
        Quaternion delta = Quaternion.Inverse(root) * shin.rotation * Quaternion.Inverse(shinRest);
        var twist = new Quaternion(0f, delta.y, 0f, delta.w);
        float m = Mathf.Sqrt(twist.y * twist.y + twist.w * twist.w);
        twist = m > 1e-4f ? new Quaternion(0f, twist.y / m, 0f, twist.w / m) : Quaternion.identity;
        foot.rotation = root * twist * footRest;
    }

    // Lowest sole on the floor; the planted foot kept where it was.
    void Ground()
    {
        Transform root = avatar.transform;
        float below = soleBelowAnkle * root.lossyScale.y;
        float leftSole = leftFoot.position.y - below, rightSole = rightFoot.position.y - below;

        if (keepFeetOnFloor)
        {
            root.position += Vector3.up * (FloorHeight - Mathf.Min(leftSole, rightSole));
        }

        if (!plantFeet)
        {
            plantedFoot = -1;
            return;
        }

        // The weight is on the lower foot; the other takes over only once clearly lower.
        int lower = leftSole <= rightSole ? 0 : 1;
        if (plantedFoot < 0 || (lower != plantedFoot && Mathf.Abs(leftSole - rightSole) > plantSwitchHeight))
        {
            plantedFoot = lower;
            plantedAt = (lower == 0 ? leftFoot : rightFoot).position;
            return;
        }

        Vector3 now = (plantedFoot == 0 ? leftFoot : rightFoot).position;
        root.position += new Vector3(plantedAt.x - now.x, 0f, plantedAt.z - now.z);
    }

    // Forget where the planted foot was (after the avatar is moved or recalibrated).
    public void ResetPlantedFoot() => plantedFoot = -1;

    void OnDrawGizmos()
    {
        if (showCollisionGizmos && collision != null)
            collision.DrawGizmos();
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
