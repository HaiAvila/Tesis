using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Persistent (added to the Bootstrap object). The director's notes for a take, opened from the
// replay panel: the take's own, by the moment they were said, then the shot's general ones. Each
// can be played, jumped to in the replay, or deleted. A menu in PanelFocusManager's terms, so the
// replay pauses while it's open. Also holds the "are you sure?" panel the deletes share.
public class VoiceNotesPanel : MonoBehaviour
{
    public static VoiceNotesPanel Instance { get; private set; }

#if DIRECTOR_BUILD
    const int RowsPerPage = 5;
    const float PanelDistance = 1.4f;
    const float PanelWorldScale = 0.0016f;
    const float PanelWidth = 900f;
    const float Padding = 40f;
    const float RowHeight = 70f;
    const float RowSpacing = 10f;
    const float TitleHeight = 60f;
    const float FooterHeight = 74f;
    const int TitleFontSize = 34;
    const int RowFontSize = 30;
    const int ButtonFontSize = 28;

    static readonly Color BackgroundColor = new Color(0.1f, 0.1f, 0.14f, 0.95f);
    static readonly Color ButtonColor = new Color(0.28f, 0.28f, 0.34f, 0.95f);
    static readonly Color PlayingColor = new Color(0.2f, 0.45f, 0.75f, 0.95f);
    static readonly Color DangerColor = new Color(0.6f, 0.16f, 0.16f, 0.95f);
    static readonly Color TextColor = new Color(0.95f, 0.95f, 0.98f, 1f);

    GameObject panelObject;
    GameObject confirmObject;
    Font font;
    AudioSource audioSource;
    readonly List<VoiceNote> notes = new List<VoiceNote>();
    int page;
    int playingId = -1;
    Action<float> onJump;
    Action onClosed;
    string takeGuid;
    int scene, shot;

    void Awake()
    {
        Instance = this;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        // A note that played to its end.
        if (playingId >= 0 && !audioSource.isPlaying && panelObject != null)
        {
            playingId = -1;
            Rebuild();
        }
    }

    public void Show(string guid, int sceneNumber, int shotNumber, Action<float> jumpTo, Action closed)
    {
        takeGuid = guid;
        scene = sceneNumber;
        shot = shotNumber;
        onJump = jumpTo;
        onClosed = closed;
        page = 0;
        Reload();
        Rebuild();
    }

    void Reload()
    {
        notes.Clear();
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database != null)
            notes.AddRange(database.GetVoiceNotes(takeGuid, scene, shot));
    }

    public void Close()
    {
        StopAudio();
        DestroyPanel();
        Action closed = onClosed;
        onClosed = null;
        closed?.Invoke();
    }

    void DestroyPanel()
    {
        PanelFocusManager.CloseModal(panelObject);
        if (panelObject != null)
            Destroy(panelObject);
        panelObject = null;
    }

    void StopAudio()
    {
        audioSource.Stop();
        if (audioSource.clip != null)
            Destroy(audioSource.clip);
        audioSource.clip = null;
        playingId = -1;
    }

    void Rebuild()
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        Vector3 position;
        Quaternion rotation;
        if (panelObject != null)
        {
            position = panelObject.transform.position;
            rotation = panelObject.transform.rotation;
        }
        else
        {
            PlaceInFront(head.transform, out position, out rotation);
        }
        DestroyPanel();

        float height = Padding * 2f + TitleHeight + RowsPerPage * (RowHeight + RowSpacing) + FooterHeight;
        RectTransform rect = BuildCanvas("VoiceNotesPanel", new Vector2(PanelWidth, height), position, rotation, head, out panelObject);

        Label(rect, $"Notas de voz ({notes.Count})", TitleFontSize, TextAlignmentOptions.Left, new Rect(Padding, Padding, PanelWidth - Padding * 2f, TitleHeight));

        float y = Padding + TitleHeight;
        if (notes.Count == 0)
            Label(rect, "Todavía no hay notas. Graba una con el botón Nota de voz o con el clic del joystick izquierdo.", RowFontSize, TextAlignmentOptions.TopLeft, new Rect(Padding, y, PanelWidth - Padding * 2f, RowHeight * 2f), wrap: true);

        int first = page * RowsPerPage;
        for (int i = first; i < Mathf.Min(first + RowsPerPage, notes.Count); i++)
        {
            BuildRow(rect, notes[i], y);
            y += RowHeight + RowSpacing;
        }

        float footerY = height - Padding - FooterHeight + 10f;
        const float footerButton = 110f;
        if (notes.Count > RowsPerPage)
        {
            int pages = Mathf.CeilToInt(notes.Count / (float)RowsPerPage);
            Button(rect, "<", ButtonColor, new Rect(Padding, footerY, footerButton, FooterHeight - 10f), () => { page = Mathf.Max(0, page - 1); Rebuild(); });
            Label(rect, $"{page + 1} / {pages}", RowFontSize, TextAlignmentOptions.Center, new Rect(Padding + footerButton, footerY, footerButton, FooterHeight - 10f));
            Button(rect, ">", ButtonColor, new Rect(Padding + footerButton * 2f, footerY, footerButton, FooterHeight - 10f), () => { page = Mathf.Min(pages - 1, page + 1); Rebuild(); });
        }
        Button(rect, "Cerrar", ButtonColor, new Rect(PanelWidth - Padding - 220f, footerY, 220f, FooterHeight - 10f), Close);

        FinishPanel(panelObject, "Notas de voz");
    }

    void BuildRow(RectTransform parent, VoiceNote note, float y)
    {
        float x = Padding;
        bool playing = note.Id == playingId;

        GameObject play = Button(parent, "", playing ? PlayingColor : ButtonColor, new Rect(x, y, 80f, RowHeight), () => TogglePlay(note));
        TMP_Text playIcon = GlassUI.IconLabel(play.transform, playing ? GlassUI.Icon.Pause : GlassUI.Icon.Play, RowHeight * 0.5f);
        GlassUI.Stretch((RectTransform)playIcon.transform);
        x += 96f;

        string when = note.TakeTime.HasValue ? $"En {Clock(note.TakeTime.Value)}" : note.SessionGuid == null ? "Del shot" : "De la toma";
        Label(parent, $"{when}  ·  {Mathf.Max(1, Mathf.RoundToInt(note.Duracion))} s", RowFontSize, TextAlignmentOptions.Left, new Rect(x, y, 400f, RowHeight));

        float right = PanelWidth - Padding;
        Button(parent, "Borrar", DangerColor, new Rect(right - 130f, y, 130f, RowHeight), () => Confirm("¿Borrar esta nota?", () =>
        {
            FindFirstObjectByType<ShotListDatabase>()?.DeleteVoiceNote(note);
            if (note.Id == playingId)
                StopAudio();
            Reload();
            page = Mathf.Clamp(page, 0, Mathf.Max(0, Mathf.CeilToInt(notes.Count / (float)RowsPerPage) - 1));
            Rebuild();
            if (RecordingPlaybackController.Instance != null)
                RecordingPlaybackController.Instance.RefreshNoteTicks();
        }));

        // Only a note said at a moment of this take has somewhere to jump to.
        if (note.TakeTime.HasValue && note.SessionGuid == takeGuid && onJump != null)
            Button(parent, "Ir", ButtonColor, new Rect(right - 240f, y, 96f, RowHeight), () =>
            {
                float time = note.TakeTime.Value;
                Action<float> jump = onJump;
                Close();
                jump(time);
            });
    }

    static string Clock(float seconds)
    {
        int total = Mathf.FloorToInt(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    void TogglePlay(VoiceNote note)
    {
        bool wasPlaying = note.Id == playingId;
        StopAudio();
        if (!wasPlaying)
        {
            AudioClip clip = WavFile.Load(Path.Combine(VoiceNoteRecorder.NotesFolder, note.Archivo));
            if (clip == null)
            {
                FloatingMessage.Show("No se encontró el audio de la nota");
            }
            else
            {
                audioSource.clip = clip;
                audioSource.Play();
                playingId = note.Id;
            }
        }
        Rebuild();
    }

    // --- "are you sure?" ---------------------------------------------------------------------

    public void Confirm(string question, Action yes)
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        CloseConfirm();
        PlaceInFront(head.transform, out Vector3 position, out Quaternion rotation);
        position -= rotation * Vector3.forward * 0.1f; // in front of the panel it's asking about
        RectTransform rect = BuildCanvas("ConfirmPanel", new Vector2(760f, 260f), position, rotation, head, out confirmObject);

        Label(rect, question, TitleFontSize, TextAlignmentOptions.Center, new Rect(Padding, Padding - 10f, 760f - Padding * 2f, 90f), wrap: true);
        Button(rect, "Cancelar", ButtonColor, new Rect(Padding, 150f, 320f, 76f), CloseConfirm);
        Button(rect, "Borrar", DangerColor, new Rect(760f - Padding - 320f, 150f, 320f, 76f), () =>
        {
            CloseConfirm();
            yes();
        });

        FinishPanel(confirmObject, "Confirmación");
    }

    void CloseConfirm()
    {
        PanelFocusManager.CloseModal(confirmObject);
        if (confirmObject != null)
            Destroy(confirmObject);
        confirmObject = null;
    }

    // --- building ------------------------------------------------------------------------------

    static void PlaceInFront(Transform head, out Vector3 position, out Quaternion rotation)
    {
        Vector3 forward = head.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        position = head.position + forward * PanelDistance;
        rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    static RectTransform BuildCanvas(string name, Vector2 size, Vector3 position, Quaternion rotation, Camera head, out GameObject root)
    {
        root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;

        var rect = (RectTransform)root.transform;
        rect.sizeDelta = size;
        rect.SetPositionAndRotation(position, rotation);
        rect.localScale = Vector3.one * PanelWorldScale;

        Image background = root.GetComponent<Image>();
        GlassUI.Glass(background, 34f);
        GlassUI.AddShadow(rect, 30f);
        background.raycastTarget = false;
        return rect;
    }

    static void FinishPanel(GameObject root, string name)
    {
        XRUISetup.MoveToUILayer(root);
        XRUISetup.EnableRayInteraction(root);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.OpenModal(root, name);
    }

    // area: x and y from the panel's top-left corner.
    static void Place(RectTransform rect, Rect area)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(area.x, -area.y);
        rect.sizeDelta = new Vector2(area.width, area.height);
    }

    void Label(RectTransform parent, string text, int size, TextAlignmentOptions alignment, Rect area, bool wrap = false)
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(parent, false);
        Place((RectTransform)labelObject.transform, area);
        SetupText(labelObject.GetComponent<TextMeshProUGUI>(), text, size, alignment, wrap);
    }

    GameObject Button(RectTransform parent, string text, Color color, Rect area, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject($"Button_{text}", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        Place((RectTransform)buttonObject.transform, area);
        buttonObject.GetComponent<Button>().onClick.AddListener(onClick);

        if (text.Length > 0)
        {
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            textObject.transform.SetParent(buttonObject.transform, false);
            var textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            SetupText(textObject.GetComponent<TextMeshProUGUI>(), text, ButtonFontSize, TextAlignmentOptions.Center, false);
        }
        GlassUI.SkinButton(buttonObject, area.height / 2f,
            color == DangerColor ? GlassUI.ButtonStyle.Danger : color == PlayingColor ? GlassUI.ButtonStyle.Primary : GlassUI.ButtonStyle.Normal);
        return buttonObject;
    }

    void SetupText(TMP_Text label, string text, int size, TextAlignmentOptions alignment, bool wrap)
    {
        label.font = GlassUI.Regular;
        label.text = text;
        label.fontSize = size;
        label.color = TextColor;
        label.alignment = alignment;
        label.enableWordWrapping = wrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
    }
#endif
}
