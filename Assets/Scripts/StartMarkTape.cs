using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Start mark, like gaffer tape on a set: a yellow "T" on the floor at the shot's start
// (Shot{N}_Start, the marker ShotStartPositioner and StartMarkAligner use). The bar is where
// the toes go, across the facing direction; the stem runs back between the feet. Added to the
// Bootstrap object at startup.
//
// Every take in every mode and both roles - Ensayo Remoto, Ensayar Solo and Inspección - so
// the actor and the director see the same mark. It used to be drawn by SoloRehearsalController
// in solo only. Since StartMarkAligner puts the actor's head over the mark when a take loads,
// the T starts out right under their feet: they have to look down to see it.
//
// Dropped onto whatever floor collider is under the marker (SceneFloor), lifted FloorOffset so
// it sits on top of the visible floor, with no colliders of its own. On the UI layer, so the
// director's virtual cameras (viewfinder, recordings) don't film it - like tape that's on the
// stage but not in the virtual set.
public class StartMarkTape : MonoBehaviour
{
    // In meters. Placeholder look, not tuned.
    const float TapeWidth = 0.06f;
    const float BarLength = 0.6f;
    const float StemLength = 0.4f;
    const float ToeOffset = 0.15f; // bar a little ahead of the mark's center
    // Above the floor *collider*. The visible floor sits ~1.2 cm higher in scenes 2 and 3
    // (measured: spaceship 0.012 vs 0.000, apartment 0.005 vs -0.006), and a 5 mm lift left
    // the tape under it there. 2.5 cm clears both and still reads as on the floor.
    const float FloorOffset = 0.025f;
    static readonly Color TapeColor = new Color(1f, 0.85f, 0.1f, 1f);

    Coroutine buildCoroutine;

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
        if (buildCoroutine != null)
            StopCoroutine(buildCoroutine);
        buildCoroutine = null;

        if (Bootstrap.IsContentScene(scene.name))
            buildCoroutine = StartCoroutine(BuildNextFrame());
    }

    // After the scene's Start(), like FallGuard - CurrentShotNumber and the marker are set.
    System.Collections.IEnumerator BuildNextFrame()
    {
        yield return null;
        buildCoroutine = null;

        GameObject marker = GameObject.Find($"Shot{Bootstrap.CurrentShotNumber}_Start");
        if (marker == null)
        {
            Debug.LogWarning($"[StartMark] no 'Shot{Bootstrap.CurrentShotNumber}_Start' marker in this scene - no tape mark shown.");
            yield break;
        }

        Build(marker.transform);
    }

    static void Build(Transform marker)
    {
        if (!SceneFloor.TryFindFloorBelow(marker.position, out Vector3 floor))
            Debug.LogWarning($"[StartMark] no floor found below the start marker at {marker.position} - mark left at the marker's height.");
        Vector3 position = floor + Vector3.up * FloorOffset;

        Vector3 forward = marker.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        forward.Normalize();

        // A world-space canvas lying flat (1 canvas unit = 1 mm): UI images are unlit, so the
        // tape reads as bright tape whatever the scene's lighting - a lit quad came out dark
        // olive on the mountain set - and the UI shader is always in the build.
        var root = new GameObject("StartMarkTape", typeof(RectTransform), typeof(Canvas));
        SceneManager.MoveGameObjectToScene(root, marker.gameObject.scene);
        root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = new Vector2(BarLength, BarLength) * 1000f; // pivot = the mark's center
        rootRect.SetPositionAndRotation(position, Quaternion.LookRotation(Vector3.down, forward)); // face up, canvas "up" = facing direction
        rootRect.localScale = Vector3.one * 0.001f;

        // Canvas space: +y is the facing direction, origin is the mark's center.
        CreateTapeStrip(rootRect, "Bar", new Vector2(0f, ToeOffset), new Vector2(BarLength, TapeWidth));
        CreateTapeStrip(rootRect, "Stem", new Vector2(0f, ToeOffset - StemLength / 2f), new Vector2(TapeWidth, StemLength));

        XRUISetup.MoveToUILayer(root);

        Debug.Log($"[StartMark] tape mark shown at {position} for shot {Bootstrap.CurrentShotNumber} ({Bootstrap.Mode})");
    }

    // center/size in meters, in the mark's own frame (x = right, y = facing direction).
    static void CreateTapeStrip(RectTransform parent, string name, Vector2 center, Vector2 size)
    {
        var strip = new GameObject(name, typeof(RectTransform), typeof(Image));
        strip.transform.SetParent(parent, false);
        var rect = (RectTransform)strip.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = center * 1000f;
        rect.sizeDelta = size * 1000f;

        Image image = strip.GetComponent<Image>();
        image.color = TapeColor;
        image.raycastTarget = false;
    }
}
