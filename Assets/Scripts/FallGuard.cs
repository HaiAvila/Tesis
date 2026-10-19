using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;

// Safety net for both roles in every content scene: if the rig ends up well below the set's
// floor - off the edge of the set, off the mountain's cliff, or through a gap in the colliders
// - it's put back on the shot's start mark with "Te regresamos al inicio", instead of falling
// forever into the void. Added to the Bootstrap object at startup.
//
// The reference floor is the floor under Shot{N}_Start (SceneFloor), measured once per take.
// The director's free-fly (Inspección) can go below the floor on purpose and is ignored.
// For the actor, the start is then put back under their feet (StartMarkAligner).
public class FallGuard : MonoBehaviour
{
    const float FallLimit = 3f; // meters below the start's floor

    Transform rig;
    Transform marker;
    float floorY;
    bool armed;
    Coroutine armCoroutine;

    void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        armed = false;
        rig = null;
        marker = null;

        if (armCoroutine != null)
            StopCoroutine(armCoroutine);
        armCoroutine = null;

        if (Bootstrap.IsContentScene(scene.name))
            armCoroutine = StartCoroutine(ArmNextFrame());
    }

    // After the scene's Start(), so the rig and the marker are in place.
    System.Collections.IEnumerator ArmNextFrame()
    {
        yield return null;
        armCoroutine = null;

        XROrigin origin = FindFirstObjectByType<XROrigin>();
        GameObject markerObject = GameObject.Find($"Shot{Bootstrap.StartShotNumber}_Start");
        if (origin == null || markerObject == null)
        {
            Debug.LogWarning($"[FallGuard] not armed: rig={(origin != null)}, marker={(markerObject != null)}.");
            yield break;
        }

        rig = origin.transform;
        marker = markerObject.transform;
        floorY = SceneFloor.TryFindFloorBelow(marker.position, out Vector3 floor) ? floor.y : marker.position.y;
        armed = true;
        Debug.Log($"[FallGuard] armed for shot {Bootstrap.StartShotNumber}: floor at y={floorY:F2}, resets below y={floorY - FallLimit:F2}");
    }

    void Update()
    {
        if (!armed || rig == null || marker == null)
            return;

        if (DirectorFreeFlyCamera.Instance != null && DirectorFreeFlyCamera.Instance.IsFlying)
            return;

        if (rig.position.y >= floorY - FallLimit)
            return;

        Debug.Log($"[FallGuard] fell to y={rig.position.y:F2} (floor {floorY:F2}) - back to the start of shot {Bootstrap.StartShotNumber}");

        rig.SetPositionAndRotation(marker.position, marker.rotation);
        Physics.SyncTransforms();
        GravityProvider gravity = rig.GetComponentInChildren<GravityProvider>();
        if (gravity != null)
            gravity.ResetFallForce();

#if !DIRECTOR_BUILD
        if (StartMarkAligner.Instance != null)
            StartMarkAligner.Instance.AlignToStart("fell");
#endif

        FloatingMessage.Show("Te regresamos al inicio");
    }
}
