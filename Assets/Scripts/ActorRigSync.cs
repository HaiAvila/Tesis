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

#if DIRECTOR_BUILD
    protected override void OnRealtimeModelReplaced(ActorRigModel previousModel, ActorRigModel currentModel)
    {
        if (previousModel != null)
            previousModel.startSetCounterDidChange -= OnStartSetCounterDidChange;
        if (currentModel != null)
            currentModel.startSetCounterDidChange += OnStartSetCounterDidChange;
    }

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
#endif
}
