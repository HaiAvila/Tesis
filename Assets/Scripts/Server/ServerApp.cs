using System.Collections.Generic;
using System.Text;
using Normal.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The server (SERVER_BUILD): the .exe that runs on the PC where the pods are - next to the
// actor in a rehearsal, the previz PC on a shoot day. Bootstrap adds this in that build only.
// No headset, no menus, nothing to configure: it joins the room, reads the receiver on USB
// (SerialBridge), turns the pods into the actor's avatar and sends it (PodAvatar,
// AvatarPoseSync), plays the cues (CueEngine) and keeps everyone's takes (TakeSync). All of
// those already work on "whichever machine has the pods"; this only sets the machine up and
// shows what's going on.
//
// The window: on the left the room, the receiver, each pod (battery, signal), the calibration,
// the take in progress and the stored takes; on the right the actor's avatar as the pods move
// it, seen from behind (the actor's left is on the left). Nothing to click.
public class ServerApp : MonoBehaviour
{
    public static ServerApp Instance { get; private set; }

    const float PanelWidth = 560f;
    const float Margin = 20f;
    const float RefreshSeconds = 0.25f;
    const float NoticeSeconds = 8f;
    const float CameraDistance = 3.9f;
    const float CameraHeight = 1.25f;

    static readonly Color Good = new Color(0.45f, 0.9f, 0.55f);
    static readonly Color Warn = new Color(1f, 0.72f, 0.3f);
    static readonly Color Bad = new Color(1f, 0.45f, 0.45f);
    static readonly Color Dim = new Color(1f, 1f, 1f, 0.55f);

    Realtime realtime;
    SerialBridge serial;
    PodReceiver receiver;
    PodAvatar podAvatar;
    Camera view;
    Transform ground;
    TMP_Text body;
    TMP_Text avatarCaption;
    float nextRefresh;
    string notice = "";
    float noticeUntil;
    readonly List<PodStatusEntry> pods = new List<PodStatusEntry>();
    readonly StringBuilder text = new StringBuilder(2048);

    // FloatingMessage, on the server: there's no head to put a message in front of.
    public static void Notify(string message)
    {
        if (Instance == null)
            return;
        Instance.notice = message;
        Instance.noticeUntil = Time.unscaledTime + NoticeSeconds;
    }

    void Awake()
    {
        Instance = this;
        Application.runInBackground = true; // it keeps working behind other windows
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
#if !UNITY_EDITOR
        Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
#endif
        StopXR();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    // A PC build starts OpenXR when it can (a headset on Link would wake up for the server).
    static void StopXR()
    {
        var settings = UnityEngine.XR.Management.XRGeneralSettings.Instance;
        if (settings == null || settings.Manager == null || settings.Manager.activeLoader == null)
            return;
        settings.Manager.StopSubsystems();
        settings.Manager.DeinitializeLoader();
        Debug.Log("[Server] XR stopped: the server has no headset");
    }

    void Start()
    {
        realtime = GetComponentInChildren<Realtime>();

        AvatarPoseSync avatarSync = GetComponentInChildren<AvatarPoseSync>(true);
        if (avatarSync != null)
        {
            avatarSync.onlyInContentScenes = false; // the server has no scenes: always show it
            receiver = avatarSync.receiver != null ? avatarSync.receiver : avatarSync.GetComponentInChildren<PodReceiver>();
            podAvatar = avatarSync.podAvatar != null ? avatarSync.podAvatar : avatarSync.GetComponentInChildren<PodAvatar>();
        }
        if (receiver != null)
        {
            serial = receiver.GetComponent<SerialBridge>();
            if (serial == null)
                serial = receiver.gameObject.AddComponent<SerialBridge>();
        }
        else
        {
            Debug.LogError("[Server] no PodReceiver under Bootstrap: the pods won't be heard.");
        }

        BuildView();
        BuildWindow();
        Debug.Log("[Server] server window up");
    }

    // --- the avatar's view ------------------------------------------------------------------

    void BuildView()
    {
        // Bootstrap's own camera, kept (the server never loads another scene). Not "MainCamera":
        // the headsets' head-locked indicators look for one, and there's no head here.
        view = Camera.main;
        if (view == null)
        {
            var cameraObject = new GameObject("ServerCamera", typeof(Camera));
            view = cameraObject.GetComponent<Camera>();
        }
        view.gameObject.tag = "Untagged";
        view.clearFlags = CameraClearFlags.SolidColor;
        view.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        view.fieldOfView = 40f;
        view.nearClipPlane = 0.1f;
        view.rect = new Rect(PanelWidth / 1280f, 0f, 1f - PanelWidth / 1280f, 1f);
        view.transform.SetPositionAndRotation(new Vector3(0f, CameraHeight, -CameraDistance), Quaternion.identity);

        GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = "ServerGround";
        Destroy(disc.GetComponent<Collider>());
        ground = disc.transform;
        ground.localScale = new Vector3(2.4f, 0.005f, 2.4f);
        Renderer discRenderer = disc.GetComponent<Renderer>();
        discRenderer.material.color = new Color(0.3f, 0.33f, 0.38f);
        discRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void LateUpdate()
    {
        AvatarPoseSync avatarSync = AvatarPoseSync.Instance;
        if (view == null || avatarSync == null || avatarSync.avatar == null)
            return;
        if (!avatarSync.IsSending && !avatarSync.IsReceiving)
            return;

        // Behind the avatar, following where the actor walks in the set.
        Transform root = avatarSync.avatar.transform;
        float scale = Mathf.Max(0.2f, root.lossyScale.y);
        Vector3 target = root.position + Vector3.up * (1.0f * scale);
        Vector3 wanted = root.position - root.forward * CameraDistance + Vector3.up * CameraHeight;
        float blend = 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime);
        view.transform.position = Vector3.Lerp(view.transform.position, wanted, blend);
        view.transform.rotation = Quaternion.Slerp(view.transform.rotation, Quaternion.LookRotation(target - view.transform.position, Vector3.up), blend);
        if (ground != null)
            ground.position = root.position;
    }

    // --- the window ---------------------------------------------------------------------------

    void BuildWindow()
    {
        var canvasObject = new GameObject("ServerWindow", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(transform, false);
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.matchWidthOrHeight = 0.5f;

        var panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panelObject.transform.SetParent(canvasObject.transform, false);
        var panel = (RectTransform)panelObject.transform;
        panel.anchorMin = new Vector2(0f, 0f);
        panel.anchorMax = new Vector2(0f, 1f);
        panel.pivot = new Vector2(0f, 0.5f);
        panel.offsetMin = new Vector2(0f, 0f);
        panel.offsetMax = new Vector2(PanelWidth, 0f);
        Image background = panelObject.GetComponent<Image>();
        background.color = new Color(0.07f, 0.08f, 0.1f, 1f);
        background.raycastTarget = false;

        TextMeshProUGUI title = GlassUI.Label(panel, "Servidor", 30f, true, null, TextAlignmentOptions.TopLeft);
        Place((RectTransform)title.transform, panel, Margin, Margin, PanelWidth - Margin * 2f, 40f);
        TextMeshProUGUI subtitle = GlassUI.Label(panel, "Deja esta ventana abierta durante el ensayo.", 15f, false, Dim, TextAlignmentOptions.TopLeft);
        Place((RectTransform)subtitle.transform, panel, Margin, Margin + 40f, PanelWidth - Margin * 2f, 24f);

        TextMeshProUGUI content = GlassUI.Label(panel, "", 17f, false, null, TextAlignmentOptions.TopLeft);
        Place((RectTransform)content.transform, panel, Margin, Margin + 76f, PanelWidth - Margin * 2f, 720f - Margin * 2f - 76f);
        content.richText = true;
        content.enableWordWrapping = true;
        content.overflowMode = TextOverflowModes.Truncate;
        content.lineSpacing = 6f;
        body = content;

        TextMeshProUGUI caption = GlassUI.Label(canvasObject.transform, "", 18f, false, Dim, TextAlignmentOptions.Bottom);
        var captionRect = (RectTransform)caption.transform;
        captionRect.anchorMin = new Vector2(0f, 0f);
        captionRect.anchorMax = new Vector2(1f, 0f);
        captionRect.pivot = new Vector2(0.5f, 0f);
        captionRect.offsetMin = new Vector2(PanelWidth + Margin, Margin);
        captionRect.offsetMax = new Vector2(-Margin, Margin + 60f);
        caption.enableWordWrapping = true;
        avatarCaption = caption;
    }

    // Top-left coordinates inside the panel, y down.
    static void Place(RectTransform rect, RectTransform parent, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    void Update()
    {
        if (body == null || Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + RefreshSeconds;

        text.Clear();
        WriteRoom();
        WriteReceiver();
        WritePods();
        WriteCalibration();
        WriteTake();
        WriteStore();
        if (Time.unscaledTime < noticeUntil && !string.IsNullOrEmpty(notice))
            text.Append('\n').Append(Paint(notice, Dim));
        body.text = text.ToString();

        AvatarPoseSync avatarSync = AvatarPoseSync.Instance;
        bool avatarShown = avatarSync != null && (avatarSync.IsSending || avatarSync.IsReceiving);
        avatarCaption.text = avatarShown
            ? "El avatar del actor, visto desde atrás"
            : "El avatar aparece cuando el actor calibra (botón Y de su visor)";
        if (ground != null)
            ground.gameObject.SetActive(avatarShown);
    }

    static string Paint(string value, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{value}</color>";
    static string Heading(string value) => $"<size=14><color=#{ColorUtility.ToHtmlStringRGBA(Dim)}>{value.ToUpperInvariant()}</color></size>\n";

    void WriteRoom()
    {
        text.Append(Heading("Sala"));
        bool connected = realtime != null && realtime.connected;
        text.Append(connected ? Paint("Conectado a la sala", Good) : Paint("Conectando a la sala… (revisa el internet de esta computadora)", Warn)).Append('\n');

        bool director = false, actor = false;
        foreach (SyncPeer peer in SyncPeer.All)
        {
            if (peer == null || peer.IsMine || !peer.Ready)
                continue;
            director |= peer.Role == SyncRole.Director;
            actor |= peer.Role == SyncRole.Actor;
        }
        // The actor's headset also shows as the head that moves the avatar.
        actor |= ActorPositionSync.Instance != null && ActorPositionSync.Instance.IsLive;
        text.Append("Visor del actor: ").Append(actor ? Paint("en la sala", Good) : Paint("no está", Dim));
        text.Append("   ·   Director: ").Append(director ? Paint("en la sala", Good) : Paint("no está", Dim)).Append("\n\n");
    }

    void WriteReceiver()
    {
        text.Append(Heading("Receptor (USB)"));
        bool hearsPods = receiver != null && receiver.ActivePods().Length > 0;
        if (serial != null && serial.State == SerialBridge.BridgeState.Connected)
            text.Append(Paint(serial.Detail, Good));
        else if (hearsPods)
            text.Append(Paint("Los pods llegan por bridge.py", Good));
        else if (serial != null && serial.State == SerialBridge.BridgeState.Busy)
            text.Append(Paint(serial.Detail, Bad));
        else
            text.Append(Paint(serial != null && !string.IsNullOrEmpty(serial.Detail) ? serial.Detail : "Buscando el receptor…", Warn));
        text.Append("\n\n");
    }

    void WritePods()
    {
        pods.Clear();
        if (receiver != null && podAvatar != null)
            PodStatus.Decode(PodStatus.Encode(receiver, podAvatar.mapping), podAvatar.mapping, pods);

        int connected = 0;
        foreach (PodStatusEntry pod in pods)
        {
            if (pod.connected)
                connected++;
        }
        text.Append(Heading($"Pods {connected}/{pods.Count}"));

        foreach (PodStatusEntry pod in pods)
        {
            text.Append(pod.connected ? Paint("●", pod.hasSensor ? Good : Warn) : Paint("○", Dim)).Append(' ');
            text.Append(pod.connected ? pod.Name : Paint(pod.Name, Dim));
            text.Append("<pos=58%>");
            if (!pod.connected)
                text.Append(Paint("apagado o sin señal", Dim));
            else if (!pod.hasSensor)
                text.Append(Paint("sin sensor de movimiento", Warn));
            else if (pod.hasHealth && pod.health.HasBattery)
            {
                string battery = $"{pod.health.BatteryPercent} %" + (pod.health.IsCharging ? " cargando" : "");
                text.Append(pod.health.IsLow ? Paint(battery + " batería baja", Warn) : battery);
                if (pod.health.HasRssi)
                    text.Append("  ·  ").Append(PodStatus.SignalLabel(pod.health.rssi));
            }
            else
                text.Append("conectado");
            text.Append('\n');
        }
        text.Append('\n');
    }

    void WriteCalibration()
    {
        text.Append(Heading("Calibración"));
        AvatarPoseSync avatarSync = AvatarPoseSync.Instance;
        if (avatarSync == null || podAvatar == null)
        {
            text.Append(Paint("No disponible", Bad)).Append("\n\n");
            return;
        }

        string instruction = AvatarPoseSync.PhaseInstruction(podAvatar.CurrentPhase);
        if (instruction != null)
            text.Append(Paint(instruction, Warn));
        else if (avatarSync.IsSending)
        {
            int solved = 0;
            foreach (var calibration in podAvatar.Calibrations)
            {
                if (calibration.Value.ok)
                    solved++;
            }
            text.Append(Paint($"Lista: {solved} pods calibrados", Good));
        }
        else if (podAvatar.IsCalibrated && !avatarSync.HasPodsHere)
            text.Append(Paint("Calibrado, pero ya no llegan los pods", Warn));
        else
            text.Append(Paint("Sin calibrar: el actor presiona Y en su visor, o el director elige Calibrar", Dim));
        text.Append("\n\n");
    }

    void WriteTake()
    {
        text.Append(Heading("Toma"));
        RehearsalSessionSync session = RehearsalSessionSync.Instance;
        RehearsalSessionState state = session != null ? session.GetComponent<RehearsalSessionState>() : null;
        SoloSession solo = SoloSession.Instance;
        bool playing = CueEngine.Instance != null && CueEngine.Instance.IsPlaying;

        if (solo != null && solo.InSolo)
            text.Append($"Ensayar Solo · {TakeName(solo.SceneNumber, solo.ShotNumber)}");
        else if (state != null && state.HasOwner && state.State == RehearsalSessionState.StateActive && state.ShotNumber != RehearsalSessionState.NoShotSelected)
            text.Append($"Ensayo Remoto · {TakeName(state.SceneNumber, state.ShotNumber)}");
        else
            text.Append(Paint("Ninguna por ahora", Dim));

        bool recording = session != null && session.IsRecording;
        if (recording || playing)
            text.Append('\n');
        if (recording)
            text.Append(Paint("● Grabando", Bad)).Append("   ");
        if (playing)
            text.Append(Paint("Pistas hápticas en curso", Good));
        text.Append("\n\n");
    }

    static string TakeName(int scene, int shot) =>
        shot == RehearsalSessionState.FullSceneShot ? $"Escena {scene} completa" : $"Escena {scene}, shot {shot}";

    void WriteStore()
    {
        text.Append(Heading("Tomas guardadas en esta computadora"));
        TakeSync sync = TakeSync.Instance;
        text.Append(sync != null ? sync.StatusText : "—").Append('\n');
        foreach (SyncPeer peer in SyncPeer.All)
        {
            if (peer == null || peer.IsMine || !peer.Ready || string.IsNullOrEmpty(peer.Status))
                continue;
            string who = peer.Role == SyncRole.Director ? "Director" : peer.Role == SyncRole.Actor ? "Visor del actor" : "Otro servidor";
            text.Append(Paint($"{who}: {peer.Status}", Dim)).Append('\n');
        }
    }
}
