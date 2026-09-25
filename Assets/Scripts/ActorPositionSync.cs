using Normal.Realtime;
using Unity.XR.CoreUtils;
using UnityEngine;

// Persistent (lives under Bootstrap, DontDestroyOnLoad), networked representation of the
// actor's head position/rotation - the "director has a synced representation of where the
// actor is" prerequisite, and what RehearsalRecorder samples while recording.
//
// Tracks the XR Origin's CAMERA, not the XR Origin root. The root transform only moves via
// discrete locomotion (joystick Move, teleport) - physical head movement (walking around a
// room-scale play space, or the XR Device Simulator's default WASD-moves-the-HMD behavior)
// only changes the camera's position relative to the origin, never the root itself.
// Confirmed via a real test where the actor visibly walked around but the root's recorded
// position stayed dead flat the whole time.
//
// This deliberately does NOT sync the XR Origin itself: both the actor and director build
// instantiate the exact same scene-placed XR Origin as their OWN local rig, so networking
// it directly would make the director's own headset movement get overwritten by whichever
// client owns it. Instead this is a separate object that only the actor build writes into;
// the director build only ever reads it, already synced.
//
// Ownership is requested explicitly every frame it isn't already held locally, rather than
// relying solely on RealtimeTransform's Auto Request Ownership flag - confirmed via logging
// that isOwnedLocallySelf stayed false for an entire session of real, confirmed local
// movement, so whatever change-detection Auto Request Ownership uses wasn't picking up
// direct transform.SetPositionAndRotation() calls here.
[RequireComponent(typeof(RealtimeTransform))]
public class ActorPositionSync : MonoBehaviour
{
    public static ActorPositionSync Instance { get; private set; }

    RealtimeTransform realtimeTransform;

    void Awake()
    {
        Instance = this;
        realtimeTransform = GetComponent<RealtimeTransform>();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

#if !DIRECTOR_BUILD
    float lastOwnershipLogTime;

    void Update()
    {
        XROrigin actorRig = FindFirstObjectByType<XROrigin>();
        if (actorRig == null || actorRig.Camera == null)
            return;

        if (!realtimeTransform.isOwnedLocallySelf)
            realtimeTransform.RequestOwnership();

        Transform actorHead = actorRig.Camera.transform;
        transform.SetPositionAndRotation(actorHead.position, actorHead.rotation);

        // TEMPORARY diagnostic - remove once the empty position/rotation issue is confirmed fixed.
        if (Time.time - lastOwnershipLogTime > 1f)
        {
            lastOwnershipLogTime = Time.time;
            Debug.Log($"ActorPositionSync: isOwnedLocallySelf={realtimeTransform.isOwnedLocallySelf} ownerIDSelf={realtimeTransform.ownerIDSelf} pos={transform.position:F3}");
        }
    }
#endif
}
