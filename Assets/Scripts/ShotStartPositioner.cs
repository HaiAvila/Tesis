using UnityEngine;

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

        string markerName = $"Shot{Bootstrap.CurrentShotNumber}_Start";
        GameObject marker = GameObject.Find(markerName);

        if (marker == null)
        {
            Debug.LogWarning($"ShotStartPositioner: marker '{markerName}' not found in scene, skipping actor placement.");
            return;
        }

        actor.SetPositionAndRotation(marker.transform.position, marker.transform.rotation);
    }
}
