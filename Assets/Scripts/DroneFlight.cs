using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// Scene 3's levitating robot, shot 14: "cruza veloz frente a él; se detiene en seco". It hovers
// at its start (where it's placed in the scene) until the set's action is triggered (SetAction),
// waits a few seconds, dashes to the end point leaning into the move, brakes hard with a small
// overshoot, then turns to face the actor and keeps facing them. Its pose is a function of the
// seconds since the trigger, so the actor's and the director's drones match and a replay plays
// it back at the moment it happened.
//
// When it's triggered (the director, or this device outside Ensayo Remoto):
//   - a take of the action's shot starts recording, or a solo run of it starts
//   - an escena completa moves on to the action's shot (Siguiente toma)
//   - "Repetir dron" in the pause menu: back to the start, the same countdown again
// A new take loads it back at the start; a take of a later shot shows it already arrived.
//
// The hover on top: the model's own looping clip (its body bobbing, the ears spinning), plus a
// slow bob, sway and roll of the whole drone. Every number is a placeholder, to tune by eye.
[RequireComponent(typeof(Animator))]
public class DroneFlight : MonoBehaviour
{
    const string ActionName = "dron";

    // The shot, its delay and the dash's length are DronePlan's: the haptics of its pass follow
    // the same clock on the machine with the pods.
    const int actionShot = DronePlan.Shot;
    const float delay = DronePlan.Delay;
    const float dashSeconds = DronePlan.DashSeconds;

    public Vector3 endPosition = new Vector3(-4f, 1.268f, -0.699f);
    [Tooltip("Where it faces after arriving when there's no actor to face.")]
    public float fallbackEndYaw = -270f;

    [Header("Timing (s)")]
    public float turnDelay = 0.15f;
    public float turnSeconds = 0.8f;

    [Header("Flight")]
    [Tooltip("Lean into the dash, then back when braking (degrees).")]
    public float dashTilt = 18f;
    [Tooltip("How far past the end it slides before settling (m).")]
    public float overshoot = 0.12f;
    [Tooltip("How fast it keeps turning to follow the actor once facing them (deg/s).")]
    public float followTurnRate = 150f;

    [Header("Hover")]
    public float hoverAmplitude = 0.025f;
    public float hoverFrequency = 0.5f;
    public float rollAmplitude = 3f;
    public float swayAmplitude = 0.01f;
    public AnimationClip idleClip;

    Vector3 startPosition;
    float startYaw;
    float facingYaw;
    bool facingInitialized;
    PlayableGraph idleGraph;

    void Awake()
    {
        startPosition = transform.position;
        startYaw = transform.eulerAngles.y;
    }

    void Start()
    {
        SetAction.Register(ActionName, delay);
        SetAction.Clear("scene loaded");

        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted += OnRecordingStarted;
            RehearsalRecorder.Instance.ShotOpened += OnShotOpened;
        }
        SoloRehearsalController.RunStarted += OnSoloRunStarted;

        Animator animator = GetComponent<Animator>();
        animator.applyRootMotion = false;
        if (idleClip != null)
        {
            AnimationClipPlayable playable = AnimationPlayableUtilities.PlayClip(animator, idleClip, out idleGraph);
            playable.SetApplyFootIK(false);
        }
    }

    void OnDestroy()
    {
        SetAction.Unregister(ActionName);
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted -= OnRecordingStarted;
            RehearsalRecorder.Instance.ShotOpened -= OnShotOpened;
        }
        SoloRehearsalController.RunStarted -= OnSoloRunStarted;
        if (idleGraph.IsValid())
            idleGraph.Destroy();
    }

    void OnRecordingStarted()
    {
        if (Bootstrap.CurrentShotNumber == actionShot)
            SetAction.Trigger($"recording shot {actionShot}", actionShot);
        else
            SetAction.Clear("recording another shot");
    }

    void OnShotOpened(int shot)
    {
        if (shot == actionShot)
            SetAction.Trigger($"escena completa reached shot {actionShot}", actionShot);
    }

    void OnSoloRunStarted(int shot)
    {
        if (shot == actionShot)
            SetAction.Trigger($"solo run of shot {actionShot}", actionShot);
        else
            SetAction.Clear("solo run of another shot");
    }

    void LateUpdate()
    {
        float elapsed = SetAction.Elapsed;
        bool arrivedAlready = elapsed == SetAction.NotTriggered
                              && Bootstrap.CurrentShotNumber > actionShot && !Bootstrap.IsFullScene;

        Vector3 position = startPosition;
        float yaw = startYaw;
        float pitch = 0f;
        float t = Time.time;

        if (arrivedAlready)
        {
            position = endPosition;
            yaw = FacingYaw(position, 1f);
        }
        else if (elapsed != SetAction.NotTriggered && elapsed >= delay)
        {
            float u = elapsed - delay;
            Vector3 direction = (endPosition - startPosition).normalized;
            if (u < dashSeconds)
            {
                float x = u / dashSeconds;
                position = Vector3.LerpUnclamped(startPosition, endPosition, EaseInOutCubic(x));
                pitch = dashTilt * (1f - 2f * x); // nose down to go, nose up to brake
                facingInitialized = false;
            }
            else
            {
                // Stopped dead: a little slide past the end and a wobble that dies out.
                float a = u - dashSeconds;
                float decay = Mathf.Exp(-6f * a);
                position = endPosition + direction * (overshoot * decay * Mathf.Sin(14f * a));
                pitch = -dashTilt * 0.6f * Mathf.Exp(-5f * a) * Mathf.Cos(14f * a);

                float turn = a <= turnDelay ? 0f : Mathf.SmoothStep(0f, 1f, (a - turnDelay) / turnSeconds);
                yaw = Mathf.LerpAngle(startYaw, FacingYaw(position, turn), turn);
            }
        }
        else
        {
            facingInitialized = false;
        }

        // Hovering, always: a slow bob and sway, a gentle roll.
        position += Vector3.up * (hoverAmplitude * Mathf.Sin(2f * Mathf.PI * hoverFrequency * t)
                                  + hoverAmplitude * 0.3f * Mathf.Sin(2f * Mathf.PI * hoverFrequency * 2.6f * t));
        position += transform.right * (swayAmplitude * Mathf.Sin(2f * Mathf.PI * 0.23f * t));
        float roll = rollAmplitude * Mathf.Sin(2f * Mathf.PI * 0.35f * t);

        transform.SetPositionAndRotation(position, Quaternion.Euler(pitch, yaw, roll));
    }

    // Toward the actor's head (flat), followed smoothly once it's facing them.
    float FacingYaw(Vector3 from, float turnProgress)
    {
        float target = fallbackEndYaw;
        Vector3? actor = ActorHead();
        if (actor.HasValue)
        {
            Vector3 d = actor.Value - from;
            d.y = 0f;
            if (d.sqrMagnitude > 0.01f)
                target = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        if (!facingInitialized)
        {
            facingYaw = target;
            facingInitialized = true;
        }
        else
        {
            facingYaw = Mathf.MoveTowardsAngle(facingYaw, target, followTurnRate * Time.deltaTime);
        }
        return facingYaw;
    }

    static Vector3? ActorHead()
    {
        if (SetAction.ReplayActive)
            return SetAction.ReplayActorHead;
#if DIRECTOR_BUILD
        // The director's own head isn't the actor: in Ensayo Remoto, the synced one or none.
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote)
            return ActorPositionSync.Instance != null && ActorPositionSync.Instance.IsLive
                ? ActorPositionSync.Instance.transform.position
                : (Vector3?)null;
#endif
        Camera head = Camera.main;
        return head != null ? head.transform.position : (Vector3?)null;
    }

    static float EaseInOutCubic(float x) =>
        x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
}
