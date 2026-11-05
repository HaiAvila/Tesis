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

    public float RigYaw => model != null ? model.rigYaw : 0f;

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
    void Update()
    {
        if (model == null)
            return;

        XROrigin rig = FindFirstObjectByType<XROrigin>();
        if (rig == null)
            return;

        if (!model.isOwnedLocallySelf)
            model.RequestOwnership();

        float yaw = rig.transform.eulerAngles.y;
        if (Mathf.Abs(Mathf.DeltaAngle(model.rigYaw, yaw)) > MinYawChange)
            model.rigYaw = yaw;
    }

    // StartMarkAligner, when the actor presses X.
    public void NotifyStartSet()
    {
        if (model == null)
            return;

        model.RequestOwnership();
        model.startSetCounter++;
    }

    // AvatarPoseSync, when the actor presses Y. Also raised here for this machine: it may be the
    // one with the pods, and starting a calibration that already started does nothing.
    public void RequestCalibration()
    {
        if (model == null)
            return;

        model.RequestOwnership();
        model.calibrateCounter++;
        CalibrationRequested?.Invoke();
    }
#endif
}
