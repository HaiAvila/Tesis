using UnityEngine;

// Scene 1's giant (the fungal golem), on the golem itself. What it does per shot and when comes
// from GolemPlan (shared with the haptics on the machine with the pods); this makes it seen:
//   shots 1-2  inside the cave (where it's placed in the scene); shot 2 rumbles from Acción
//   shot 3     walks out to shot3End        shot 4   walks on to shot4End, up to the protagonist
//   shot 5     turns to its right (to shot5EndZ) and steps to shot5End
// The ends are the user's (Nov 11), in the cave's local space like the golem's own Transform.
// Before Acción - or recording one shot on its own - it stands where the previous shot left it.
//
// The model has no skeleton (a Tripo mesh), so the walk is procedural: the body dips at each
// footfall (GolemPlan.StrideSeconds, the same moments the actor feels on the thighs), sways
// side to side step by step and leans into the walk; the feet always on the ground below it
// (the path drops almost a metre from the cave to the protagonist). Every number is a
// placeholder to tune by eye.
//
// Triggered through SetAction like scene 3's drone, so the director's, the actor's and the
// replay's golem move together: a take of shots 2-5 starts recording, an escena completa reaches
// one of them, a solo run of one starts, or the pause menu's Repetir.
public class GolemWalk : MonoBehaviour
{
    [Header("Where each walk ends (local to the cave, as in the golem's Transform)")]
    public Vector3 shot3End = new Vector3(7.42f, -0.925f, 3.55f);
    public Vector3 shot4End = new Vector3(15.01f, -0.925f, 3.55f);
    public Vector3 shot5End = new Vector3(17.76f, -0.925f, 3.55f);
    [Tooltip("Its local Z rotation at the end of shot 5 (it turns to its right).")]
    public float shot5EndZ = 180.305f;

    [Header("Walk")]
    public float bobHeight = 0.06f;
    public float swayDegrees = 2.5f;
    public float leanDegrees = 3f;
    [Tooltip("Lifted this much while leaning, so the front of the feet doesn't sink (m).")]
    public float leanLift = 0.05f;
    [Tooltip("Searched for the ground from this high above the golem (m): below the cave's ceiling.")]
    public float groundProbeHeight = 2.5f;

    [Header("Rumble (shot 2)")]
    public float rumbleShake = 0.025f;
    public float rumbleTilt = 1.5f;
    public float rumbleFrequency = 18f;

    Vector3 startLocal;
    float startZ;
    Collider[] ownColliders;
    int openedShot;
    string registeredName;

    void Awake()
    {
        startLocal = transform.localPosition;
        startZ = transform.localEulerAngles.z;
        ownColliders = GetComponentsInChildren<Collider>();
        Rigidbody body = GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = true;
            body.useGravity = false;
        }
    }

    void Start()
    {
        SetAction.Clear("scene loaded");
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted += OnRecordingStarted;
            RehearsalRecorder.Instance.ShotOpened += OnShotOpened;
        }
        SoloRehearsalController.RunStarted += OnSoloRunStarted;
    }

    void OnDestroy()
    {
        if (registeredName != null)
            SetAction.Unregister(registeredName);
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted -= OnRecordingStarted;
            RehearsalRecorder.Instance.ShotOpened -= OnShotOpened;
        }
        SoloRehearsalController.RunStarted -= OnSoloRunStarted;
    }

    // --- triggers ----------------------------------------------------------------------------

    void OnRecordingStarted()
    {
        int shot = Bootstrap.IsFullScene ? Bootstrap.StartShotNumber : Bootstrap.CurrentShotNumber;
        openedShot = shot;
        if (GolemPlan.HasAction(shot))
            SetAction.Trigger($"recording shot {shot}", shot);
        else
            SetAction.Clear($"recording shot {shot}");
    }

    void OnShotOpened(int shot)
    {
        openedShot = shot;
        if (GolemPlan.HasAction(shot))
            SetAction.Trigger($"escena completa reached shot {shot}", shot);
    }

    void OnSoloRunStarted(int shot)
    {
        if (GolemPlan.HasAction(shot))
            SetAction.Trigger($"solo run of shot {shot}", shot);
        else
            SetAction.Clear($"solo run of shot {shot}");
    }

    // The shot whose start it stands at, while nothing is moving it.
    int CurrentShot()
    {
        if (SetAction.ReplayActive)
            return SetAction.ReplayShot;
        if (Bootstrap.IsFullScene)
            return openedShot > 0 ? openedShot : Bootstrap.StartShotNumber;
        return Bootstrap.CurrentShotNumber;
    }

    // The pause menu's Repetir, for the shot at hand.
    void UpdateRegistration(int shot)
    {
        string name = shot == 2 ? "temblor" : GolemPlan.HasAction(shot) ? "gigante" : null;
        if (name == registeredName)
            return;
        if (registeredName != null)
            SetAction.Unregister(registeredName);
        registeredName = name;
        if (name == null)
            return;

        GolemPlan.Action action = GolemPlan.For(shot);
        if (shot == 2)
            SetAction.Register(name, action.Delay, "El gigante vuelve a sacudir la cueva.", "Temblor otra vez");
        else
            SetAction.Register(name, action.Delay,
                action.Delay > 0f ? $"Vuelve a su lugar y camina otra vez a los {action.Delay:0.#} segundos." : "Vuelve a su lugar y camina otra vez.",
                action.Delay > 0f ? $"El gigante camina en {action.Delay:0.#} s" : "El gigante camina otra vez");
    }

    // --- the pose -----------------------------------------------------------------------------

    void LateUpdate()
    {
        int shot = CurrentShot();
        UpdateRegistration(SetAction.Shot > 0 && !SetAction.ReplayActive ? SetAction.Shot : shot);

        float elapsed = SetAction.Elapsed;
        int actionShot = SetAction.Shot;
        bool acting = elapsed != SetAction.NotTriggered && GolemPlan.HasAction(actionShot);
        if (!acting)
        {
            StartPose(shot, out Vector3 local, out float z);
            Place(local, z, Vector3.zero, Quaternion.identity);
            return;
        }

        GolemPlan.Action action = GolemPlan.For(actionShot);
        StartPose(actionShot, out Vector3 from, out float fromZ);
        float e = elapsed - action.Delay;

        if (action.Kind == GolemPlan.Kind.Rumble)
        {
            Vector3 shake = Vector3.zero;
            Quaternion tilt = Quaternion.identity;
            if (e >= 0f && e < action.Duration)
            {
                float fade = 1f - e / action.Duration;
                float t = Time.time * rumbleFrequency;
                shake = new Vector3(Mathf.PerlinNoise(t, 0.3f) - 0.5f, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 0.4f, Mathf.PerlinNoise(t, 5.1f) - 0.5f) * (2f * rumbleShake * fade);
                tilt = Quaternion.Euler((Mathf.PerlinNoise(t, 9.2f) - 0.5f) * 2f * rumbleTilt * fade, 0f, (Mathf.PerlinNoise(3.3f, t) - 0.5f) * 2f * rumbleTilt * fade);
            }
            Place(from, fromZ, shake, tilt);
            return;
        }

        EndPose(actionShot, out Vector3 to, out float toZ);
        float u = Mathf.Clamp01(e / action.Duration);
        float eased = u * u * (3f - 2f * u);
        Vector3 localPosition = Vector3.Lerp(from, to, eased);
        float localZ = Mathf.LerpAngle(fromZ, toZ, eased);

        // The gait, only while walking: lowest at each footfall, swaying a side per step.
        Vector3 bob = Vector3.zero;
        Quaternion gait = Quaternion.identity;
        if (e > 0f && e < action.Duration)
        {
            float weight = Mathf.Clamp01(Mathf.Min(e, action.Duration - e) / 0.4f);
            float phase = e / GolemPlan.StrideSeconds + 0.5f;
            // Plus what the lean dips the front of its feet into the ground.
            bob = Vector3.up * ((bobHeight * (0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * phase)) + leanLift) * weight);
            Vector3 forward = transform.parent != null ? transform.parent.TransformDirection(to - from) : to - from;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            gait = Quaternion.AngleAxis(swayDegrees * Mathf.Sin(Mathf.PI * phase) * weight, forward)
                   * Quaternion.AngleAxis(leanDegrees * weight, right);
        }
        Place(localPosition, localZ, bob, gait);
    }

    void StartPose(int shot, out Vector3 local, out float z)
    {
        z = startZ;
        local = shot == 4 ? shot3End : shot == 5 ? shot4End : shot > 5 ? shot5End : startLocal;
        if (shot > 5)
            z = shot5EndZ;
    }

    void EndPose(int shot, out Vector3 local, out float z)
    {
        z = shot == 5 ? shot5EndZ : startZ;
        local = shot == 3 ? shot3End : shot == 4 ? shot4End : shot == 5 ? shot5End : startLocal;
    }

    // local: in the cave's space; offset (world) and extra (a world-space turn) on top. Its feet on
    // the ground below.
    void Place(Vector3 local, float localZ, Vector3 offset, Quaternion extra)
    {
        Transform parent = transform.parent;
        Vector3 world = parent != null ? parent.TransformPoint(local) : local;
        if (TryGround(world, out float groundY))
            world.y = groundY;
        Quaternion baseRotation = (parent != null ? parent.rotation : Quaternion.identity) * Quaternion.Euler(0f, 0f, localZ);
        transform.SetPositionAndRotation(world + offset, extra * baseRotation);
    }

    bool TryGround(Vector3 at, out float y)
    {
        y = at.y;
        RaycastHit[] hits = Physics.RaycastAll(at + Vector3.up * groundProbeHeight, Vector3.down, groundProbeHeight + 4f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            if (System.Array.IndexOf(ownColliders, hit.collider) >= 0)
                continue;
            if (hit.distance < best)
            {
                best = hit.distance;
                y = hit.point.y;
            }
        }
        return best < float.MaxValue;
    }
}
