using Unity.XR.CoreUtils;
using UnityEngine;

// The set's floor under a point - used for the solo tape start mark and by FallGuard.
//
// The Shot{N}_Start markers float well above the floor (1.6 m up in every scene - the rig
// drops onto the floor from there), so this looks all the way down, not just near the point:
// the first collider below, skipping triggers and the XR rig's own colliders.
public static class SceneFloor
{
    const float StartAbove = 0.5f;     // start just above the point
    const float MaxDistance = 50f;     // and look all the way down

    public static bool TryFindFloorBelow(Vector3 point, out Vector3 floor)
    {
        Vector3 origin = point + Vector3.up * StartAbove;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, MaxDistance, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.GetComponentInParent<XROrigin>() != null)
                continue;

            floor = hit.point;
            return true;
        }

        floor = point;
        return false;
    }
}
