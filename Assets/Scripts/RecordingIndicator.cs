using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Shows a pulsing red "REC" dot in
// the director's peripheral view while RehearsalRecorder is recording - built and
// destroyed directly from its RecordingStarted/RecordingStopped events (the same
// start/stop calls that drive the actual recording), never a separate timer, so it can't
// drift out of sync with whether a recording is really running.
public class RecordingIndicator : MonoBehaviour
{
#if DIRECTOR_BUILD
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
#endif
}
