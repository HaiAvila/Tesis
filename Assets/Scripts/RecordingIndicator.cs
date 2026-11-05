using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Shows a pulsing red "REC" dot in
// the director's peripheral view while RehearsalRecorder is recording - built and
// destroyed directly from its RecordingStarted/RecordingStopped events (the same
// start/stop calls that drive the actual recording), never a separate timer, so it can't
// drift out of sync with whether a recording is really running.
//
// The actor gets the same dot plus the word "Grabando", in Ensayo Remoto, following the
// recording state the director syncs (RehearsalSessionSync.IsRecording): without it they can't
// tell a take has started.
public class RecordingIndicator : MonoBehaviour
{
    const int CircleTextureSize = 64;
    const float DotDiameter = 0.035f;
    const float PulseMinAlpha = 0.35f;
    const float PulsePeriod = 1.1f;

    // Parented to the camera, not placed in the world: fixed in the upper-left periphery
    // of view regardless of where the director looks, like a battery/notification HUD.
    static readonly Vector3 LocalOffset = new Vector3(-0.14f, 0.12f, 0.5f);
    static readonly Color DotColor = new Color(0.9f, 0.08f, 0.08f, 1f);

    static Sprite cachedCircleSprite;

    GameObject indicatorObject;

#if !DIRECTOR_BUILD
    // To the right of the dot, head-locked like it.
    static readonly Vector3 ActorLabelOffset = new Vector3(-0.115f, 0.12f, 0.5f);
    const float ActorLabelScale = 0.0003f;
    const int ActorLabelFontSize = 56;
    static readonly Vector2 ActorLabelSize = new Vector2(320f, 76f);

    void Update()
    {
        bool recording = Bootstrap.Mode == Bootstrap.AppMode.Remote
                         && RehearsalSessionSync.Instance != null && RehearsalSessionSync.Instance.IsRecording
                         && Bootstrap.IsContentScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);

        if (recording && indicatorObject == null)
        {
            Camera headCamera = Camera.main;
            if (headCamera == null)
                return;
            BuildIndicator(headCamera.transform);
            BuildActorLabel();
        }
        else if (!recording && indicatorObject != null)
        {
            Destroy(indicatorObject);
            indicatorObject = null;
        }
    }

    // A child of the dot, so it goes with it. The dot's canvas is scaled to the dot, hence the scale.
    void BuildActorLabel()
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelObject.transform.SetParent(indicatorObject.transform, false);

        var rect = (RectTransform)labelObject.transform;
        rect.sizeDelta = ActorLabelSize;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.SetPositionAndRotation(indicatorObject.transform.parent.TransformPoint(ActorLabelOffset), indicatorObject.transform.rotation);
        rect.localScale = Vector3.one * (ActorLabelScale / DotDiameter);

        Text label = labelObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = "Grabando";
        label.fontSize = ActorLabelFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.raycastTarget = false;

        XRUISetup.MoveToUILayer(indicatorObject);
    }
#endif

#if DIRECTOR_BUILD
    void Start()
    {
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted += OnRecordingStarted;
            RehearsalRecorder.Instance.RecordingStopped += OnRecordingStopped;
        }
    }

    void OnDestroy()
    {
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted -= OnRecordingStarted;
            RehearsalRecorder.Instance.RecordingStopped -= OnRecordingStopped;
        }
    }
#endif

    void OnRecordingStarted()
    {
        if (indicatorObject != null)
            Destroy(indicatorObject);

        Camera headCamera = Camera.main;
        if (headCamera == null)
        {
            Debug.LogWarning("RecordingIndicator: no Camera.main found, cannot show REC indicator.");
            return;
        }

        BuildIndicator(headCamera.transform);
    }

    void OnRecordingStopped()
    {
        // Disappearing entirely, rather than just freezing the pulse, so there's never
        // doubt about whether it's still recording.
        if (indicatorObject != null)
            Destroy(indicatorObject);
        indicatorObject = null;
    }

    void BuildIndicator(Transform head)
    {
        indicatorObject = new GameObject("RecordingIndicator", typeof(RectTransform), typeof(Canvas));
        indicatorObject.transform.SetParent(head, false);

        Canvas canvas = indicatorObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        RectTransform canvasRect = (RectTransform)indicatorObject.transform;
        canvasRect.sizeDelta = new Vector2(1f, 1f);
        canvasRect.localPosition = LocalOffset;
        canvasRect.localRotation = Quaternion.identity;
        canvasRect.localScale = Vector3.one * DotDiameter;

        var dotObject = new GameObject("Dot", typeof(RectTransform), typeof(Image));
        dotObject.transform.SetParent(canvasRect, false);
        RectTransform dotRect = (RectTransform)dotObject.transform;
        dotRect.anchorMin = Vector2.zero;
        dotRect.anchorMax = Vector2.one;
        dotRect.offsetMin = Vector2.zero;
        dotRect.offsetMax = Vector2.zero;

        Image dotImage = dotObject.GetComponent<Image>();
        dotImage.sprite = GetCircleSprite();
        dotImage.color = DotColor;

        // Head-locked, so the viewfinder camera would otherwise see it floating in the shot;
        // the viewfinder has its own REC badge on the panel instead.
        XRUISetup.MoveToUILayer(indicatorObject);

        StartCoroutine(Pulse(dotImage));
    }

    IEnumerator Pulse(Image dotImage)
    {
        while (dotImage != null)
        {
            float t = (Mathf.Sin(Time.time * (2f * Mathf.PI / PulsePeriod)) + 1f) * 0.5f;
            Color color = DotColor;
            color.a = Mathf.Lerp(PulseMinAlpha, 1f, t);
            dotImage.color = color;
            yield return null;
        }
    }

    static Sprite GetCircleSprite()
    {
        if (cachedCircleSprite != null)
            return cachedCircleSprite;

        var texture = new Texture2D(CircleTextureSize, CircleTextureSize, TextureFormat.RGBA32, false);
        float radius = CircleTextureSize * 0.5f;
        var center = new Vector2(radius, radius);

        for (int y = 0; y < CircleTextureSize; y++)
        {
            for (int x = 0; x < CircleTextureSize; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float alpha = Mathf.Clamp01(radius - distance);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply();

        cachedCircleSprite = Sprite.Create(texture, new Rect(0, 0, CircleTextureSize, CircleTextureSize), new Vector2(0.5f, 0.5f));
        return cachedCircleSprite;
    }
}
