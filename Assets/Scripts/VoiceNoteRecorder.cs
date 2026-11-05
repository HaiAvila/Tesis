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
// The microphone is Normcore's if the voice chat has it open, read directly so a muted voice chat
// doesn't silence the note too; else its own. 16 kHz mono is plenty for speech and keeps a two
// minute note under 4 MB, and two minutes is where a forgotten recording stops itself.
public class VoiceNoteRecorder : MonoBehaviour
{
    public static VoiceNoteRecorder Instance { get; private set; }

    public static string NotesFolder => Path.Combine(Application.persistentDataPath, "Notas");

    public bool IsRecording { get; private set; }

#if DIRECTOR_BUILD
    const float MaxSeconds = 120f;
    const int TargetSampleRate = 16000;

    // In the lower-left corner of the view, placeholder position.
    static readonly Vector3 ButtonOffset = new Vector3(-0.42f, -0.30f, 1f);
    const float ButtonScale = 0.001f;
    static readonly Vector2 ButtonSize = new Vector2(250f, 76f);
    const int ButtonFontSize = 34;

    static readonly Color IdleColor = new Color(0.16f, 0.16f, 0.2f, 0.9f);
    static readonly Color RecordingColor = new Color(0.75f, 0.1f, 0.1f, 0.95f);

    InputAction noteAction;
    GameObject buttonObject;
    Image buttonImage;
    Text buttonLabel;
    Font font;

    readonly List<float> samples = new List<float>();
    float[] readBuffer = new float[0];
    MicrophoneDevice ownMicrophone;
    MicrophoneDevice readingFrom;
    int readHead;
    int decimationCounter;
    float decimationSum;
    int decimation = 1;
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
        ReleaseMicrophone();
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
        MicrophoneDevice microphone = OpenSharedMicrophone();
        if (microphone == null)
        {
            ownMicrophone = MicrophoneDevice.Start("");
            microphone = ownMicrophone;
        }

        if (microphone == null)
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
        decimationCounter = 0;
        decimationSum = 0f;
        decimation = Mathf.Max(1, microphone.sampleRate / TargetSampleRate);
        BeginReading(microphone);
        recordingStarted = Time.unscaledTime;
        IsRecording = true;
        if (VoiceChat.Instance != null)
            VoiceChat.Instance.NoteRecording = true;

        Debug.Log($"[Notes] recording a note: scene {noteScene}, shot {noteShot}, take {noteTakeGuid ?? "none"}, at {(noteTakeTime.HasValue ? noteTakeTime.Value.ToString("F1") : "-")} s, mic {microphone.sampleRate} Hz");
    }

    // The voice chat's microphone, if it's really open: after a disconnect Normcore has closed it,
    // but sharedMicrophone still points at it. It always opens the default device, "".
    static MicrophoneDevice OpenSharedMicrophone()
    {
        return Microphone.IsRecording("") ? MicrophoneDevice.sharedMicrophone : null;
    }

    void BeginReading(MicrophoneDevice microphone)
    {
        readingFrom = microphone;
        readHead = microphone.deviceWriteHeadPosition;
    }

    void StopNote()
    {
        ReadMicrophone();
        IsRecording = false;
        if (VoiceChat.Instance != null)
            VoiceChat.Instance.NoteRecording = false;
        ReleaseMicrophone();

        int sampleRate = TargetRate();
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

    int TargetRate() => (readingFrom != null ? readingFrom.sampleRate : TargetSampleRate * decimation) / decimation;

    void ReleaseMicrophone()
    {
        if (ownMicrophone != null)
            ownMicrophone.Dispose();
        ownMicrophone = null;
    }

    // The microphone is a ring buffer about a second long: read what's new every frame, down to
    // one averaged mono sample for each `decimation` of them.
    void ReadMicrophone()
    {
        // The voice chat opened it again, a reconnect: that one is what's live now. Ours isn't
        // closed, since closing it would close theirs too.
        MicrophoneDevice shared = OpenSharedMicrophone();
        if (shared != null && shared != readingFrom)
        {
            BeginReading(shared);
            ownMicrophone = null;
        }
        if (readingFrom == null)
            return;

        int total = readingFrom.deviceBufferSampleCount;
        int head = readingFrom.deviceWriteHeadPosition;
        if (total <= 0 || head == readHead)
            return;

        if (head > readHead)
        {
            ReadRange(readHead, head - readHead);
        }
        else
        {
            ReadRange(readHead, total - readHead);
            ReadRange(0, head);
        }
        readHead = head;
    }

    void ReadRange(int offset, int count)
    {
        if (count <= 0)
            return;

        int channels = Mathf.Max(1, readingFrom.numberOfChannels);
        int length = count * channels;
        if (readBuffer.Length != length)
            readBuffer = new float[length];
        if (!readingFrom.GetBufferData(readBuffer, offset))
            return;

        for (int i = 0; i < length; i += channels)
        {
            float mono = 0f;
            for (int c = 0; c < channels; c++)
                mono += readBuffer[i + c];
            decimationSum += mono / channels;
            if (++decimationCounter < decimation)
                continue;

            samples.Add(decimationSum / decimation);
            decimationSum = 0f;
            decimationCounter = 0;
        }
    }

    void Update()
    {
        if (IsRecording)
        {
            ReadMicrophone();
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
            buttonLabel.text = $"● {seconds / 60}:{seconds % 60:00}  Parar";
            buttonImage.color = RecordingColor;
        }
        else
        {
            buttonLabel.text = "● Nota de voz";
            buttonImage.color = IdleColor;
        }
    }

    bool BuildButton()
    {
        Camera head = Camera.main;
        if (head == null)
            return false;

        buttonObject = new GameObject("VoiceNoteButton", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(head.transform, false);
        Canvas canvas = buttonObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = head;

        var rect = (RectTransform)buttonObject.transform;
        rect.sizeDelta = ButtonSize;
        rect.localPosition = ButtonOffset;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * ButtonScale;

        buttonImage = buttonObject.GetComponent<Image>();
        buttonObject.GetComponent<Button>().onClick.AddListener(Toggle);

        var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(rect, false);
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        buttonLabel = textObject.GetComponent<Text>();
        buttonLabel.font = font;
        buttonLabel.fontSize = ButtonFontSize;
        buttonLabel.fontStyle = FontStyle.Bold;
        buttonLabel.color = Color.white;
        buttonLabel.alignment = TextAnchor.MiddleCenter;
        buttonLabel.raycastTarget = false;

        XRUISetup.MoveToUILayer(buttonObject);
        XRUISetup.EnableRayInteraction(buttonObject);
        XRUISetup.EnsureEventSystem();
        PanelFocusManager.RegisterPersistent(buttonObject, "Nota de voz");
        return true;
    }
#endif
}
