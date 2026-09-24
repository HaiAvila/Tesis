using Normal.Realtime;
using Unity.XR.CoreUtils;
using UnityEngine;

// Persistent (lives under Bootstrap, DontDestroyOnLoad), networked representation of the
// actor's XR Origin position/rotation - the "director has a synced representation of
// where the actor is" prerequisite, and what RehearsalRecorder samples while recording.
//
// This deliberately does NOT sync the XR Origin itself: both the actor and director build
// instantiate the exact same scene-placed XR Origin as their OWN local rig, so networking
// it directly would make the director's own headset movement get overwritten by whichever
// client owns it. Instead this is a separate object that only the actor build writes into
// (RealtimeTransform's Auto Request Ownership takes care of claiming ownership whenever it
// moves); the director build only ever reads it, already synced.
[RequireComponent(typeof(RealtimeTransform))]
public class ActorPositionSync : MonoBehaviour
{
    public static ActorPositionSync Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

#if !DIRECTOR_BUILD
    void Update()
    {
        XROrigin actorRig = FindFirstObjectByType<XROrigin>();
        if (actorRig == null)
            return;

        transform.SetPositionAndRotation(actorRig.transform.position, actorRig.transform.rotation);
    }
#endif
}
