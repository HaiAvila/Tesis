using Normal.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (added to the Bootstrap object). Says when the connection is the problem, on screen,
// so nobody has to read a log:
// - a banner while this device can't reach Normcore, at startup too, where it would otherwise just
//   wait in silence. Not in Inspección de Escenas, which works offline;
// - "Conexión recuperada" when it's back;
// - for the director in a take, whether the actor was lost: a recording keeps going, but the actor
//   stands still in it until they return.
public class ConnectionStatus : MonoBehaviour
{
    const float StartupGraceSeconds = 4f;

    // Top center, head-locked. Placeholder position.
    static readonly Vector3 BannerOffset = new Vector3(0f, 0.15f, 0.5f);
    const float BannerScale = 0.0003f;
    const int BannerFontSize = 48;
    static readonly Vector2 BannerSize = new Vector2(1000f, 84f);
    static readonly Color BannerColor = new Color(0.6f, 0.12f, 0.12f, 0.9f);

    Realtime realtime;
    GameObject banner;
    bool hasBeenConnected;
    bool wasDisconnected;
    float startTime;
#if DIRECTOR_BUILD
    bool actorWasLive;
#endif

    void Start()
    {
        realtime = GetComponentInChildren<Realtime>();
        startTime = Time.unscaledTime;
    }

    void Update()
    {
        if (realtime == null)
            return;

        bool connected = realtime.connected;
        if (connected)
            hasBeenConnected = true;

        bool disconnected = !connected && (hasBeenConnected || Time.unscaledTime - startTime > StartupGraceSeconds);
        if (wasDisconnected && connected)
            FloatingMessage.Show("Conexión recuperada");
        wasDisconnected = disconnected;

        bool show = disconnected && Bootstrap.Mode != Bootstrap.AppMode.Inspect;
        if (show && banner == null)
            BuildBanner();
        else if (!show && banner != null)
        {
            Destroy(banner);
            banner = null;
        }

#if DIRECTOR_BUILD
        UpdateActor(connected);
#endif
    }

#if DIRECTOR_BUILD
    void UpdateActor(bool connected)
    {
        bool inTake = Bootstrap.Mode == Bootstrap.AppMode.Remote && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);
        bool live = ActorPositionSync.Instance != null && ActorPositionSync.Instance.IsLive;
        if (inTake && connected)
        {
            if (actorWasLive && !live)
            {
                bool recording = RehearsalRecorder.Instance != null && RehearsalRecorder.Instance.IsRecording;
                Debug.Log($"[Connection] the actor was lost (recording: {recording})");
                FloatingMessage.Show("Se perdió la conexión con el actor");
            }
        }
        actorWasLive = live;
    }
#endif

    void BuildBanner()
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        banner = new GameObject("ConnectionBanner", typeof(RectTransform), typeof(Canvas), typeof(Image));
        banner.transform.SetParent(head.transform, false);
        banner.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

        var rect = (RectTransform)banner.transform;
        rect.sizeDelta = BannerSize;
        rect.localPosition = BannerOffset;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * BannerScale;

        Image background = banner.GetComponent<Image>();
        background.color = BannerColor;
        background.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(rect, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        Text label = textObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text = "Sin conexión con el servidor. Reintentando…";
        label.fontSize = BannerFontSize;
        label.fontStyle = FontStyle.Bold;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.raycastTarget = false;

        XRUISetup.MoveToUILayer(banner);
    }
}
