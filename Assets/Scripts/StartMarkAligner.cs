using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;

// Actor only (Ensayo Remoto and Ensayar Solo): lines the shot's start mark up with where the
// actor is physically standing, so the whole real room is available for the walk.
//
// ShotStartPositioner puts the XR Origin - the center of the physical play area - on
// Shot{N}_Start, so the mark always landed at the middle of the real room, wherever the actor
// stood. A shot that walks 3 m forward from there hits the Quest boundary halfway in a 4 m
// room. This re-places the rig instead so that the actor's head is over the mark and their
// facing direction is the mark's: it rotates the rig about the head and shifts it
// horizontally. Only the rig moves - never the head/camera or its tracking.
//
// - When a take loads: automatically, right after ShotStartPositioner, so the tape T (solo)
//   and the virtual start are under the actor wherever they're standing.
// - "Fijar inicio aquí": the left controller's X (primary) re-does it at any time. The actor
//   walks to one end of the real room, faces the open space and presses X - the start moves
//   under their feet with the room ahead of them.
//
// The director is never moved by this (#if !DIRECTOR_BUILD): they move with the joystick
// and don't need their real walk to match the set.
public class StartMarkAligner : MonoBehaviour
{
    public static StartMarkAligner Instance { get; private set; }

    // Read by the tutorial's "Fijar inicio aquí" step.
    public int ManualAlignCount { get; private set; }

#if !DIRECTOR_BUILD
    InputAction setStartAction;
    Coroutine alignOnLoadCoroutine;

    void Awake()
    {
        Instance = this;

        // Left primary (X on a Quest) - free on the actor: A is the rig's stock Jump and B is
        // Iniciar/Parar in solo. Simulator: T + B.
        setStartAction = new InputAction("SetStartHere", InputActionType.Button);
        setStartAction.AddBinding("<XRController>{LeftHand}/{primaryButton}");
        setStartAction.AddBinding("<XRSimulatedController>{LeftHand}/primaryButton");
        setStartAction.performed += OnSetStartPressed;
        setStartAction.Enable();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        setStartAction.performed -= OnSetStartPressed;
        setStartAction.Disable();
        setStartAction.Dispose();

        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    static bool InActorTake =>
        (Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo)
        && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (alignOnLoadCoroutine != null)
            StopCoroutine(alignOnLoadCoroutine);
        alignOnLoadCoroutine = null;

        bool actorFlow = Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Solo;
        if (actorFlow && Bootstrap.IsContentScene(scene.name))
            alignOnLoadCoroutine = StartCoroutine(AlignAfterPositioner());
    }

    // sceneLoaded fires before the scene's Start() calls - wait one frame so
    // ShotStartPositioner has put the rig on the marker first.
    System.Collections.IEnumerator AlignAfterPositioner()
    {
        yield return null;
        alignOnLoadCoroutine = null;
        Align("take loaded", manual: false);
    }

    // FallGuard, after putting a fallen actor back on the mark.
    public void AlignToStart(string reason)
    {
        Align(reason, manual: false);
    }

    void OnSetStartPressed(InputAction.CallbackContext context)
    {
        if (!InActorTake || PanelFocusManager.IsModalOpen)
            return;

        Align("X pressed", manual: true);
    }

    void Align(string reason, bool manual)
    {
        GameObject marker = GameObject.Find($"Shot{Bootstrap.StartShotNumber}_Start");
        XROrigin origin = FindFirstObjectByType<XROrigin>();
        if (marker == null || origin == null || origin.Camera == null)
        {
            Debug.LogWarning($"[StartAlign] can't align ({reason}): marker={(marker != null)}, rig={(origin != null)}.");
            return;
        }

        Transform rig = origin.transform;
        Transform head = origin.Camera.transform;

        // Facing: turn the rig about the head so the head's flat forward is the mark's.
        float yawDelta = Mathf.DeltaAngle(FlatYaw(head.forward), FlatYaw(marker.transform.forward));
        rig.RotateAround(head.position, Vector3.up, yawDelta);

        // Position: shift the rig horizontally so the head is over the mark. Height is left
        // to the rig, which stands on (or drops onto) the floor.
        Vector3 offset = marker.transform.position - head.position;
        offset.y = 0f;
        rig.position += offset;

        Physics.SyncTransforms();
        GravityProvider gravity = rig.GetComponentInChildren<GravityProvider>();
        if (gravity != null)
            gravity.ResetFallForce();

        Debug.Log($"[StartAlign] start mark under the actor ({reason}): turned {yawDelta:F0}°, moved {offset.magnitude:F2} m, shot {Bootstrap.StartShotNumber}");

        if (!manual)
            return;

        ManualAlignCount++;
        FloatingMessage.Show("Inicio fijado aquí");
        SendHaptic();
    }

    static float FlatYaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

    static void SendHaptic()
    {
        GameObject leftController = GameObject.Find("Left Controller");
        HapticImpulsePlayer hapticPlayer = leftController != null ? leftController.GetComponent<HapticImpulsePlayer>() : null;
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
#endif
}
