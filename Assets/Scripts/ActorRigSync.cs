using System;
using Normal.Realtime;
using Unity.XR.CoreUtils;
using UnityEngine;

// Next to ActorPositionSync: that one syncs the actor's head in the set, this one how the rig
// under it is turned. AvatarPoseSync needs both to keep the body facing the same way as the
// head: the T-pose's facing is stored relative to the rig, so whatever turns the rig afterwards
// ("Fijar inicio aquí", a new take, the joystick) turns the avatar with it.
//
// Same pattern as ActorPositionSync: the actor's build writes, asking for ownership every frame
// it doesn't hold it; the director's build only reads.
public class ActorRigSync : RealtimeComponent<ActorRigModel>
{
    public static ActorRigSync Instance { get; private set; }

    // The actor pressed Y. Raised on every machine; only the one with the pods acts on it.
    public static event Action CalibrationRequested;

    const float MinYawChange = 0.05f;
    const float MinPositionChange = 0.01f;

    // After a disconnect the model is a fresh one, not in any datastore: writing to it throws.
    bool Online => model != null && realtime != null && realtime.connected;

    public float RigYaw => model != null ? model.rigYaw : 0f;
    public Vector3 RigPosition => model != null ? model.rigPosition : Vector3.zero;
    public byte[] LimitPoints => model != null ? model.limitPoints : null;

    // Normcore clears the owner when the actor leaves.
    public bool HasActor => model != null && model.ownerIDSelf >= 0;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    protected override void OnRealtimeModelReplaced(ActorRigModel previousModel, ActorRigModel currentModel)
    {
        if (previousModel != null)
        {
            previousModel.calibrateCounterDidChange -= OnCalibrateCounterDidChange;
#if DIRECTOR_BUILD
            previousModel.startSetCounterDidChange -= OnStartSetCounterDidChange;
#endif
        }

        if (currentModel != null)
        {
            currentModel.calibrateCounterDidChange += OnCalibrateCounterDidChange;
#if DIRECTOR_BUILD
            currentModel.startSetCounterDidChange += OnStartSetCounterDidChange;
#endif
        }
    }

    void OnCalibrateCounterDidChange(ActorRigModel changedModel, int counter)
    {
        CalibrationRequested?.Invoke();
    }

#if DIRECTOR_BUILD
    void OnStartSetCounterDidChange(ActorRigModel changedModel, int counter)
    {
        FloatingMessage.Show("El actor fijó su inicio");
    }
#else
    int sentLimitVersion = -1;

    void Update()
    {
        if (!Online)
            return;

        XROrigin rig = FindFirstObjectByType<XROrigin>();
        if (rig == null)
            return;

        if (!model.isOwnedLocallySelf)
            model.RequestOwnership();

        float yaw = rig.transform.eulerAngles.y;
        if (Mathf.Abs(Mathf.DeltaAngle(model.rigYaw, yaw)) > MinYawChange)
            model.rigYaw = yaw;

        Vector3 position = rig.transform.position;
        if (Vector3.Distance(model.rigPosition, position) > MinPositionChange)
            model.rigPosition = position;

        PlayAreaOutline playArea = PlayAreaOutline.Instance;
        if (playArea != null && playArea.LimitVersion != sentLimitVersion)
        {
            sentLimitVersion = playArea.LimitVersion;
            model.limitPoints = PlayAreaOutline.EncodePoints(playArea.RigLocalPoints);
        }
    }

    // StartMarkAligner, when the actor presses X.
    public void NotifyStartSet()
    {
        if (!Online)
            return;

        model.RequestOwnership();
        model.startSetCounter++;
    }

    // AvatarPoseSync, when the actor presses Y. Also raised here for this machine: it may be the
    // one with the pods, and starting a calibration that already started does nothing.
    public void RequestCalibration()
    {
        if (!Online)
        {
            CalibrationRequested?.Invoke();
            return;
        }

        model.RequestOwnership();
        model.calibrateCounter++;
        CalibrationRequested?.Invoke();
    }
#endif
}
