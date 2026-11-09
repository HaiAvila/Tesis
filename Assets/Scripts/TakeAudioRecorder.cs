using System.Collections.Generic;
using System.IO;
using Normal.Realtime;
using UnityEngine;

// Persistent (added to the Bootstrap object). Director only: the conversation during a take, as
// two tracks - the director's own microphone (only while the actor can hear it, so voice notes
// stay out of it) and the actor's voice as it's played here. 16 kHz mono each, kept in memory
// with the take and written with Guardar (Audio/<session guid>_director.wav, _actor.wav);
// Descartar drops it. Both start at the take's start and are padded with silence when nothing
// arrives (no actor connected, the microphone stalling), so they stay in step with the replay.
public class TakeAudioRecorder : MonoBehaviour
{
    public static TakeAudioRecorder Instance { get; private set; }

    public const string DirectorTrack = "director";
    public const string ActorTrack = "actor";

    public static string AudioFolder => Path.Combine(Application.persistentDataPath, "Audio");

    public static string TrackPath(string sessionGuid, string track) => Path.Combine(AudioFolder, $"{sessionGuid}_{track}.wav");

    public static void DeleteTracks(string sessionGuid)
    {
        foreach (string track in new[] { DirectorTrack, ActorTrack })
        {
            string path = TrackPath(sessionGuid, track);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

#if DIRECTOR_BUILD
    const float PadAfterSeconds = 0.25f;  // a gap longer than this is filled with silence
    const float TapScanSeconds = 1f;

    readonly List<float> director = new List<float>();
    readonly List<float> actor = new List<float>();
    readonly List<float> actorRaw = new List<float>();
    readonly MicrophoneReader microphone = new MicrophoneReader();
    readonly List<VoiceTap> taps = new List<VoiceTap>();
    bool recording;
    float started;
    float nextTapScan;
    float actorPhase;
    float actorSum;
    int actorSummed;

    public bool HasPending => director.Count > 0 || actor.Count > 0;
    public bool IsRecording => recording;

    void Awake()
    {
        Instance = this;
    }

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
        if (Instance == this)
            Instance = null;
        if (RehearsalRecorder.Instance != null)
        {
            RehearsalRecorder.Instance.RecordingStarted -= OnRecordingStarted;
            RehearsalRecorder.Instance.RecordingStopped -= OnRecordingStopped;
        }
        microphone.End();
    }

    void OnRecordingStarted()
    {
        Discard();
        started = Time.unscaledTime;
        recording = true;
        if (!microphone.Begin())
            Debug.LogWarning("[TakeAudio] no microphone: the director's track will be silent");
        ScanTaps();
        Debug.Log("[TakeAudio] recording the conversation");
    }

    void OnRecordingStopped()
    {
        if (!recording)
            return;

        Collect();
        recording = false;
        microphone.End();
        foreach (VoiceTap tap in taps)
        {
            if (tap != null)
                tap.Collecting = false;
        }

        // Exactly the take's length, so both tracks line up with the replay to the end.
        int length = Mathf.RoundToInt((Time.unscaledTime - started) * MicrophoneReader.SampleRate);
        Fit(director, length);
        Fit(actor, length);
        Debug.Log($"[TakeAudio] conversation recorded: {length / (float)MicrophoneReader.SampleRate:F1} s");
    }

    static void Fit(List<float> track, int length)
    {
        if (track.Count > length)
            track.RemoveRange(length, track.Count - length);
        while (track.Count < length)
            track.Add(0f);
    }

    public void Discard()
    {
        director.Clear();
        actor.Clear();
        actorRaw.Clear();
        actorPhase = 0f;
        actorSum = 0f;
        actorSummed = 0;
    }

    // Guardar. Only a track with sound in it is written.
    public void Save(string sessionGuid)
    {
        if (!HasPending)
            return;

        Directory.CreateDirectory(AudioFolder);
        Write(director, TrackPath(sessionGuid, DirectorTrack));
        Write(actor, TrackPath(sessionGuid, ActorTrack));
        Discard();
    }

    static void Write(List<float> track, string path)
    {
        foreach (float sample in track)
        {
            if (sample != 0f)
            {
                WavFile.Save(path, track.ToArray(), MicrophoneReader.SampleRate);
                return;
            }
        }
    }

    // Ver replay, before Guardar or Descartar. The caller destroys the clips.
    public AudioClip PendingClip(string track)
    {
        List<float> samples = track == DirectorTrack ? director : actor;
        if (samples.Count == 0)
            return null;

        AudioClip clip = AudioClip.Create($"Pending_{track}", samples.Count, 1, MicrophoneReader.SampleRate, false);
        clip.SetData(samples.ToArray(), 0);
        return clip;
    }

    void Update()
    {
        if (!recording)
            return;

        if (Time.unscaledTime >= nextTapScan)
            ScanTaps();
        Collect();
    }

    // The other person's voice copies: each gets a VoiceTap once Normcore has given it its output.
    void ScanTaps()
    {
        nextTapScan = Time.unscaledTime + TapScanSeconds;
        foreach (RealtimeAvatarVoice voice in FindObjectsByType<RealtimeAvatarVoice>(FindObjectsSortMode.None))
        {
            if (voice.isOwnedLocallyInHierarchy || voice.GetComponent<AudioOutput>() == null)
                continue;

            VoiceTap tap = voice.GetComponent<VoiceTap>();
            if (tap == null)
                tap = voice.gameObject.AddComponent<VoiceTap>();
            if (!taps.Contains(tap))
                taps.Add(tap);
            tap.Collecting = true;
        }
    }

    void Collect()
    {
        // Only what the actor could hear: not a voice note, not with the microphone off.
        bool transmitting = VoiceChat.Instance != null && VoiceChat.Instance.IsTransmitting;
        microphone.Read(director, !transmitting);

        actorRaw.Clear();
        taps.RemoveAll(tap => tap == null);
        foreach (VoiceTap tap in taps)
            tap.Drain(actorRaw);
        Resample(actorRaw, AudioSettings.outputSampleRate, actor);

        int expected = Mathf.RoundToInt((Time.unscaledTime - started) * MicrophoneReader.SampleRate);
        int slack = Mathf.RoundToInt(PadAfterSeconds * MicrophoneReader.SampleRate);
        if (director.Count < expected - slack)
            Fit(director, expected);
        if (actor.Count < expected - slack)
            Fit(actor, expected);
    }

    void Resample(List<float> source, int sourceRate, List<float> into)
    {
        int rate = Mathf.Max(MicrophoneReader.SampleRate, sourceRate);
        foreach (float sample in source)
        {
            actorSum += sample;
            actorSummed++;
            actorPhase += MicrophoneReader.SampleRate;
            if (actorPhase < rate)
                continue;

            actorPhase -= rate;
            into.Add(actorSum / actorSummed);
            actorSum = 0f;
            actorSummed = 0;
        }
    }
#endif
}
