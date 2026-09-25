using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). When a content scene loads while
// Bootstrap.IsInspecting is true, loads the most recent recorded session for the current
// shot and plays it back on a placeholder capsule at the original relative speed between
// samples (not instant, not accelerated), with simple play/pause. Never touches the
// director's own camera/rig - the capsule is a separate object.
public class RecordingPlaybackController : MonoBehaviour
{
    const float CapsuleHeight = 1.8f;
    const float CapsuleRadius = 0.25f;
    const float ButtonFontSize = 28f;
    const float ButtonWidth = 260f;
    const float ButtonHeight = 80f;
    const float PanelWorldScale = 0.003f;

    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.9f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);
    static readonly Color CapsuleColor = new Color(1f, 0.75f, 0.1f, 1f);

    GameObject capsule;
    GameObject panelObject;
    Text statusText;
    Font uiFont;

    List<Metric> samples;
    float playbackTime;
    bool isPlaying;
    Coroutine playbackCoroutine;

    void Awake()
    {
        uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CleanUp();

        if (!Bootstrap.IsInspecting || !Bootstrap.IsContentScene(scene.name))
            return;

        LoadAndStart(Bootstrap.CurrentShotNumber);
    }

    void CleanUp()
    {
        if (playbackCoroutine != null)
            StopCoroutine(playbackCoroutine);
        playbackCoroutine = null;

        if (capsule != null)
            Destroy(capsule);
        if (panelObject != null)
            Destroy(panelObject);

        capsule = null;
        panelObject = null;
        statusText = null;
        samples = null;
        playbackTime = 0f;
        isPlaying = false;
    }

    void LoadAndStart(int shotNumber)
    {
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("RecordingPlaybackController: no ShotListDatabase instance found.");
            return;
        }

        RecordingSession session = database.GetMostRecentSession(shotNumber);
        if (session == null)
        {
            Debug.LogWarning($"RecordingPlaybackController: no recorded session found for shot {shotNumber}.");
            return;
        }

        samples = database.GetMetricsForSession(session.Id);
        if (samples.Count == 0)
        {
            Debug.LogWarning($"RecordingPlaybackController: session {session.Id} for shot {shotNumber} has no samples.");
            samples = null;
            return;
        }

        BuildCapsule();
        BuildPanel();
        ApplySampleAt(0f);
        SetPlaying(true);

        playbackCoroutine = StartCoroutine(PlaybackLoop());
    }

    void BuildCapsule()
    {
        capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "PlaybackCapsule";
        capsule.transform.localScale = new Vector3(CapsuleRadius * 2f, CapsuleHeight * 0.5f, CapsuleRadius * 2f);
        Object.Destroy(capsule.GetComponent<Collider>());
        capsule.GetComponent<Renderer>().material.color = CapsuleColor;
    }

    void BuildPanel()
    {
        Vector3 startPosition = new Vector3(samples[0].PosX, samples[0].PosY, samples[0].PosZ);
        Vector3 panelPosition = startPosition + Vector3.up * 2f;

        panelObject = new GameObject("PlaybackPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        RectTransform canvasRect = (RectTransform)panelObject.transform;
        canvasRect.sizeDelta = new Vector2(ButtonWidth + 40f, ButtonHeight + 40f);
        canvasRect.position = panelPosition;
        canvasRect.rotation = Quaternion.identity;
        canvasRect.localScale = Vector3.one * PanelWorldScale;

        var buttonObject = new GameObject("Button_PlayPause", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(canvasRect, false);
        RectTransform buttonRect = (RectTransform)buttonObject.transform;
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
        buttonObject.GetComponent<Image>().color = ButtonColor;
        buttonObject.GetComponent<Button>().onClick.AddListener(OnPlayPauseClicked);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(buttonObject.transform, false);
        RectTransform textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        statusText = textObject.GetComponent<Text>();
        statusText.font = uiFont;
        statusText.fontSize = (int)ButtonFontSize;
        statusText.fontStyle = FontStyle.Bold;
        statusText.color = TextColor;
        statusText.alignment = TextAnchor.MiddleCenter;

        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
    }

    void OnPlayPauseClicked()
    {
        if (!isPlaying && samples != null && playbackTime >= samples[samples.Count - 1].TiempoRelativo)
            playbackTime = 0f;

        SetPlaying(!isPlaying);
    }

    void SetPlaying(bool playing)
    {
        isPlaying = playing;
        if (statusText != null)
            statusText.text = isPlaying ? "Pausar" : "Reproducir";
    }

    IEnumerator PlaybackLoop()
    {
        while (true)
        {
            if (isPlaying && samples != null)
            {
                playbackTime += Time.deltaTime;
                float totalDuration = samples[samples.Count - 1].TiempoRelativo;

                if (playbackTime >= totalDuration)
                {
                    playbackTime = totalDuration;
                    SetPlaying(false);
                }

                ApplySampleAt(playbackTime);
            }

            yield return null;
        }
    }

    void ApplySampleAt(float time)
    {
        if (samples == null || samples.Count == 0 || capsule == null)
            return;

        Metric first = samples[0];
        if (time <= first.TiempoRelativo)
        {
            SetCapsuleTransform(first);
            return;
        }

        Metric last = samples[samples.Count - 1];
        if (time >= last.TiempoRelativo)
        {
            SetCapsuleTransform(last);
            return;
        }

        for (int i = 0; i < samples.Count - 1; i++)
        {
            Metric a = samples[i];
            Metric b = samples[i + 1];
            if (time < a.TiempoRelativo || time > b.TiempoRelativo)
                continue;

            float span = b.TiempoRelativo - a.TiempoRelativo;
            float t = span > 0f ? (time - a.TiempoRelativo) / span : 0f;

            Vector3 position = Vector3.Lerp(
                new Vector3(a.PosX, a.PosY, a.PosZ),
                new Vector3(b.PosX, b.PosY, b.PosZ), t);
            Quaternion rotation = Quaternion.Slerp(
                new Quaternion(a.RotX, a.RotY, a.RotZ, a.RotW),
                new Quaternion(b.RotX, b.RotY, b.RotZ, b.RotW), t);

            capsule.transform.SetPositionAndRotation(position, rotation);
            return;
        }
    }

    void SetCapsuleTransform(Metric sample)
    {
        capsule.transform.SetPositionAndRotation(
            new Vector3(sample.PosX, sample.PosY, sample.PosZ),
            new Quaternion(sample.RotX, sample.RotY, sample.RotZ, sample.RotW));
    }
}
