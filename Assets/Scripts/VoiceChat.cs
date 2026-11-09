using Normal.Realtime;
using UnityEngine;

// Persistent (added to the Bootstrap object). The director and the actor talking during Ensayo
// Remoto, with Normcore's own voice (RealtimeAvatarVoice, which also asks for the microphone
// permission on the Quest).
//
// Each machine creates its own VoiceClient (a Resources prefab: RealtimeView +
// RealtimeAvatarVoice + AudioSource + VoiceClient, which keeps every copy across scene changes)
// when it joins the room; Normcore removes it when that machine leaves and plays the others'
// voices through their copies. The
// AudioSource isn't spatial, so a remote voice sounds the same wherever anyone stands.
//
// The microphone only sends during Ensayo Remoto, and the pause menu can turn it off (for
// two headsets in the same room, which would otherwise echo). The choice is remembered. The
// server has no one to talk to, so it doesn't create one.
public class VoiceChat : MonoBehaviour
{
    public static VoiceChat Instance { get; private set; }

    const string PrefabName = "VoiceClient";
    const string MutedKey = "Voice.Muted";

    Realtime realtime;
    RealtimeAvatarVoice voice;

    // The director's voice note is private: the actor doesn't hear it.
    public bool NoteRecording { get; set; }

    // Whether the other person hears this microphone now. The take's conversation track keeps
    // only that (TakeAudioRecorder).
    public bool IsTransmitting => voice != null && !voice.mute;

    public bool MicrophoneOn
    {
        get => PlayerPrefs.GetInt(MutedKey, 0) == 0;
        set
        {
            PlayerPrefs.SetInt(MutedKey, value ? 0 : 1);
            PlayerPrefs.Save();
            Debug.Log($"[Voice] microphone {(value ? "on" : "off")}");
        }
    }

    void Awake()
    {
        Instance = this;
    }

#if !SERVER_BUILD
    void Start()
    {
        realtime = GetComponentInChildren<Realtime>();
        if (realtime == null)
        {
            Debug.LogError("[Voice] no Realtime found.");
            return;
        }

        realtime.didConnectToRoom += OnConnectedToRoom;
        if (realtime.connected)
            OnConnectedToRoom(realtime);
    }

    void OnConnectedToRoom(Realtime room)
    {
        if (voice != null)
            return;

        GameObject client = Realtime.Instantiate(PrefabName, new Realtime.InstantiateOptions
        {
            ownedByClient = true,
            preventOwnershipTakeover = true,
            destroyWhenOwnerLeaves = true,
            destroyWhenLastClientLeaves = true,
            useInstance = realtime,
        });

        if (client == null)
        {
            Debug.LogError("[Voice] couldn't create the voice client.");
            return;
        }

        voice = client.GetComponent<RealtimeAvatarVoice>();
        Debug.Log("[Voice] voice client created");
    }

    void Update()
    {
        if (voice != null)
            voice.mute = !MicrophoneOn || NoteRecording || Bootstrap.Mode != Bootstrap.AppMode.Remote;
#if DIRECTOR_BUILD
        HushOthersDuringReplay();
#endif
    }

#if DIRECTOR_BUILD
    // While the director has a replay open, the actor's live voice is silenced here, so only the
    // take's recorded conversation and notes are heard. The actor still hears the director.
    // Never while a take is recording: its conversation track is taken from that voice.
    const float VoiceScanSeconds = 1f;
    readonly System.Collections.Generic.List<AudioSource> otherVoices = new System.Collections.Generic.List<AudioSource>();
    float nextVoiceScan;
    bool hushed;

    void HushOthersDuringReplay()
    {
        bool hush = RecordingPlaybackController.Instance != null && RecordingPlaybackController.Instance.IsPanelOpen
                    && !(TakeAudioRecorder.Instance != null && TakeAudioRecorder.Instance.IsRecording);

        if (hush && Time.unscaledTime >= nextVoiceScan)
        {
            nextVoiceScan = Time.unscaledTime + VoiceScanSeconds;
            otherVoices.Clear();
            foreach (RealtimeAvatarVoice other in FindObjectsByType<RealtimeAvatarVoice>(FindObjectsSortMode.None))
            {
                if (other.isOwnedLocallyInHierarchy)
                    continue;
                AudioSource source = other.GetComponent<AudioSource>();
                if (source != null)
                    otherVoices.Add(source);
            }
        }

        if (hush != hushed)
        {
            hushed = hush;
            nextVoiceScan = 0f;
            Debug.Log($"[Voice] the actor's live voice {(hush ? "silenced for the replay" : "back")}");
        }

        otherVoices.RemoveAll(source => source == null);
        foreach (AudioSource source in otherVoices)
            source.mute = hush;
        if (!hush)
            otherVoices.Clear();
    }
#endif

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (realtime != null)
            realtime.didConnectToRoom -= OnConnectedToRoom;
    }
#endif
}
