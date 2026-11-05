using System;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// The actor's avatar over Normcore (AvatarPoseModel), on the persistent "ActorAvatar" object
// under Bootstrap, next to the PodReceiver, the PodAvatar and the Y Bot they drive.
//
// Whichever machine has the pods sends - the server, or the Editor while testing - not a
// build role: if PodReceiver hears pods here and PodAvatar is calibrated, this machine owns
// the model and writes the pose (~20 Hz). Every other machine copies the pose onto its own
// Y Bot. PodAvatar runs everywhere but only ever moves bones on the machine that calibrated.
//
// What's synced is the final pose: the local rotation of every bone PodAvatar moves (the
// pod bones, the shoulders, the hips, the flat feet), so viewers don't need the calibration, plus the
// avatar's size once (scale and the joints the body measurements moved).
//
// Placed the same way on every machine, after the pose:
// - facing: the way the actor's headset faced during the T-pose relative to the rig (rootYaw)
//   plus how the rig is turned now (ActorRigSync: X, a new take and the joystick turn the rig
//   but not the actor), else the start mark's direction;
// - the head bone turns with the headset;
// - the head bone sits under the headset (eyesForwardOfHeadBone behind the eyes), else over
//   the start mark;
// - the lowest foot stands on the set's floor - so a squat lowers the hips.
//
// Calibrar: the actor's Y in a take (ActorRigSync, through Normcore), SessionCommand.Calibrate
// from the director (another machine, through Normcore, or this one - the temporary C key), or
// the Inspector button. Ignored on a machine without pods. The phase is synced, so every headset
// shows which pose comes next: the actor's in TutorialController's panel, where a long
// instruction fits and stays up.
//
// Shown in the content scenes only, and not on the actor's own build (seeing your own body
// from a headset that isn't aligned with it is worse than seeing nothing) unless
// showOnActorBuild. Hidden while a replay is open: the replay's own avatar (PlaybackAvatar) is
// the one to look at, and two bodies in the same place only confuse.
//
// Recording: RehearsalRecorder takes whatever this machine is showing (TryCaptureFrame) - the
// final pose and placement, sent or received alike.
[DefaultExecutionOrder(100)] // after PodAvatar's LateUpdate: the pose is final when it's sent
public class AvatarPoseSync : RealtimeComponent<AvatarPoseModel>
{
    public static AvatarPoseSync Instance { get; private set; }

    // The bones PodAvatar can move, in the order AvatarPoseModel.rotations stores them.
    public static readonly HumanBodyBones[] SyncedBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
    };

    // The joints PodAvatar's body measurements move, in AvatarPoseModel.layout order.
    public static readonly HumanBodyBones[] LayoutBones =
    {
        HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
    };

    public PodReceiver receiver;
    public PodAvatar podAvatar;
    [Tooltip("The Y Bot both of them drive (PodAvatar's avatar).")]
    public Animator avatar;

    [Tooltip("Poses sent per second (the pods arrive at ~19 Hz).")]
    public float sendRate = 20f;
    [Tooltip("How fast a viewer's bones follow the received pose (higher = snappier).")]
    public float viewerSmoothing = 20f;
    [Tooltip("A viewer hides the avatar after this long without a pose (the pods machine left or lost its pods).")]
    public float staleSeconds = 1.5f;
    [Tooltip("How far the headset (the eyes) sits in front of the avatar's head bone (m).")]
    public float eyesForwardOfHeadBone = 0.08f;
    [Tooltip("Turn the avatar's head with the actor's headset.")]
    public bool driveHeadFromHeadset = true;
    [Tooltip("Also show the avatar on the actor's own build.")]
    public bool showOnActorBuild;
    [Tooltip("Only show the avatar in the content scenes (not over the menus).")]
    public bool onlyInContentScenes = true;

    Transform root, head, leftFoot, rightFoot;
    readonly Transform[] synced = new Transform[SyncedBones.Length];
    readonly Transform[] layoutJoints = new Transform[LayoutBones.Length];
    readonly Quaternion[] received = new Quaternion[SyncedBones.Length];
    Renderer[] renderers;
    Quaternion headRest;          // relative to the avatar
    Quaternion headRestLocal;
    float soleBelowAnkle;         // in the avatar's local units (the root's scale applies)

    // Viewer
    bool hasReceived;
    float lastReceivedTime = -100f;

    // Sender
    float nextSendTime;
    bool layoutSent;
    PodAvatar.Phase lastLocalPhase = PodAvatar.Phase.Idle;
    Vector3 tPoseForwardSum;
    float localRootYaw;
    bool localHasRootYaw;

    // Both
    bool shown;
    bool localPhaseMessage, remotePhaseMessage;
    float floorY;
    Transform startMark;
    float nextMarkSearch;
    RehearsalSessionState sessionState;
    bool hasPods;
    int lastPodsSent = -1;
    GameObject playbackTemplate;
#if !DIRECTOR_BUILD
    InputAction calibrateAction;
#endif

    public bool IsSending { get; private set; }
    public bool IsReceiving { get; private set; }
    public bool HasPodsHere => hasPods;

    // Whether any machine in the room hears pods. Normcore clears the owner when that machine leaves.
    public bool PodsAvailable => hasPods || (model != null && model.ownerIDSelf >= 0 && model.podsConnected > 0);

    // A calibration running on this machine or on the one with the pods. Idle otherwise.
    public PodAvatar.Phase RunningPhase
    {
        get
        {
            if (podAvatar != null && podAvatar.CurrentPhase != PodAvatar.Phase.Idle)
                return podAvatar.CurrentPhase;
            if (model != null && !model.isOwnedLocallySelf && model.ownerIDSelf >= 0)
                return (PodAvatar.Phase)model.calibrationPhase;
            return PodAvatar.Phase.Idle;
        }
    }

    void Awake()
    {
        Instance = this;
        if (receiver == null) receiver = GetComponentInChildren<PodReceiver>();
        if (podAvatar == null) podAvatar = GetComponentInChildren<PodAvatar>();
        if (avatar == null && podAvatar != null) avatar = podAvatar.avatar;
        if (avatar == null || !avatar.isHuman)
        {
            Debug.LogError("[AvatarSync] needs the Y Bot's Humanoid Animator.");
            enabled = false;
            return;
        }

        root = avatar.transform;
        // This places the avatar itself (from the headset): PodAvatar's own floor and planted
        // foot would only fight it. The flat feet stay, and are synced.
        if (podAvatar != null)
        {
            podAvatar.keepFeetOnFloor = false;
            podAvatar.plantFeet = false;
        }
        for (int i = 0; i < SyncedBones.Length; i++)
        {
            synced[i] = avatar.GetBoneTransform(SyncedBones[i]);
            received[i] = synced[i] != null ? synced[i].localRotation : Quaternion.identity;
        }
        for (int i = 0; i < LayoutBones.Length; i++)
            layoutJoints[i] = avatar.GetBoneTransform(LayoutBones[i]);

        head = avatar.GetBoneTransform(HumanBodyBones.Head);
        leftFoot = avatar.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightFoot = avatar.GetBoneTransform(HumanBodyBones.RightFoot);
        headRest = Quaternion.Inverse(root.rotation) * head.rotation;
        headRestLocal = head.localRotation;

        // Before anything is resized, posed or hidden: what a replay's avatar starts from.
        playbackTemplate = Instantiate(avatar.gameObject, transform);
        playbackTemplate.name = "PlaybackAvatarTemplate";
        playbackTemplate.SetActive(false);

        // Before PodAvatar resizes anything: how far the soles are below the ankles.
        renderers = avatar.GetComponentsInChildren<Renderer>();
        float meshBottom = PodAvatar.BakedBounds(avatar).min.y;
        soleBelowAnkle = (Mathf.Min(leftFoot.position.y, rightFoot.position.y) - meshBottom) / root.lossyScale.y;

        foreach (Renderer r in renderers)
        {
            r.enabled = false;
            // The pose moves the mesh far from its bind-pose bounds: don't let it get culled.
            if (r is SkinnedMeshRenderer smr)
                smr.updateWhenOffscreen = true;
        }

        RehearsalSessionState.CommandSentLocally += OnCommand;
        ActorRigSync.CalibrationRequested += OnCalibrationRequested;

#if !DIRECTOR_BUILD
        // Left secondary (Y on a Quest): free on the actor. Simulator: T + N.
        calibrateAction = new InputAction("Calibrate", InputActionType.Button);
        calibrateAction.AddBinding("<XRController>{LeftHand}/{secondaryButton}");
        calibrateAction.AddBinding("<XRSimulatedController>{LeftHand}/secondaryButton");
        calibrateAction.performed += OnCalibratePressed;
        calibrateAction.Enable();
#endif
    }

    void Start()
    {
        sessionState = FindFirstObjectByType<RehearsalSessionState>();
        if (sessionState != null)
            sessionState.OnCommand += OnCommand;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        RehearsalSessionState.CommandSentLocally -= OnCommand;
        ActorRigSync.CalibrationRequested -= OnCalibrationRequested;
        if (sessionState != null)
            sessionState.OnCommand -= OnCommand;

#if !DIRECTOR_BUILD
        if (calibrateAction != null)
        {
            calibrateAction.performed -= OnCalibratePressed;
            calibrateAction.Disable();
            calibrateAction.Dispose();
        }
#endif
    }

    // --- calibration ------------------------------------------------------------------

    void OnCommand(SessionCommand command)
    {
        if (command == SessionCommand.Calibrate)
            StartCalibration("command from the director");
    }

    void OnCalibrationRequested()
    {
        StartCalibration("the actor pressed Y");
    }

#if !DIRECTOR_BUILD
    void OnCalibratePressed(InputAction.CallbackContext context)
    {
        bool actorFlow = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
        if (!actorFlow || !Bootstrap.IsContentScene(SceneManager.GetActiveScene().name) || PanelFocusManager.IsModalOpen)
            return;

        if (RunningPhase != PodAvatar.Phase.Idle)
            return;

        if (StartMarkAligner.IsTakeRunning)
        {
            FloatingMessage.Show("No puedes calibrar durante la toma");
            return;
        }

        if (!PodsAvailable)
        {
            FloatingMessage.Show("No hay pods conectados");
            return;
        }

        if (ActorRigSync.Instance != null)
            ActorRigSync.Instance.RequestCalibration();
    }
#endif

    public void StartCalibration(string reason = "Inspector")
    {
        if (podAvatar == null || receiver == null)
            return;
        if (!hasPods)
        {
            Debug.Log($"[AvatarSync] Calibrar ({reason}) ignored: no pods on this machine.");
            return;
        }
        if (podAvatar.CurrentPhase != PodAvatar.Phase.Idle)
            return;

        Debug.Log($"[AvatarSync] Calibrar ({reason}): pods {string.Join(", ", receiver.ActivePods())}");
        podAvatar.StartCalibration();
    }

    // The local PodAvatar's phase: synced, the T-pose facing collected from the headset.
    void TrackLocalPhase()
    {
        PodAvatar.Phase phase = podAvatar.CurrentPhase;
        if (phase == PodAvatar.Phase.CaptureTPose && ActorPositionSync.Instance != null && ActorPositionSync.Instance.IsLive)
        {
            Vector3 f = ActorPositionSync.Instance.transform.forward;
            f.y = 0f;
            // Relative to the rig, so turning the rig later turns the avatar with it.
            tPoseForwardSum += Quaternion.Euler(0f, -CurrentRigYaw, 0f) * f.normalized;
        }

        if (phase == lastLocalPhase)
            return;

        if (phase == PodAvatar.Phase.CaptureTPose)
            tPoseForwardSum = Vector3.zero;
        if (lastLocalPhase == PodAvatar.Phase.CaptureTPose)
        {
            localHasRootYaw = tPoseForwardSum.sqrMagnitude > 0.01f;
            if (localHasRootYaw)
                localRootYaw = Mathf.Atan2(tPoseForwardSum.x, tPoseForwardSum.z) * Mathf.Rad2Deg;
            Debug.Log(localHasRootYaw
                ? $"[AvatarSync] T-pose facing from the headset: {localRootYaw:F0}° from the rig"
                : "[AvatarSync] no headset during the T-pose: the avatar faces the start mark's way");
        }

        bool finished = phase == PodAvatar.Phase.Idle;
        int solved = finished ? CountCalibrated() : 0;
        if (finished)
        {
            layoutSent = false; // a fresh calibration resends the size
            Debug.Log($"[AvatarSync] calibration done: {solved} pods calibrated");
        }

        lastLocalPhase = phase;
        localPhaseMessage = true;

        if (model != null)
        {
            model.RequestOwnership();
            if (finished)
            {
                model.calibratedPods = solved;
                model.rootYaw = localRootYaw;
                model.hasRootYaw = localHasRootYaw;
            }
            model.calibrationPhase = (int)phase;
        }
    }

    int CountCalibrated()
    {
        int n = 0;
        foreach (var kv in podAvatar.Calibrations)
            if (kv.Value.ok) n++;
        return n;
    }

    // --- Normcore -------------------------------------------------------------------------

    protected override void OnRealtimeModelReplaced(AvatarPoseModel previousModel, AvatarPoseModel currentModel)
    {
        if (previousModel != null)
        {
            previousModel.rotationsDidChange -= OnRotationsDidChange;
            previousModel.layoutDidChange -= OnLayoutDidChange;
            previousModel.calibrationPhaseDidChange -= OnCalibrationPhaseDidChange;
        }

        hasReceived = false;
        layoutSent = false;
        lastPodsSent = -1;
        if (currentModel == null)
            return;

        Debug.Log($"[AvatarSync] joined: owner={currentModel.ownerIDSelf}, phase={(PodAvatar.Phase)currentModel.calibrationPhase}, calibrated pods={currentModel.calibratedPods}");
        currentModel.rotationsDidChange += OnRotationsDidChange;
        currentModel.layoutDidChange += OnLayoutDidChange;
        currentModel.calibrationPhaseDidChange += OnCalibrationPhaseDidChange;
        if (currentModel.layout != null && currentModel.layout.Length > 0)
            ApplyLayout(currentModel.layout);
    }

    void OnRotationsDidChange(AvatarPoseModel m, byte[] data)
    {
        if (m.isOwnedLocallySelf || data == null || data.Length < SyncedBones.Length * 8)
            return;

        for (int i = 0; i < SyncedBones.Length; i++)
        {
            int o = i * 8;
            var q = new Quaternion(ReadShort(data, o), ReadShort(data, o + 2), ReadShort(data, o + 4), ReadShort(data, o + 6));
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag > 0.0001f)
                received[i] = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        if (!hasReceived)
            Debug.Log("[AvatarSync] receiving the actor's pose");
        hasReceived = true;
        lastReceivedTime = Time.time;
    }

    void OnLayoutDidChange(AvatarPoseModel m, byte[] data)
    {
        if (!m.isOwnedLocallySelf)
            ApplyLayout(data);
    }

    void OnCalibrationPhaseDidChange(AvatarPoseModel m, int phase)
    {
        if (!m.isOwnedLocallySelf)
            remotePhaseMessage = true; // read next frame, when calibratedPods is in too
    }

    byte[] EncodeRotations()
    {
        var data = new byte[SyncedBones.Length * 8];
        for (int i = 0; i < SyncedBones.Length; i++)
        {
            Quaternion q = synced[i] != null ? synced[i].localRotation : Quaternion.identity;
            int o = i * 8;
            WriteShort(data, o, q.x);
            WriteShort(data, o + 2, q.y);
            WriteShort(data, o + 4, q.z);
            WriteShort(data, o + 6, q.w);
        }
        return data;
    }

    byte[] EncodeLayout()
    {
        var data = new byte[4 + LayoutBones.Length * 12];
        WriteFloat(data, 0, root.localScale.x);
        for (int i = 0; i < LayoutBones.Length; i++)
        {
            Vector3 p = layoutJoints[i] != null ? layoutJoints[i].localPosition : Vector3.zero;
            WriteFloat(data, 4 + i * 12, p.x);
            WriteFloat(data, 8 + i * 12, p.y);
            WriteFloat(data, 12 + i * 12, p.z);
        }
        return data;
    }

    void ApplyLayout(byte[] data)
    {
        if (ApplyLayout(data, root, avatar))
            Debug.Log($"[AvatarSync] the actor's size received (scale {root.localScale.x:F3})");
    }

    // Also for a replay's avatar. False if there's no layout to apply.
    public static bool ApplyLayout(byte[] data, Transform avatarRoot, Animator animator)
    {
        if (data == null || data.Length < 4 + LayoutBones.Length * 12)
            return false;

        float scale = BitConverter.ToSingle(data, 0);
        if (scale > 0.01f)
            avatarRoot.localScale = Vector3.one * scale;
        for (int i = 0; i < LayoutBones.Length; i++)
        {
            Transform joint = animator.GetBoneTransform(LayoutBones[i]);
            if (joint != null)
                joint.localPosition = new Vector3(BitConverter.ToSingle(data, 4 + i * 12), BitConverter.ToSingle(data, 8 + i * 12), BitConverter.ToSingle(data, 12 + i * 12));
        }
        return true;
    }

    // --- recording and replaying ----------------------------------------------------------

    // One frame of PoseTrack: the root's position (3 floats) and yaw (float), the head's local
    // rotation (4 × int16), then each synced bone's local rotation, same format as the model's.
    const int FrameHeadOffset = 16;
    const int FrameBonesOffset = FrameHeadOffset + 8;

    // This machine's avatar as it stands now, if it has one: it's being sent or received.
    public bool TryCaptureFrame(out byte[] frame)
    {
        frame = null;
        if (!IsSending && !IsReceiving)
            return false;

        byte[] rotations = EncodeRotations();
        frame = new byte[FrameBonesOffset + rotations.Length];
        Vector3 p = root.position;
        WriteFloat(frame, 0, p.x);
        WriteFloat(frame, 4, p.y);
        WriteFloat(frame, 8, p.z);
        WriteFloat(frame, 12, root.eulerAngles.y);
        Quaternion q = head.localRotation;
        WriteShort(frame, FrameHeadOffset, q.x);
        WriteShort(frame, FrameHeadOffset + 2, q.y);
        WriteShort(frame, FrameHeadOffset + 4, q.z);
        WriteShort(frame, FrameHeadOffset + 6, q.w);
        Array.Copy(rotations, 0, frame, FrameBonesOffset, rotations.Length);
        return true;
    }

    public byte[] CaptureLayout() => EncodeLayout();

    public static void ReadFrameRoot(byte[] frame, out Vector3 position, out float yaw, out Quaternion headRotation)
    {
        position = new Vector3(BitConverter.ToSingle(frame, 0), BitConverter.ToSingle(frame, 4), BitConverter.ToSingle(frame, 8));
        yaw = BitConverter.ToSingle(frame, 12);
        headRotation = ReadQuaternion(frame, FrameHeadOffset);
    }

    public static Quaternion ReadFrameBone(byte[] frame, int bone) => ReadQuaternion(frame, FrameBonesOffset + bone * 8);

    static Quaternion ReadQuaternion(byte[] data, int offset)
    {
        var q = new Quaternion(ReadShort(data, offset), ReadShort(data, offset + 2), ReadShort(data, offset + 4), ReadShort(data, offset + 6));
        float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return magnitude > 0.0001f ? new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude) : Quaternion.identity;
    }

    // A fresh copy of the Y Bot for a replay; its owner destroys it.
    public Animator CreatePlaybackAvatar()
    {
        GameObject copy = Instantiate(playbackTemplate);
        copy.name = "PlaybackAvatar";
        copy.SetActive(true);
        foreach (Renderer r in copy.GetComponentsInChildren<Renderer>())
        {
            if (r is SkinnedMeshRenderer smr)
                smr.updateWhenOffscreen = true;
        }
        return copy.GetComponent<Animator>();
    }

    static void WriteShort(byte[] data, int offset, float value)
    {
        short s = (short)Mathf.Clamp(Mathf.RoundToInt(value * 32767f), -32767, 32767);
        data[offset] = (byte)s;
        data[offset + 1] = (byte)(s >> 8);
    }

    static float ReadShort(byte[] data, int offset) =>
        (short)(data[offset] | (data[offset + 1] << 8)) / 32767f;

    static void WriteFloat(byte[] data, int offset, float value) =>
        Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 4);

    // --- every frame --------------------------------------------------------------------

    void Update()
    {
        int podCount = receiver != null ? receiver.ActivePods().Length : 0;
        hasPods = podCount > 0;
        if (model != null && podCount != lastPodsSent && (hasPods || lastPodsSent > 0))
        {
            model.RequestOwnership();
            model.podsConnected = podCount;
            lastPodsSent = podCount;
        }

        if (podAvatar != null)
            TrackLocalPhase();

        if (localPhaseMessage)
            ShowPhaseMessage(podAvatar.CurrentPhase, CountCalibrated());
        else if (remotePhaseMessage && model != null)
            ShowPhaseMessage((PodAvatar.Phase)model.calibrationPhase, model.calibratedPods);
        localPhaseMessage = remotePhaseMessage = false;
    }

    void LateUpdate()
    {
        // PodAvatar.IsCalibrated is true after any calibration, even one that solved no pod.
        IsSending = hasPods && podAvatar != null && podAvatar.IsCalibrated && podAvatar.CurrentPhase == PodAvatar.Phase.Idle && CountCalibrated() > 0;
        IsReceiving = !IsSending && model != null && !model.isOwnedLocallySelf && model.ownerIDSelf >= 0
                      && hasReceived && Time.time - lastReceivedTime < staleSeconds;

        if (IsReceiving)
        {
            float blend = 1f - Mathf.Exp(-viewerSmoothing * Time.deltaTime);
            for (int i = 0; i < SyncedBones.Length; i++)
            {
                if (synced[i] != null)
                    synced[i].localRotation = Quaternion.Slerp(synced[i].localRotation, received[i], blend);
            }
        }

        if (IsSending || IsReceiving)
            Place();

        if (IsSending && model != null && Time.time >= nextSendTime)
        {
            nextSendTime = Time.time + 1f / Mathf.Max(1f, sendRate);
            if (!model.isOwnedLocallySelf)
            {
                model.RequestOwnership();
                layoutSent = false;
                Debug.Log("[AvatarSync] this machine has the pods: sending the actor's pose");
            }
            if (!layoutSent)
            {
                model.layout = EncodeLayout();
                model.rootYaw = localRootYaw;
                model.hasRootYaw = localHasRootYaw;
                layoutSent = true;
            }
            model.rotations = EncodeRotations();
        }

        bool replayOpen = RecordingPlaybackController.Instance != null && RecordingPlaybackController.Instance.IsPanelOpen;
        SetShown((IsSending || IsReceiving) && !replayOpen && RoleShowsAvatar() && (!onlyInContentScenes || InContentScene()));
    }

    void Place()
    {
        ActorPositionSync actorHead = ActorPositionSync.Instance;
        bool headLive = actorHead != null && actorHead.IsLive;
        Transform mark = FindStartMark();

        // Facing: the T-pose's, from the headset; else the start mark's.
        bool hasYaw = IsSending ? localHasRootYaw : model != null && model.hasRootYaw;
        float yaw = IsSending ? localRootYaw : model != null ? model.rootYaw : 0f;
        if (hasYaw)
            yaw += CurrentRigYaw;
        else
            yaw = mark != null ? mark.eulerAngles.y : root.eulerAngles.y;
        root.rotation = Quaternion.Euler(0f, yaw, 0f);

        if (headLive && driveHeadFromHeadset)
            head.rotation = actorHead.transform.rotation * headRest;
        else
            head.localRotation = headRestLocal;

        // Where the head bone goes, horizontally.
        Vector3 target;
        bool hasTarget = true;
        if (headLive)
        {
            Vector3 f = actorHead.transform.forward;
            f.y = 0f;
            target = actorHead.transform.position - (f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.zero) * eyesForwardOfHeadBone;
        }
        else if (mark != null)
            target = mark.position;
        else
        {
            target = head.position;
            hasTarget = false;
        }

        if (hasTarget && SceneFloor.TryFindFloorBelow(target, out Vector3 floor))
            floorY = floor.y;

        // The lowest sole on the floor, then the head over its target.
        float sole = Mathf.Min(leftFoot.position.y, rightFoot.position.y) - soleBelowAnkle * root.lossyScale.y;
        Vector3 shift = new Vector3(target.x - head.position.x, floorY - sole, target.z - head.position.z);
        root.position += shift;
    }

    static float CurrentRigYaw => ActorRigSync.Instance != null ? ActorRigSync.Instance.RigYaw : 0f;

    Transform FindStartMark()
    {
        if (startMark == null && Time.time >= nextMarkSearch)
        {
            nextMarkSearch = Time.time + 1f;
            GameObject marker = GameObject.Find($"Shot{Bootstrap.StartShotNumber}_Start");
            startMark = marker != null ? marker.transform : null;
        }
        return startMark;
    }

    bool RoleShowsAvatar()
    {
#if DIRECTOR_BUILD || SERVER_BUILD
        return true;
#else
        return showOnActorBuild;
#endif
    }

    static bool InContentScene()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (Bootstrap.IsContentScene(SceneManager.GetSceneAt(i).name))
                return true;
        }
        return false;
    }

    void SetShown(bool show)
    {
        if (show == shown)
            return;
        shown = show;
        foreach (Renderer r in renderers)
            r.enabled = show;
    }

    // --- what each headset shows during the calibration ------------------------------------

    // What to do in this phase; null when none is running.
    public static string PhaseInstruction(PodAvatar.Phase phase) => phase switch
    {
        PodAvatar.Phase.CountdownTPose => "Calibración: ponte en T, palmas abajo",
        PodAvatar.Phase.CaptureTPose => "Quieto en T…",
        PodAvatar.Phase.CountdownSecond => "Baja los brazos de lado, palmas a los muslos, y haz una sentadilla ligera",
        PodAvatar.Phase.CaptureSecond => "Quieto…",
        PodAvatar.Phase.CountdownThird => "Párate derecho, brazos al frente, junta las palmas",
        PodAvatar.Phase.CaptureThird => "Quieto con las palmas juntas…",
        _ => null,
    };

    static void ShowPhaseMessage(PodAvatar.Phase phase, int solved)
    {
        string text = PhaseInstruction(phase) ?? (solved > 0 ? $"Calibración lista: {solved} pods" : "La calibración no resolvió ningún pod");
        Debug.Log($"[AvatarSync] calibration phase {phase}: \"{text}\"");

        // The poses are in the panel when there's one here (the actor, in a take).
        bool inPanel = phase != PodAvatar.Phase.Idle && TutorialController.Instance != null && TutorialController.Instance.ShowsCalibrationHere;
        if (!inPanel)
            FloatingMessage.Show(text);
    }
}
