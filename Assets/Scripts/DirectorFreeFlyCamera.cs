using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). A free-fly camera mode for the
// director while reviewing a recording's playback (RecordingPlaybackController turns it
// on/off around a playback session) - moves the XR Origin directly in whatever direction
// the head is actually looking, including up/down, via the left joystick. Head tracking
// keeps controlling where they look, completely normally.
//
// While active, the CharacterController and the "Move"/"Gravity" locomotion providers are
// disabled - no floor collision, no gravity, movement is a direct transform write. "Turn"
// is left alone so the director can still rotate in place if they want to. Never touches
// locomotion outside a playback session - every other screen keeps normal grounded
// movement untouched.
public class DirectorFreeFlyCamera : MonoBehaviour
{
    public static DirectorFreeFlyCamera Instance { get; private set; }

    const float FlySpeed = 2.5f;

    // Read by FallGuard: flying below the floor on purpose isn't a fall.
    public bool IsFlying => isActive;

    bool isActive;
    GameObject disabledMoveObject;
    GameObject disabledGravityObject;
    CharacterController disabledCharacterController;

#if DIRECTOR_BUILD
    InputAction flyMoveAction;
#endif

    void Awake()
    {
        Instance = this;

#if DIRECTOR_BUILD
        flyMoveAction = new InputAction("DirectorFreeFlyMove", InputActionType.Value, expectedControlType: "Vector2");
        flyMoveAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        flyMoveAction.AddBinding("<XRSimulatedController>{LeftHand}/primary2DAxis");
        flyMoveAction.Enable();
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        flyMoveAction.Disable();
        flyMoveAction.Dispose();
#endif
    }

#if DIRECTOR_BUILD
    public void EnableFly()
    {
        if (isActive)
            return;

        XROrigin xrOrigin = FindFirstObjectByType<XROrigin>();
        if (xrOrigin == null || xrOrigin.Camera == null)
            return;

        CharacterController controller = xrOrigin.GetComponent<CharacterController>();
        if (controller != null && controller.enabled)
        {
            controller.enabled = false;
            disabledCharacterController = controller;
        }

        GameObject move = GameObject.Find("Move");
        if (move != null && move.activeSelf)
        {
            move.SetActive(false);
            disabledMoveObject = move;
        }

        GameObject gravity = GameObject.Find("Gravity");
        if (gravity != null && gravity.activeSelf)
        {
            gravity.SetActive(false);
            disabledGravityObject = gravity;
        }

        isActive = true;
    }

    public void DisableFly()
    {
        if (!isActive)
            return;

        if (disabledCharacterController != null)
            disabledCharacterController.enabled = true;
        disabledCharacterController = null;

        if (disabledMoveObject != null)
            disabledMoveObject.SetActive(true);
        disabledMoveObject = null;

        if (disabledGravityObject != null)
            disabledGravityObject.SetActive(true);
        disabledGravityObject = null;

        isActive = false;
    }

    void Update()
    {
        if (!isActive || PanelFocusManager.IsModalOpen)
            return; // not flying while a menu is open

        XROrigin xrOrigin = FindFirstObjectByType<XROrigin>();
        if (xrOrigin == null || xrOrigin.Camera == null)
            return;

        Vector2 axis = flyMoveAction.ReadValue<Vector2>();
        if (axis.sqrMagnitude < 0.0001f)
            return;

        Transform head = xrOrigin.Camera.transform;
        Vector3 moveDirection = head.forward * axis.y + head.right * axis.x;

        xrOrigin.transform.position += moveDirection * FlySpeed * Time.deltaTime;
    }
#endif
}
