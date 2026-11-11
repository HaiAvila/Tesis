using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// The director's haptic timeline editor: the shot's cues (CueTrack) laid out under the replay
// panel, one row per body zone, the replay's moment as a playhead. Works with the ray (Quest)
// and the mouse (Editor / PC) alike - plain uGUI events on a world-space canvas.
//
//   drag a cue          move it (snapped to 0.1 s)
//   drag a span's end   stretch it (Pasos, Proximidad, Contacto, Sostener)
//   tap a cue           select it; Borrar / Cambiar lado act on the selected one
//   palette             add a cue of that kind at the playhead, manual
//   ruler               tap or drag to move the replay
//   Acción / Corte      fixed
//   Minimizar           a thin strip: the cues and the playhead only (remembered per device)
//
// Every change is saved at once. An automatic cue the director changes becomes manual (white
// outline) and leaves a "removed" copy, so regenerating the take's cues doesn't bring the old
// one back (ShotListDatabase.TakeOverAutoCue). Only the preferida's cues are edited: they're
// the shot's.
public class CueTimelineEditor : MonoBehaviour
{
    const float Width = 1240f;
    const float Height = 432f;
    const float Padding = 24f;
    const float LabelColumn = 120f;
    const float LaneHeight = 46f;
    const float LaneGap = 6f;
    const float LanesTop = 140f;
    const float RulerHeight = 30f;
    // Minimized: thin lanes, no palette, tools or hint.
    const float MiniLaneHeight = 20f;
    const float MiniLaneGap = 4f;
    const float MiniLanesTop = 72f;
    const string MinimizedPref = "CueEditor.Minimized";
    const float InstantWidth = 20f;
    const float LockedWidth = 64f;
    const float EdgeGrab = 12f;
    const float Snap = 0.1f;
    const float MinSpan = 0.2f;
    // Under the replay panel, smaller than the panel's own scale and tilted toward the eyes like
    // a laptop screen, so it doesn't hang far below the view (Nov 10, the user on the Quest: "I
    // have to look down to see it"). The replay goes up a little while it's open
    // (RecordingPlaybackController.EditorLift), keeping the recorded video above it in view.
    const float Scale = 0.5f;
    const float Tilt = 12f;

    const string Dot = ""; // Material Symbols "fiber_manual_record", a filled dot (Inter falls back to the icon font)
    static readonly string[] LaneNames = { "Todos", "Esternón", "Muñecas", "Espinillas" };
    static readonly CueKind[] Palette =
    {
        CueKind.Orientation, CueKind.Timing, CueKind.Checkpoint, CueKind.Anticipation,
        CueKind.Proximity, CueKind.Contact, CueKind.HoldPosition,
    };

    RecordingPlaybackController replay;
    ShotListDatabase database;
    RecordingSession session;
    float duration;
    Action onChanged;

    readonly RectTransform[] lanes = new RectTransform[LaneNames.Length];
    RectTransform timeline;   // the lanes' and ruler's common x range
    RectTransform playhead;
    TMP_Text title;
    TMP_Text hint;
    List<CueEvent> cues = new List<CueEvent>();
    readonly List<GameObject> blocks = new List<GameObject>();
    int selectedId = -1;

    // Called when the editor goes away (Cerrar, the pencil again, the replay closing).
    public Action Closed;

    bool minimized;
    RectTransform rootRect;
    RectTransform rulerRect;
    readonly TMP_Text[] laneLabels = new TMP_Text[LaneNames.Length];
    readonly List<GameObject> fullOnly = new List<GameObject>();
    TMP_Text minimizeLabel;

    float LaneH => minimized ? MiniLaneHeight : LaneHeight;
    float Gap => minimized ? MiniLaneGap : LaneGap;
    float Top => minimized ? MiniLanesTop : LanesTop;
    float BlockInset => minimized ? 2f : 4f;
    float LanesSpan => LaneNames.Length * (LaneH + Gap);
    float PanelHeight => minimized ? Top + LanesSpan + RulerHeight + 14f : Height;

    float TimelineWidth => Width - Padding * 2f - LabelColumn;

    public static CueTimelineEditor Create(RectTransform replayPanel, float replayPanelHeight, RecordingPlaybackController replay, RecordingSession session, float duration, Action onChanged)
    {
        var root = new GameObject("CueTimelineEditor", typeof(RectTransform), typeof(Image), typeof(CueTimelineEditor));
        root.transform.SetParent(replayPanel, false);
        var rect = (RectTransform)root.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(Width, Height);
        rect.anchoredPosition = new Vector2(0f, -replayPanelHeight / 2f - 16f);
        rect.localScale = Vector3.one * Scale;
        rect.localRotation = Quaternion.Euler(Tilt, 0f, 0f);

        Image background = root.GetComponent<Image>();
        GlassUI.Glass(background, 30f);
        background.raycastTarget = false;
        GlassUI.AddShadow(rect, 26f);

        CueTimelineEditor editor = root.GetComponent<CueTimelineEditor>();
        editor.replay = replay;
        editor.session = session;
        editor.duration = Mathf.Max(duration, 0.5f);
        editor.onChanged = onChanged;
        editor.database = FindFirstObjectByType<ShotListDatabase>();
        editor.rootRect = rect;
        editor.minimized = PlayerPrefs.GetInt(MinimizedPref, 0) == 1;
        editor.Build(rect);
        editor.ApplyLayout();
        editor.Reload();

        XRUISetup.MoveToUILayer(root);
        return editor;
    }

    // --- layout ----------------------------------------------------------------------------------

    void Build(RectTransform root)
    {
        title = Text(root, $"Pistas hápticas · Shot {session.ShotNumber}", 26f, true, new Rect(Padding, 18f, 480f, 40f), TextAlignmentOptions.MidlineLeft);

        Pill(root, GlassUI.Icon.Close, "Cerrar", new Rect(Width - Padding - 140f, 16f, 140f, 44f), Close, GlassUI.ButtonStyle.Normal);
        minimizeLabel = Pill(root, null, "", new Rect(Width - Padding - 300f, 16f, 150f, 44f), ToggleMinimized, GlassUI.ButtonStyle.Normal)
            .GetComponentInChildren<TMP_Text>();
        fullOnly.Add(Pill(root, null, "Borrar", new Rect(Width - Padding - 460f, 16f, 140f, 44f), DeleteSelected, GlassUI.ButtonStyle.Danger));
        fullOnly.Add(Pill(root, null, "Cambiar lado", new Rect(Width - Padding - 670f, 16f, 200f, 44f), SwapSide, GlassUI.ButtonStyle.Normal));

        fullOnly.Add(Text(root, "Añadir:", 20f, false, new Rect(Padding, 80f, 90f, 40f), TextAlignmentOptions.MidlineLeft, GlassUI.SubTextColor).gameObject);
        float x = Padding + 96f;
        foreach (CueKind kind in Palette)
        {
            CueKind captured = kind;
            string name = HapticCues.Name(kind);
            float w = 30f + name.Length * 11f;
            fullOnly.Add(Pill(root, null, $"<color=#{ColorUtility.ToHtmlStringRGB(HapticCues.Color(kind))}>{Dot}</color> {name}", new Rect(x, 78f, w, 42f), () => Add(captured), GlassUI.ButtonStyle.Row));
            x += w + 8f;
        }

        // The timeline: lanes, ruler, playhead share one x range. Their sizes: ApplyLayout.
        var area = new GameObject("Timeline", typeof(RectTransform));
        area.transform.SetParent(root, false);
        timeline = (RectTransform)area.transform;

        for (int i = 0; i < LaneNames.Length; i++)
        {
            laneLabels[i] = Text(root, LaneNames[i], 20f, false, new Rect(Padding, 0f, LabelColumn - 10f, LaneHeight), TextAlignmentOptions.MidlineLeft, GlassUI.SubTextColor);

            var lane = new GameObject($"Lane_{LaneNames[i]}", typeof(RectTransform), typeof(Image), typeof(LaneSurface));
            lane.transform.SetParent(timeline, false);
            lanes[i] = (RectTransform)lane.transform;
            GlassUI.Rounded(lane.GetComponent<Image>(), 10f, new Color(1f, 1f, 1f, 0.05f));
            lane.GetComponent<LaneSurface>().Editor = this;
        }

        var ruler = new GameObject("Ruler", typeof(RectTransform), typeof(Image), typeof(RulerSurface));
        ruler.transform.SetParent(timeline, false);
        rulerRect = (RectTransform)ruler.transform;
        ruler.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);
        ruler.GetComponent<RulerSurface>().Editor = this;
        for (int s = 0; s <= Mathf.FloorToInt(duration); s++)
        {
            bool major = s % 5 == 0;
            var tick = new GameObject("Tick", typeof(RectTransform), typeof(Image));
            tick.transform.SetParent(rulerRect, false);
            Place((RectTransform)tick.transform, new Rect(TimeToX(s) - 1f, 0f, 2f, major ? 12f : 6f));
            Image tickImage = tick.GetComponent<Image>();
            tickImage.color = new Color(1f, 1f, 1f, major ? 0.5f : 0.25f);
            tickImage.raycastTarget = false;
            if (major)
                Text(rulerRect, $"{s}s", 15f, false, new Rect(TimeToX(s) - 30f, 12f, 60f, 18f), TextAlignmentOptions.Center, GlassUI.SubTextColor);
        }

        var head = new GameObject("Playhead", typeof(RectTransform), typeof(Image));
        head.transform.SetParent(timeline, false);
        playhead = (RectTransform)head.transform;
        Image headImage = head.GetComponent<Image>();
        headImage.color = GlassUI.Accent;
        headImage.raycastTarget = false;

        hint = Text(root, "Arrastra para mover · los bordes de una barra la estiran · se ajusta a 0.1 s · contorno blanco = editada a mano",
            16f, false, new Rect(Padding, Height - 38f, Width - Padding * 2f, 26f), TextAlignmentOptions.MidlineLeft, GlassUI.SubTextColor);
        fullOnly.Add(hint.gameObject);
    }

    // Full or minimized: what shows, the lanes' heights, the panel's height (its top stays put).
    void ApplyLayout()
    {
        foreach (GameObject part in fullOnly)
            part.SetActive(!minimized);
        minimizeLabel.text = (minimized ? GlassUI.Icon.Expand : GlassUI.Icon.Collapse) + "  " + (minimized ? "Expandir" : "Minimizar");

        float lanesSpan = LanesSpan;
        Place(timeline, new Rect(Padding + LabelColumn, Top, TimelineWidth, lanesSpan + RulerHeight));
        for (int i = 0; i < LaneNames.Length; i++)
        {
            float y = i * (LaneH + Gap);
            Place(lanes[i], new Rect(0f, y, TimelineWidth, LaneH));
            Place((RectTransform)laneLabels[i].transform, new Rect(Padding, Top + y, LabelColumn - 10f, LaneH));
            laneLabels[i].fontSize = minimized ? 15f : 20f;
        }
        Place(rulerRect, new Rect(0f, lanesSpan, TimelineWidth, RulerHeight));
        Place(playhead, new Rect(0f, 0f, 3f, lanesSpan + 12f));
        rootRect.sizeDelta = new Vector2(Width, PanelHeight);
        Rebuild();
    }

    void ToggleMinimized()
    {
        minimized = !minimized;
        PlayerPrefs.SetInt(MinimizedPref, minimized ? 1 : 0);
        ApplyLayout();
    }

    void OnDestroy() => Closed?.Invoke();

    void Update()
    {
        if (replay != null && playhead != null)
            playhead.anchoredPosition = new Vector2(TimeToX(Mathf.Clamp(replay.PlaybackTime, 0f, duration)) - 1.5f, playhead.anchoredPosition.y);
    }

    // --- cues ------------------------------------------------------------------------------------

    void Reload()
    {
        cues = database != null ? database.GetCueTrack(session.ShotNumber) : new List<CueEvent>();
        Rebuild();
        onChanged?.Invoke();
    }

    void Rebuild()
    {
        foreach (GameObject block in blocks)
            if (block != null) Destroy(block);
        blocks.Clear();

        // Spans first, single cues over them.
        foreach (bool spans in new[] { true, false })
            foreach (CueEvent cue in cues)
                if ((cue.Dur > 0f) == spans)
                    blocks.Add(BuildBlock(cue));
        playhead.SetAsLastSibling();
    }

    GameObject BuildBlock(CueEvent cue)
    {
        RectTransform lane = lanes[LaneOf(cue.Kind)];
        var block = new GameObject($"Cue_{cue.Kind}", typeof(RectTransform), typeof(Image), typeof(Outline), typeof(CueBlock));
        block.transform.SetParent(lane, false);
        var rect = (RectTransform)block.transform;
        PlaceBlock(rect, cue);

        bool selected = cue.Id == selectedId;
        float blockHeight = LaneH - BlockInset * 2f;
        Color color = HapticCues.Color(cue.Kind);
        GlassUI.Rounded(block.GetComponent<Image>(), 7f, new Color(color.r, color.g, color.b, selected ? 1f : 0.85f));
        Outline outline = block.GetComponent<Outline>();
        bool manual = cue.Source == CueEvent.SourceManual;
        outline.enabled = manual || selected;
        outline.effectColor = selected ? GlassUI.Accent : Color.white;
        outline.effectDistance = selected ? new Vector2(3f, -3f) : new Vector2(2f, -2f);

        // Minimized: colors only, the strip is too thin for names.
        if (!minimized)
        {
            string side = SideLetter(cue.Pod);
            string label = cue.Dur > 0f && rect.sizeDelta.x > 110f ? HapticCues.Name(cue.Kind) + (side.Length > 0 ? " · " + side : "") : Short(cue.Kind) + side;
            TMP_Text text = Text(rect, label, Locked(cue.Kind) ? 14f : 15f, true, new Rect(0f, 0f, Mathf.Max(rect.sizeDelta.x, InstantWidth), blockHeight), TextAlignmentOptions.Center, new Color(0.05f, 0.06f, 0.08f, 1f));
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
        }

        CueBlock handle = block.GetComponent<CueBlock>();
        handle.Editor = this;
        handle.Cue = cue;
        return block;
    }

    void PlaceBlock(RectTransform rect, CueEvent cue)
    {
        // Acción and Corte: wide enough for their names, inside the timeline's ends.
        if (Locked(cue.Kind))
        {
            float at = cue.Kind == CueKind.CameraStart ? 0f : Mathf.Min(TimeToX(cue.T), TimelineWidth) - LockedWidth;
            Place(rect, new Rect(Mathf.Max(0f, at), BlockInset, LockedWidth, LaneH - BlockInset * 2f));
            return;
        }

        float x = TimeToX(cue.T);
        float w = cue.Dur > 0f ? Mathf.Max(cue.Dur / duration * TimelineWidth, InstantWidth) : InstantWidth;
        if (cue.Dur <= 0f)
            x -= InstantWidth / 2f;
        Place(rect, new Rect(x, BlockInset, w, LaneH - BlockInset * 2f));
    }

    static int LaneOf(CueKind kind)
    {
        switch (kind)
        {
            case CueKind.CameraStart:
            case CueKind.Cut: return 0;
            case CueKind.Checkpoint:
            case CueKind.Anticipation:
            case CueKind.MarkConfirmation: return 1;
            case CueKind.Timing: return 3;
            default: return 2;
        }
    }

    static bool Locked(CueKind kind) => kind == CueKind.CameraStart || kind == CueKind.Cut;

    static string Short(CueKind kind)
    {
        switch (kind)
        {
            case CueKind.Orientation: return "G";
            case CueKind.Timing: return "P";
            case CueKind.Checkpoint: return "M";
            case CueKind.Anticipation: return "A";
            case CueKind.Proximity: return "Px";
            case CueKind.Contact: return "C";
            case CueKind.HoldPosition: return "S";
            case CueKind.CameraStart: return "Acción";
            case CueKind.Cut: return "Corte";
            default: return "?";
        }
    }

    static string SideLetter(int pod)
    {
        switch (pod)
        {
            case PodIds.LeftWrist: case PodIds.LeftBicep: case PodIds.LeftQuad: case PodIds.LeftShin: return "I";
            case PodIds.RightWrist: case PodIds.RightBicep: case PodIds.RightQuad: case PodIds.RightShin: return "D";
            default: return "";
        }
    }

    static int OtherSide(int pod)
    {
        switch (pod)
        {
            case PodIds.LeftWrist: return PodIds.RightWrist;
            case PodIds.RightWrist: return PodIds.LeftWrist;
            case PodIds.LeftBicep: return PodIds.RightBicep;
            case PodIds.RightBicep: return PodIds.LeftBicep;
            case PodIds.LeftQuad: return PodIds.RightQuad;
            case PodIds.RightQuad: return PodIds.LeftQuad;
            case PodIds.LeftShin: return PodIds.RightShin;
            case PodIds.RightShin: return PodIds.LeftShin;
            default: return pod;
        }
    }

    // --- editing ---------------------------------------------------------------------------------

    float TimeToX(float t) => t / duration * TimelineWidth;
    float XToTime(float x) => x / TimelineWidth * duration;
    static float SnapTime(float t) => Mathf.Round(t / Snap) * Snap;

    // The pointer's x on the timeline (0 = its left edge), from where the ray or mouse hits.
    public bool TryTimelineX(PointerEventData e, out float x)
    {
        if (e.pointerCurrentRaycast.isValid)
        {
            Vector3 local = timeline.InverseTransformPoint(e.pointerCurrentRaycast.worldPosition);
            x = local.x - timeline.rect.xMin;
            return true;
        }
        Camera camera = e.pressEventCamera != null ? e.pressEventCamera : e.enterEventCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(timeline, e.position, camera, out Vector2 point))
        {
            x = point.x - timeline.rect.xMin;
            return true;
        }
        x = 0f;
        return false;
    }

    public void Select(CueEvent cue)
    {
        if (cue != null && Locked(cue.Kind))
        {
            FloatingMessage.Show("Acción y Corte son fijas");
            return;
        }
        selectedId = cue != null ? cue.Id : -1;
        Rebuild();
    }

    public enum DragMode { Move, StretchStart, StretchEnd }

    public DragMode DragModeAt(CueEvent cue, float timelineX)
    {
        if (cue.Dur <= 0f)
            return DragMode.Move;
        float start = TimeToX(cue.T), end = TimeToX(cue.T + cue.Dur);
        if (timelineX - start < EdgeGrab) return DragMode.StretchStart;
        if (end - timelineX < EdgeGrab) return DragMode.StretchEnd;
        return DragMode.Move;
    }

    // While dragging: the block follows, nothing is saved yet.
    public void Preview(RectTransform block, CueEvent cue, DragMode mode, float startT, float startDur, float deltaX)
    {
        float delta = XToTime(deltaX);
        switch (mode)
        {
            case DragMode.Move:
                cue.T = Mathf.Clamp(SnapTime(startT + delta), 0.05f, Mathf.Max(0.05f, duration - Mathf.Max(cue.Dur, 0.05f)));
                break;
            case DragMode.StretchStart:
                float end = startT + startDur;
                cue.T = Mathf.Clamp(SnapTime(startT + delta), 0f, end - MinSpan);
                cue.Dur = end - cue.T;
                break;
            case DragMode.StretchEnd:
                cue.Dur = Mathf.Clamp(SnapTime(startDur + delta), MinSpan, duration - cue.T);
                break;
        }
        PlaceBlock(block, cue);
        hint.text = cue.Dur > 0f ? $"{HapticCues.Name(cue.Kind)}: {cue.T:F1} s → {cue.T + cue.Dur:F1} s" : $"{HapticCues.Name(cue.Kind)}: {cue.T:F1} s";
    }

    public void Commit(CueEvent cue)
    {
        if (database == null)
            return;
        database.TakeOverAutoCue(cue);
        database.UpdateCue(cue);
        selectedId = cue.Id;
        Debug.Log($"[CueEditor] shot {session.ShotNumber}: {cue.Kind} pod {cue.Pod} at {cue.T:F1} s" + (cue.Dur > 0f ? $" for {cue.Dur:F1} s" : ""));
        Reload();
    }

    CueEvent Selected => cues.Find(c => c.Id == selectedId);

    void DeleteSelected()
    {
        CueEvent cue = Selected;
        if (cue == null || database == null)
        {
            FloatingMessage.Show("Toca una pista para seleccionarla");
            return;
        }
        database.DeleteCue(cue);
        selectedId = -1;
        Debug.Log($"[CueEditor] shot {session.ShotNumber}: deleted {cue.Kind} at {cue.T:F1} s");
        Reload();
    }

    void SwapSide()
    {
        CueEvent cue = Selected;
        if (cue == null)
        {
            FloatingMessage.Show("Toca una pista para seleccionarla");
            return;
        }
        int other = OtherSide(cue.Pod);
        if (other == cue.Pod)
        {
            FloatingMessage.Show("Esta pista no tiene lado");
            return;
        }
        cue.Pod = other;
        if (cue.Kind == CueKind.Orientation)
            cue.Param = -cue.Param;
        Commit(cue);
    }

    void Add(CueKind kind)
    {
        if (database == null)
            return;
        float t = Mathf.Clamp(SnapTime(replay != null ? replay.PlaybackTime : 0f), 0.1f, duration - 0.1f);
        var cue = new CueEvent
        {
            SceneNumber = session.SceneNumber,
            ShotNumber = session.ShotNumber,
            SessionGuid = session.SessionGuid,
            T = t,
            Cue = (int)kind,
            Source = CueEvent.SourceManual,
        };

        switch (kind)
        {
            case CueKind.Orientation:
                cue.Pod = PodIds.RightWrist;
                cue.Param = 90f;
                break;
            case CueKind.Timing:
                cue.Pod = OtherSide(LastStepPodBefore(t));
                break;
            case CueKind.Checkpoint:
            case CueKind.Anticipation:
                cue.Pod = PodIds.Sternum;
                break;
            default: // the hand: on the wrist the shot's hand cues already use
                cue.Pod = HandPod();
                cue.Dur = Mathf.Min(kind == CueKind.Contact ? 2f : 1.5f, duration - t);
                break;
        }

        database.InsertCue(cue);
        selectedId = cue.Id;
        Debug.Log($"[CueEditor] shot {session.ShotNumber}: added {kind} at {t:F1} s");
        Reload();
    }

    int LastStepPodBefore(float t)
    {
        CueEvent last = null;
        foreach (CueEvent c in cues)
            if (c.Kind == CueKind.Timing && c.Dur <= 0f && c.T < t && (last == null || c.T > last.T))
                last = c;
        return last != null ? last.Pod : PodIds.RightShin; // so the first one goes on the left
    }

    int HandPod()
    {
        foreach (CueEvent c in cues)
            if (c.Kind == CueKind.Proximity || c.Kind == CueKind.Contact || c.Kind == CueKind.HoldPosition)
                return c.Pod;
        return PodIds.RightWrist;
    }

    public void Seek(float timelineX)
    {
#if DIRECTOR_BUILD
        if (replay != null)
            replay.JumpTo(Mathf.Clamp(XToTime(timelineX), 0f, duration));
#endif
    }

    void Close() => Destroy(gameObject);

    // --- the question before a new preferida replaces the shot's cues ---------------------------

    static GameObject askPanel;

    // The shot has the director's edited cues: keep them next to the new preferida's automatic
    // ones (true), or start the shot's cues over from the new take (false).
    public static void AskKeepEdits(Action<bool> answer)
    {
        Camera head = Camera.main;
        if (head == null)
        {
            answer(true);
            return;
        }
        if (askPanel != null)
        {
            PanelFocusManager.CloseModal(askPanel);
            Destroy(askPanel);
        }

        Vector3 forward = head.transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        askPanel = new GameObject("KeepEditsPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image));
        Canvas canvas = askPanel.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;
        var rect = (RectTransform)askPanel.transform;
        rect.sizeDelta = new Vector2(860f, 280f);
        rect.SetPositionAndRotation(head.transform.position + forward * 1.1f, Quaternion.LookRotation(forward, Vector3.up));
        rect.localScale = Vector3.one * 0.0012f;
        Image background = askPanel.GetComponent<Image>();
        GlassUI.Glass(background, 34f);
        background.raycastTarget = false;
        GlassUI.AddShadow(rect, 30f);

        Text(rect, "Este shot tiene pistas hápticas editadas a mano. ¿Las conservas con la nueva toma preferida?", 26f, false,
            new Rect(36f, 26f, 788f, 110f), TextAlignmentOptions.Center);
        void Finish(bool keep)
        {
            PanelFocusManager.CloseModal(askPanel);
            Destroy(askPanel);
            askPanel = null;
            answer(keep);
        }
        Pill(rect, null, "Empezar de cero", new Rect(36f, 170f, 370f, 72f), () => Finish(false), GlassUI.ButtonStyle.Danger);
        Pill(rect, null, "Conservarlas", new Rect(454f, 170f, 370f, 72f), () => Finish(true), GlassUI.ButtonStyle.Primary);

        XRUISetup.MoveToUILayer(askPanel);
        XRUISetup.EnableRayInteraction(askPanel);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.OpenModal(askPanel, "Conservar pistas editadas");
    }

    // --- small builders (top-left coordinates, y down) -------------------------------------------

    static void Place(RectTransform rect, Rect r)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(r.x, -r.y);
        rect.sizeDelta = new Vector2(r.width, r.height);
    }

    static TMP_Text Text(RectTransform parent, string text, float size, bool semiBold, Rect r, TextAlignmentOptions alignment, Color? color = null)
    {
        TextMeshProUGUI label = GlassUI.Label(parent, text, size, semiBold, color, alignment);
        Place((RectTransform)label.transform, r);
        return label;
    }

    static GameObject Pill(RectTransform parent, string glyph, string text, Rect r, Action onClick, GlassUI.ButtonStyle style)
    {
        var buttonObject = new GameObject($"Button_{text}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        Place((RectTransform)buttonObject.transform, r);
        GlassUI.SkinButton(buttonObject, r.height / 2f, style);
        buttonObject.GetComponent<Button>().onClick.AddListener(() => onClick());
        TextMeshProUGUI tmp = GlassUI.Label(buttonObject.transform, glyph != null ? glyph + "  " + text : text, 19f, true,
            style == GlassUI.ButtonStyle.Danger ? GlassUI.DangerText : (Color?)null);
        GlassUI.Stretch((RectTransform)tmp.transform);
        tmp.enableWordWrapping = false;
        return buttonObject;
    }
}

// A cue on the timeline: tap to select, drag to move or stretch.
public class CueBlock : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public CueTimelineEditor Editor;
    public CueEvent Cue;

    CueTimelineEditor.DragMode mode;
    float startX, startT, startDur;
    bool dragging;

    public void OnPointerClick(PointerEventData e)
    {
        if (!dragging)
            Editor.Select(Cue);
    }

    public void OnBeginDrag(PointerEventData e)
    {
        if (Cue.Kind == CueKind.CameraStart || Cue.Kind == CueKind.Cut || !Editor.TryTimelineX(e, out startX))
            return;
        dragging = true;
        mode = Editor.DragModeAt(Cue, startX);
        startT = Cue.T;
        startDur = Cue.Dur;
    }

    public void OnDrag(PointerEventData e)
    {
        if (dragging && Editor.TryTimelineX(e, out float x))
            Editor.Preview((RectTransform)transform, Cue, mode, startT, startDur, x - startX);
    }

    public void OnEndDrag(PointerEventData e)
    {
        if (!dragging)
            return;
        dragging = false;
        if (!Mathf.Approximately(Cue.T, startT) || !Mathf.Approximately(Cue.Dur, startDur))
            Editor.Commit(Cue);
    }
}

// A lane's empty space: tapping it clears the selection.
public class LaneSurface : MonoBehaviour, IPointerClickHandler
{
    public CueTimelineEditor Editor;
    public void OnPointerClick(PointerEventData e) => Editor.Select(null);
}

// The ruler under the lanes: tap or drag to move the replay there.
public class RulerSurface : MonoBehaviour, IPointerDownHandler, IDragHandler
{
    public CueTimelineEditor Editor;
    public void OnPointerDown(PointerEventData e) { if (Editor.TryTimelineX(e, out float x)) Editor.Seek(x); }
    public void OnDrag(PointerEventData e) { if (Editor.TryTimelineX(e, out float x)) Editor.Seek(x); }
}
