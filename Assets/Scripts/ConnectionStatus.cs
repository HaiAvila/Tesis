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
    // A red icon on the glass: urgent, without a slab of red over the view.
    const float BannerScale = 0.0003f;
    const int BannerFontSize = 48;
    static readonly Vector2 BannerSize = new Vector2(1000f, 84f);
    static readonly Color BannerColor = new Color(0.95f, 0.38f, 0.38f, 1f);

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

        banner = GlassUI.HudChip(head.transform, "ConnectionBanner", BannerOffset, GlassUI.Icon.WifiOff,
            "Sin conexión con el servidor. Reintentando…", BannerColor, 0.5f).gameObject;
    }
}
