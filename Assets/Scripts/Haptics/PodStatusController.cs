using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (added to the Bootstrap object). What each person gets to know about the pods:
//
// - Both: pause menu → Estado de pods, one row per pod (ShowPanel).
// - Actor: TutorialController's "Antes de ensayar" panel warns before a take (MissingPods,
//   LowPods). Nothing during a take, so they stay in the scene. In Ensayar Solo, with no director
//   to call cut, a pod lost during the take is reported when it ends.
// - Director, in Ensayo Remoto: "Pods 9/9" under the REC dot, amber with a low battery, red with
//   a pod missing; a message if a pod drops; and another when recording with one missing, which
//   is allowed (a take with 8 pods can be what's wanted).
//
// The data comes from AvatarPoseSync.LatestPodStatus, whichever machine has the pods.
public class PodStatusController : MonoBehaviour
{
    public static PodStatusController Instance { get; private set; }

    const float RefreshSeconds = 0.5f;
    // A buzz sags the voltage for a moment: once low, a pod stays low until clearly above it.
    const int LowRecoverMillivolts = PodReceiver.PodHealth.LowBatteryMillivolts + 50;

    const float PanelDistance = 1.5f;
    const float PanelWorldScale = 0.0016f;
    const float PanelWidth = 1000f;
    const float PanelPadding = 44f;
    const float RowHeight = 54f;
    const float RowSpacing = 6f;
    const float TitleHeight = 64f;
    const float CloseButtonHeight = 76f;
    const float CloseButtonWidth = 280f;
    const int TitleFontSize = 34;
    const int RowFontSize = 30;
    const int ButtonFontSize = 30;

    // Under the REC dot (RecordingIndicator.LocalOffset), head-locked like it.
    static readonly Vector3 IndicatorOffset = RecordingIndicator.ChipOffset + new Vector3(0f, -GlassUI.ChipSpacing, 0f);
    const float IndicatorScale = 0.0003f;
    const int IndicatorFontSize = 56;
    static readonly Vector2 IndicatorSize = new Vector2(280f, 76f);
    const float SoloReportDelaySeconds = 1.6f; // after "Detenido" fades

    static readonly Color BackgroundColor = new Color(0.1f, 0.1f, 0.14f, 0.92f);
    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.95f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);
    static readonly Color SubTextColor = new Color(0.7f, 0.7f, 0.76f, 1f);
    static readonly Color WarningColor = new Color(1f, 0.72f, 0.2f, 1f);
    static readonly Color ProblemColor = new Color(1f, 0.38f, 0.33f, 1f);
    static readonly Color IndicatorBackground = new Color(0.05f, 0.05f, 0.08f, 0.6f);

    readonly List<PodStatusEntry> entries = new List<PodStatusEntry>();
    readonly HashSet<int> lowPods = new HashSet<int>();
    readonly HashSet<int> connectedBefore = new HashSet<int>();
    readonly List<int> lostDuringSoloTake = new List<int>();
    readonly List<PodStatusEntry> scratch = new List<PodStatusEntry>();
    bool hadDataBefore;
    bool soloWasRunning;
    float nextRefresh;
    Font font;

    GameObject panelObject;
    TMP_Text panelTitle;
    TMP_Text[] rowNames, rowBatteries, rowSignals;

    public bool HasData { get; private set; }
    public int ConnectedCount { get; private set; }
    public int TotalCount => entries.Count;

    void Awake()
    {
        Instance = this;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
#if DIRECTOR_BUILD
        if (RehearsalRecorder.Instance != null)
            RehearsalRecorder.Instance.RecordingStarted -= OnRecordingStarted;
#endif
    }

    void Update()
    {
        if (Time.time < nextRefresh)
            return;
        nextRefresh = Time.time + RefreshSeconds;

        Refresh();
#if DIRECTOR_BUILD
        UpdateIndicator();
#else
        UpdateSoloReport();
#endif
        if (panelObject != null)
            UpdatePanel();
    }

    void Refresh()
    {
        AvatarPoseSync avatar = AvatarPoseSync.Instance;
        HasData = avatar != null && avatar.podAvatar != null
                  && PodStatus.Decode(avatar.LatestPodStatus, avatar.podAvatar.mapping, scratch);

        entries.Clear();
        ConnectedCount = 0;
        if (!HasData)
        {
            hadDataBefore = false;
            connectedBefore.Clear();
            return;
        }

        List<PodStatusEntry> lost = null;
        foreach (PodStatusEntry source in scratch)
        {
            PodStatusEntry entry = source;
            bool hasBattery = entry.connected && entry.hasHealth && entry.health.HasBattery;
            if (!hasBattery || entry.health.IsCharging || entry.health.batteryMillivolts >= LowRecoverMillivolts)
                lowPods.Remove(entry.podId);
            else if (entry.health.IsLow)
                lowPods.Add(entry.podId);
            entry.low = lowPods.Contains(entry.podId);

            entries.Add(entry);
            if (entry.connected)
                ConnectedCount++;
            else if (hadDataBefore && connectedBefore.Contains(entry.podId))
                (lost ??= new List<PodStatusEntry>()).Add(entry);
        }

        connectedBefore.Clear();
        foreach (PodStatusEntry entry in entries)
        {
            if (entry.connected)
                connectedBefore.Add(entry.podId);
        }
        hadDataBefore = true;

        if (lost != null)
            OnPodsLost(lost);
    }

    void OnPodsLost(List<PodStatusEntry> lost)
    {
        foreach (PodStatusEntry entry in lost)
            Debug.Log($"[Pods] lost {entry.Name}");

#if DIRECTOR_BUILD
        if (Bootstrap.Mode == Bootstrap.AppMode.Remote && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
            FloatingMessage.Show(lost.Count == 1 ? $"Se perdió el pod {lost[0].podId}" : $"Se perdieron {lost.Count} pods");
#else
        if (Bootstrap.Mode == Bootstrap.AppMode.Solo && SoloRehearsalController.Instance != null && SoloRehearsalController.Instance.IsRunning)
        {
            foreach (PodStatusEntry entry in lost)
                lostDuringSoloTake.Add(entry.podId);
        }
#endif
    }

    // The actor's warnings. Null when there's nothing to say.
    public string MissingPods()
    {
        return HasData && ConnectedCount < TotalCount ? ListBodyParts(e => !e.connected) : null;
    }

    public string LowPods()
    {
        return HasData && lowPods.Count > 0 ? ListBodyParts(e => e.low) : null;
    }

    string ListBodyParts(System.Func<PodStatusEntry, bool> include)
    {
        var parts = new List<string>();
        foreach (PodStatusEntry entry in entries)
        {
            if (include(entry))
                parts.Add(PodStatus.BodyPart(entry.bone));
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

#if !DIRECTOR_BUILD
    void UpdateSoloReport()
    {
        bool running = Bootstrap.Mode == Bootstrap.AppMode.Solo && SoloRehearsalController.Instance != null && SoloRehearsalController.Instance.IsRunning;
        if (soloWasRunning && !running && lostDuringSoloTake.Count > 0)
        {
            string text = lostDuringSoloTake.Count == 1 ? $"En la toma se perdió el pod {lostDuringSoloTake[0]}" : $"En la toma se perdieron {lostDuringSoloTake.Count} pods";
            lostDuringSoloTake.Clear();
            StartCoroutine(ShowAfterDelay(text));
        }
        if (!running)
            lostDuringSoloTake.Clear();
        soloWasRunning = running;
    }

    static IEnumerator ShowAfterDelay(string text)
    {
        yield return new WaitForSeconds(SoloReportDelaySeconds);
        Debug.Log($"[Pods] {text}");
        FloatingMessage.Show(text);
    }
#endif

    // --- The director's "Pods 9/9" ---------------------------------------------------------

#if DIRECTOR_BUILD
    GameObject indicatorObject;
    HudChip indicatorChip;

    void Start()
    {
        if (RehearsalRecorder.Instance != null)
            RehearsalRecorder.Instance.RecordingStarted += OnRecordingStarted;
    }

    void OnRecordingStarted()
    {
        Refresh();
        if (HasData && ConnectedCount < TotalCount)
            FloatingMessage.Show($"Grabando con {ConnectedCount} de {TotalCount} pods");
    }

    void UpdateIndicator()
    {
        bool show = HasData && Bootstrap.Mode == Bootstrap.AppMode.Remote && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);
        if (!show)
        {
            if (indicatorObject != null)
                Destroy(indicatorObject);
            indicatorObject = null;
            return;
        }

        if (indicatorObject == null && !BuildIndicator())
            return;

        Color color = ConnectedCount < TotalCount ? ProblemColor : lowPods.Count > 0 ? WarningColor : GlassUI.Accent;
        indicatorChip.SetText($"Pods {ConnectedCount}/{TotalCount}");
        indicatorChip.Icon.color = color;
        indicatorChip.Label.color = ConnectedCount < TotalCount ? ProblemColor : lowPods.Count > 0 ? WarningColor : TextColor;
    }

    // A glass chip under the REC chip, in the upper-left corner of the view.
    bool BuildIndicator()
    {
        Camera head = Camera.main;
        if (head == null)
            return false;

        indicatorChip = GlassUI.HudChip(head.transform, "PodStatusIndicator", IndicatorOffset, GlassUI.Icon.Pods, "Pods", GlassUI.Accent, 0f);
        indicatorObject = indicatorChip.gameObject;
        return true;
    }
#endif

    // --- Estado de pods --------------------------------------------------------------------

    public void ShowPanel()
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        ClosePanel();
        Refresh();

        int rows = Mathf.Max(entries.Count, 9);
        float height = PanelPadding * 2f + TitleHeight + rows * (RowHeight + RowSpacing) + CloseButtonHeight + RowSpacing * 2f;

        panelObject = new GameObject("PodStatusPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image));
        Canvas canvas = panelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;

        Vector3 forward = head.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        var rect = (RectTransform)panelObject.transform;
        rect.sizeDelta = new Vector2(PanelWidth, height);
        rect.SetPositionAndRotation(head.transform.position + forward * PanelDistance, Quaternion.LookRotation(forward, Vector3.up));
        rect.localScale = Vector3.one * PanelWorldScale;

        Image background = panelObject.GetComponent<Image>();
        GlassUI.Glass(background, 34f);
        GlassUI.AddShadow(rect, 30f);
        background.raycastTarget = false;

        float y = -PanelPadding;
        panelTitle = CreateText(rect, "Title", TitleFontSize, FontStyles.Bold, TextColor, TextAlignmentOptions.Left);
        PlaceRow((RectTransform)panelTitle.transform, y, TitleHeight, 0f, 1f);
        y -= TitleHeight;

        rowNames = new TMP_Text[rows];
        rowBatteries = new TMP_Text[rows];
        rowSignals = new TMP_Text[rows];
        for (int i = 0; i < rows; i++)
        {
            rowNames[i] = CreateText(rect, $"Name{i}", RowFontSize, FontStyles.Normal, TextColor, TextAlignmentOptions.Left);
            rowBatteries[i] = CreateText(rect, $"Battery{i}", RowFontSize, FontStyles.Normal, TextColor, TextAlignmentOptions.Left);
            rowSignals[i] = CreateText(rect, $"Signal{i}", RowFontSize, FontStyles.Normal, SubTextColor, TextAlignmentOptions.Left);
            PlaceRow((RectTransform)rowNames[i].transform, y, RowHeight, 0f, 0.48f);
            PlaceRow((RectTransform)rowBatteries[i].transform, y, RowHeight, 0.48f, 0.76f);
            PlaceRow((RectTransform)rowSignals[i].transform, y, RowHeight, 0.76f, 1f);
            y -= RowHeight + RowSpacing;
        }

        BuildCloseButton(rect);

        XRUISetup.MoveToUILayer(panelObject);
        XRUISetup.EnableRayInteraction(panelObject);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.OpenModal(panelObject, "Estado de pods");

        UpdatePanel();
    }

    void ClosePanel()
    {
        PanelFocusManager.CloseModal(panelObject);
        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;
    }

    void UpdatePanel()
    {
        if (!HasData)
            panelTitle.text = "Estado de pods: no hay pods conectados";
        else
        {
            string title = $"Estado de pods: {ConnectedCount}/{TotalCount}";
            int missing = TotalCount - ConnectedCount;
            if (missing > 0)
                title += $" · {missing} sin conexión";
            if (lowPods.Count > 0)
                title += $" · {lowPods.Count} con batería baja";
            panelTitle.text = title;
        }

        for (int i = 0; i < rowNames.Length; i++)
        {
            if (i >= entries.Count)
            {
                rowNames[i].text = rowBatteries[i].text = rowSignals[i].text = "";
                continue;
            }

            PodStatusEntry entry = entries[i];
            rowNames[i].text = entry.Name;
            rowNames[i].color = entry.connected ? TextColor : SubTextColor;

            if (!entry.connected)
            {
                rowBatteries[i].text = "Sin conexión";
                rowBatteries[i].color = ProblemColor;
                rowSignals[i].text = "";
                continue;
            }

            if (!entry.hasHealth || !entry.health.HasBattery)
            {
                rowBatteries[i].text = "—";
                rowBatteries[i].color = SubTextColor;
            }
            else if (entry.health.IsCharging)
            {
                rowBatteries[i].text = "Cargando";
                rowBatteries[i].color = TextColor;
            }
            else
            {
                rowBatteries[i].text = entry.low ? $"{entry.health.BatteryPercent} % · baja" : $"{entry.health.BatteryPercent} %";
                rowBatteries[i].color = entry.low ? WarningColor : TextColor;
            }

            rowSignals[i].text = entry.hasSensor ? PodStatus.SignalLabel(entry.hasHealth ? entry.health.rssi : 0) : "Sin sensor";
            rowSignals[i].color = entry.hasSensor ? SubTextColor : WarningColor;
        }
    }

    void BuildCloseButton(RectTransform parent)
    {
        var buttonObject = new GameObject("Close", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        buttonObject.GetComponent<Button>().onClick.AddListener(ClosePanel);

        var rect = (RectTransform)buttonObject.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, PanelPadding);
        rect.sizeDelta = new Vector2(CloseButtonWidth, CloseButtonHeight);

        TMP_Text label = CreateText(rect, "Label", ButtonFontSize, FontStyles.Bold, TextColor, TextAlignmentOptions.Center);
        label.text = "Cerrar";
        Stretch((RectTransform)label.transform, Vector2.zero, Vector2.one);
        GlassUI.SkinButton(buttonObject, CloseButtonHeight / 2f);
    }

    TMP_Text CreateText(Transform parent, string name, int size, FontStyles style, Color color, TextAlignmentOptions alignment)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(parent, false);
        TMP_Text text = textObject.GetComponent<TextMeshProUGUI>();
        text.font = GlassUI.Regular;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    static void Stretch(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    // A strip across the panel at this height, from one fraction of its inner width to another.
    static void PlaceRow(RectTransform rect, float top, float height, float from, float to)
    {
        float inner = PanelWidth - PanelPadding * 2f;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(PanelPadding + inner * from, top);
        rect.sizeDelta = new Vector2(inner * (to - from), height);
    }
}
