using System.Collections.Generic;
using Normal.Realtime;
using UnityEngine;

public enum SyncRole { Unknown = 0, Actor = 1, Director = 2, Server = 3 }

// On the SyncPeer prefab (Resources): this machine's presence in the room, or another machine's
// copy of theirs. Only its owner writes it. SyncLink does the talking; this is the data.
public class SyncPeer : RealtimeComponent<SyncPeerModel>
{
    // Every peer in the room, this machine's included.
    public static readonly List<SyncPeer> All = new List<SyncPeer>();

    public bool IsMine => isOwnedLocallyInHierarchy;
    public bool Ready => model != null && model.peerId != 0;
    public SyncRole Role => model != null ? (SyncRole)model.role : SyncRole.Unknown;
    public int PeerId => model != null ? model.peerId : 0;
    public int OutTo => model != null ? model.outTo : 0;
    public int OutSeq => model != null ? model.outSeq : 0;
    public byte[] OutData => model != null ? model.outData : null;
    public int DataVersion => model != null ? model.dataVersion : 0;
    public string Status => model != null ? model.status ?? "" : "";

    void Awake()
    {
        // A copy that arrives from another machine lands in the active scene: keep it.
        DontDestroyOnLoad(gameObject);
        All.Add(this);
    }

    void OnDestroy()
    {
        All.Remove(this);
    }

    // Which role the machine with that Normcore client ID has (Unknown if it has no peer here).
    public static SyncRole RoleOfClient(int clientId)
    {
        foreach (SyncPeer peer in All)
        {
            if (peer != null && peer.Ready && peer.ownerIDInHierarchy == clientId)
                return peer.Role;
        }
        return SyncRole.Unknown;
    }

    // The last chunk number this peer says it received from that one (0 = none).
    public int AckFor(int peerId)
    {
        string acks = model != null ? model.acks : null;
        if (string.IsNullOrEmpty(acks))
            return 0;

        string key = peerId + ":";
        foreach (string entry in acks.Split(';'))
        {
            if (entry.StartsWith(key) && int.TryParse(entry.Substring(key.Length), out int seq))
                return seq;
        }
        return 0;
    }

    // --- the owner's side ---------------------------------------------------------------------

    bool Writable => model != null && IsMine && realtime != null && realtime.connected;

    public void Introduce(SyncRole role, int peerId)
    {
        if (!Writable)
            return;
        model.role = (int)role;
        model.peerId = peerId;
    }

    public bool PutChunk(int toPeerId, int seq, byte[] data)
    {
        if (!Writable)
            return false;
        model.outData = data;
        model.outSeq = seq;
        model.outTo = toPeerId;
        return true;
    }

    public void SetAcks(string acks)
    {
        if (Writable && model.acks != acks)
            model.acks = acks;
    }

    public void BumpDataVersion()
    {
        if (Writable)
            model.dataVersion = model.dataVersion + 1;
    }

    public void SetStatus(string status)
    {
        if (Writable && model.status != status)
            model.status = status;
    }
}
