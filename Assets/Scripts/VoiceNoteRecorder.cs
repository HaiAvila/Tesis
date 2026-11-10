using System.Collections.Generic;
using System.IO;
using Normal.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Persistent (added to the Bootstrap object). Director only: spoken notes, instead of typing on a
// headset. A press starts one and another press ends it, from either the left joystick's click
// or the corner button, which also shows the time while recording. The actor doesn't hear it:
// the voice chat goes silent for the duration (VoiceChat.NoteRecording).
//
// Where a note belongs, decided when it starts (see StartNote):
// - a take being recorded, or just recorded and not saved yet: that take, at this moment of it;
// - a replay: that take, at the replay's current moment;
// - otherwise: the shot in general.
// Discarding a take keeps its notes, as notes of the shot.
//
// The microphone through MicrophoneReader (16 kHz mono: plenty for speech, under 4 MB for two
// minutes, which is where a forgotten recording stops itself).
public class VoiceNoteRecorder : MonoBehaviour
{
    public static VoiceNoteRecorder Instance { get; private set; }

    public static string NotesFolder => Path.Combine(AppPaths.DataRoot, "Notas");

    public bool IsRecording { get; private set; }

#if DIRECTOR_BUILD
    const float MaxSeconds = 120f;

    // In the lower-left corner of the view, placeholder position.
    static readonly Vector3 ButtonOffset = new Vector3(RecordingIndicator.ChipOffset.x, -0.2f, RecordingIndicator.ChipOffset.z);
    const float ButtonScale = 0.001f;
    static readonly Vector2 ButtonSize = new Vector2(250f, 76f);
    const int ButtonFontSize = 34;

    static readonly Color IdleColor = new Color(0.16f, 0.16f, 0.2f, 0.9f);
    static readonly Color RecordingColor = new Color(0.95f, 0.22f, 0.22f, 1f);

    InputAction noteAction;
    GameObject buttonObject;
    HudChip buttonChip;
    Font font;

    readonly List<float> samples = new List<float>();
    readonly MicrophoneReader microphone = new MicrophoneReader();
    float recordingStarted;

    string noteTakeGuid;
    int noteScene, noteShot;
    float? noteTakeTime;

    static bool ModeAllowsNotes =>
        (Bootstrap.Mode == Bootstrap.AppMode.Remote || Bootstrap.Mode == Bootstrap.AppMode.Inspect)
        && Bootstrap.IsContentScene(SceneManager.GetActiveScene().name);

    void Awake()
    {
        Instance = this;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // The left stick's click: free for the director. Simulator: the corner button.
        noteAction = new InputAction("VoiceNote", InputActionType.Button);
        noteAction.AddBinding("<XRController>{LeftHand}/{primary2DAxisClick}");
        noteAction.AddBinding("<XRSimulatedController>{LeftHand}/primary2DAxisClick");
        noteAction.performed += OnNotePressed;
        noteAction.Enable();

        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

        noteAction.performed -= OnNotePressed;
        noteAction.Disable();
        noteAction.Dispose();
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        microphone.End();
    }

    // Leaving the scene mid-note: what was said so far is kept.
    void OnSceneUnloaded(Scene scene)
    {
        if (IsRecording)
            StopNote();
    }

    void OnNotePressed(InputAction.CallbackContext context)
    {
        Toggle();
    }

    public void Toggle()
    {
        if (IsRecording)
        {
            StopNote();
            return;
        }

        // Right after a take, with Guardar and Descartar up, is the natural moment for one. That
        // panel hides the corner button, so this is the joystick's click.
        RehearsalRecorder recorder = RehearsalRecorder.Instance;
        bool deciding = recorder != null && recorder.IsDecisionPanelOpen;
        if (ModeAllowsNotes && (!PanelFocusManager.IsModalOpen || deciding))
            StartNote();
    }

    void StartNote()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
            FloatingMessage.Show("Permite el micrófono y vuelve a intentarlo");
            return;
        }
#endif
        if (!microphone.Begin())
        {
            FloatingMessage.Show("No hay micrófono");
            return;
        }

        // Which take this note belongs to.
        RehearsalRecorder recorder = RehearsalRecorder.Instance;
        RecordingPlaybackController playback = RecordingPlaybackController.Instance;
        noteScene = Bootstrap.CurrentSceneNumber;
        noteShot = Bootstrap.CurrentShotNumber;
        noteTakeGuid = null;
        noteTakeTime = null;
        if (recorder != null && recorder.HasPendingTake)
        {
            noteTakeGuid = recorder.PendingTakeGuid;
            noteScene = recorder.PendingTakeScene;
            noteShot = recorder.PendingTakeShot;
            if (recorder.IsRecording)
                noteTakeTime = recorder.RecordingTime;
            else if (playback != null && playback.IsPanelOpen)
                noteTakeTime = playback.PlaybackTime;
        }
        else if (playback != null && playback.IsPanelOpen && playback.CurrentSession != null)
        {
            noteTakeGuid = playback.CurrentSession.SessionGuid;
            noteScene = playback.CurrentSession.SceneNumber;
            noteShot = playback.CurrentSession.ShotNumber;
            noteTakeTime = playback.PlaybackTime;
        }

        samples.Clear();
        recordingStarted = Time.unscaledTime;
        IsRecording = true;
        if (VoiceChat.Instance != null)
            VoiceChat.Instance.NoteRecording = true;

        Debug.Log($"[Notes] recording a note: scene {noteScene}, shot {noteShot}, take {noteTakeGuid ?? "none"}, at {(noteTakeTime.HasValue ? noteTakeTime.Value.ToString("F1") : "-")} s");
    }

    void StopNote()
    {
        microphone.Read(samples);
        IsRecording = false;
        if (VoiceChat.Instance != null)
            VoiceChat.Instance.NoteRecording = false;
        microphone.End();

        int sampleRate = MicrophoneReader.SampleRate;
        float duration = samples.Count / (float)sampleRate;
        if (samples.Count == 0)
        {
            FloatingMessage.Show("La nota quedó vacía");
            return;
        }

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("VoiceNoteRecorder: no ShotListDatabase instance found, note lost.");
            return;
        }

        Directory.CreateDirectory(NotesFolder);
        string fileName = System.Guid.NewGuid().ToString("N") + ".wav";
        WavFile.Save(Path.Combine(NotesFolder, fileName), samples.ToArray(), sampleRate);
        samples.Clear();

        database.SaveVoiceNote(new VoiceNote
        {
            SessionGuid = noteTakeGuid,
            SceneNumber = noteScene,
            ShotNumber = noteShot,
            TakeTime = noteTakeTime,
            FechaHora = System.DateTime.Now.ToString("o"),
            Duracion = duration,
            Archivo = fileName,
        });

        Debug.Log($"[Notes] saved {fileName}: {duration:F1} s");
        FloatingMessage.Show("Nota guardada");
        if (RecordingPlaybackController.Instance != null)
            RecordingPlaybackController.Instance.RefreshNoteTicks();
    }

    void Update()
    {
        if (IsRecording)
        {
            microphone.Read(samples);
            if (Time.unscaledTime - recordingStarted >= MaxSeconds)
                StopNote();
        }

        UpdateButton();
    }

    void UpdateButton()
    {
        if (!ModeAllowsNotes)
        {
            if (buttonObject != null)
            {
                PanelFocusManager.Unregister(buttonObject);
                Destroy(buttonObject);
                buttonObject = null;
            }
            return;
        }

        if (buttonObject == null && !BuildButton())
            return;

        if (IsRecording)
        {
            int seconds = Mathf.FloorToInt(Time.unscaledTime - recordingStarted);
            buttonChip.SetText($"{seconds / 60}:{seconds % 60:00} · Parar");
            buttonChip.Icon.color = RecordingColor;
        }
        else
        {
            buttonChip.SetText("Nota de voz");
            buttonChip.Icon.color = GlassUI.TextColor;
        }
    }

    // A glass chip in the lower-left corner of the view, which the ray can press too.
    bool BuildButton()
    {
        Camera head = Camera.main;
        if (head == null)
            return false;

        buttonChip = GlassUI.HudChip(head.transform, "VoiceNoteButton", ButtonOffset, GlassUI.Icon.Mic, "Nota de voz", GlassUI.TextColor, 0f);
        buttonObject = buttonChip.gameObject;
        buttonObject.AddComponent<CanvasScaler>();
        buttonObject.GetComponent<Image>().raycastTarget = true;
        buttonObject.AddComponent<Button>().onClick.AddListener(Toggle);

        XRUISetup.EnableRayInteraction(buttonObject);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.RegisterPersistent(buttonObject, "Nota de voz");
        return true;
    }
#endif
}
