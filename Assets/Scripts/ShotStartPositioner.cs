using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;

public class ShotStartPositioner : MonoBehaviour
{
    public Transform actor;

    void Start()
    {
        if (actor == null)
        {
            Debug.LogWarning("ShotStartPositioner: no actor Transform assigned, skipping placement.");
            return;
        }

        string markerName = $"Shot{Bootstrap.StartShotNumber}_Start";
        GameObject marker = GameObject.Find(markerName);

        if (marker == null)
        {
            Debug.LogWarning($"ShotStartPositioner: marker '{markerName}' not found in scene, skipping actor placement.");
            return;
        }

        actor.SetPositionAndRotation(marker.transform.position, marker.transform.rotation);

        // GravityProvider only calls ResetFallForce() itself once it detects ground
        // AND locomotion state is already "Moving" - right after a teleport it isn't
        // "Moving" yet, so any fall velocity accumulated before the teleport would
        // otherwise survive it.
        GravityProvider gravityProvider = actor.GetComponentInChildren<GravityProvider>();
        if (gravityProvider != null)
            gravityProvider.ResetFallForce();
    }
}
