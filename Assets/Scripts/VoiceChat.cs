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
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (realtime != null)
            realtime.didConnectToRoom -= OnConnectedToRoom;
    }
#endif
}
