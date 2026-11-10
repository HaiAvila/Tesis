using System;
using System.Collections.Generic;
using System.Text;
using Normal.Realtime;
using UnityEngine;

// Persistent (added to the Bootstrap object). Sends whole messages of any size between two
// machines in the room, through Normcore - the only path there is between a director somewhere
// else and the server next to the actor. TakeSync is what uses it.
//
// Each machine creates its own SyncPeer when it joins (as VoiceChat does its VoiceClient). A
// message is cut into chunks; one chunk at a time sits in the sender's SyncPeer, addressed to
// one peer, until that peer's SyncPeer says it has it; then the next. One chunk per round trip
// (roughly 5 to 10 a second), which is slow and steady on purpose: it shares the room with the
// live pose, the voice and the cue commands, and nothing here is urgent.
//
// Messages to a peer arrive whole, in order, once - or not at all if that peer leaves (its
// unsent messages are dropped, PeerLost is raised). Each travels with a checksum: one that
// arrives damaged is thrown away and MessageDamaged is raised, never handed over.
public class SyncLink : MonoBehaviour
{
    public static SyncLink Instance { get; private set; }

    const string PrefabName = "SyncPeer";
    const int ChunkBytes = 12000;
    const float AckTimeoutSeconds = 20f;
    const int MaxMessageBytes = 64 * 1024 * 1024;

    public static SyncRole LocalRole
    {
        get
        {
#if SERVER_BUILD
            return SyncRole.Server;
#elif DIRECTOR_BUILD
            return SyncRole.Director;
#else
            return SyncRole.Actor;
#endif
        }
    }

    // (from, the whole message)
    public event Action<SyncPeer, byte[]> MessageReceived;
    // A peer left, or stopped answering, with messages to or from it unfinished.
    public event Action<int> PeerLost;
    // A message from that peer didn't match its checksum and was thrown away.
    public event Action<SyncPeer> MessageDamaged;

    Realtime realtime;
    SyncPeer own;
    int ownPeerId;

    struct Chunk
    {
        public int To;
        public byte[] Data;
    }

    readonly Queue<Chunk> outgoing = new Queue<Chunk>();
    bool inFlight;
    int inFlightTo;
    int inFlightSeq;
    float inFlightSince;
    int nextSeq;

    readonly Dictionary<int, int> lastSeqFrom = new Dictionary<int, int>();
    readonly Dictionary<int, List<byte[]>> partial = new Dictionary<int, List<byte[]>>();
    readonly List<int> gone = new List<int>();
    string acksSent = "";

    // No new chunk leaves while this is set (a take is recording). What's already on its way
    // finishes, and received chunks are still acknowledged.
    public bool Paused { get; set; }

    public SyncPeer Own => own != null && own.Ready ? own : null;
    public bool Connected => Own != null && realtime != null && realtime.connected;
    public int PendingChunks => outgoing.Count + (inFlight ? 1 : 0);

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (realtime != null)
            realtime.didConnectToRoom -= OnConnectedToRoom;
    }

    void Start()
    {
        realtime = GetComponentInChildren<Realtime>();
        if (realtime == null)
        {
            Debug.LogError("[Sync] no Realtime found.");
            return;
        }

        realtime.didConnectToRoom += OnConnectedToRoom;
        if (realtime.connected)
            OnConnectedToRoom(realtime);
    }

    // Also after a reconnect: Normcore destroyed the old peer with the old connection.
    void OnConnectedToRoom(Realtime room)
    {
        if (own != null)
            return;

        GameObject peer = Realtime.Instantiate(PrefabName, new Realtime.InstantiateOptions
        {
            ownedByClient = true,
            preventOwnershipTakeover = true,
            destroyWhenOwnerLeaves = true,
            destroyWhenLastClientLeaves = true,
            useInstance = realtime,
        });
        if (peer == null)
        {
            Debug.LogError("[Sync] couldn't create this machine's SyncPeer.");
            return;
        }

        own = peer.GetComponent<SyncPeer>();
        ownPeerId = UnityEngine.Random.Range(1, int.MaxValue);
        ResetPipes();
        own.Introduce(LocalRole, ownPeerId);
        Debug.Log($"[Sync] in the room as {LocalRole} (peer {ownPeerId})");
    }

    void ResetPipes()
    {
        outgoing.Clear();
        inFlight = false;
        nextSeq = 0;
        lastSeqFrom.Clear();
        partial.Clear();
        acksSent = "";
    }

    // The first other machine with that role, if any.
    public SyncPeer Find(SyncRole role)
    {
        foreach (SyncPeer peer in SyncPeer.All)
        {
            if (peer != null && peer != own && peer.Ready && peer.Role == role)
                return peer;
        }
        return null;
    }

    public SyncPeer Find(int peerId)
    {
        foreach (SyncPeer peer in SyncPeer.All)
        {
            if (peer != null && peer != own && peer.Ready && peer.PeerId == peerId)
                return peer;
        }
        return null;
    }

    // Queues a whole message for that peer. False if it can't be sent now.
    public bool Send(int toPeerId, byte[] message)
    {
        if (!Connected || message == null || Find(toPeerId) == null)
            return false;

        // The message's checksum goes first.
        var framed = new byte[message.Length + 4];
        Buffer.BlockCopy(BitConverter.GetBytes(Checksum(message, 0, message.Length)), 0, framed, 0, 4);
        Buffer.BlockCopy(message, 0, framed, 4, message.Length);
        message = framed;

        int offset = 0;
        do
        {
            int length = Mathf.Min(ChunkBytes, message.Length - offset);
            var data = new byte[length + 1];
            data[0] = (byte)(offset + length >= message.Length ? 1 : 0); // 1 = the message ends here
            Buffer.BlockCopy(message, offset, data, 1, length);
            outgoing.Enqueue(new Chunk { To = toPeerId, Data = data });
            offset += length;
        } while (offset < message.Length);
        return true;
    }

    void Update()
    {
        if (own == null)
        {
            // Destroyed with the connection: start clean when the room is back.
            if (ownPeerId != 0)
            {
                ownPeerId = 0;
                ResetPipes();
            }
            return;
        }
        if (!Connected)
            return;

        Receive();
        Transmit();
    }

    void Receive()
    {
        bool acksChanged = false;
        foreach (SyncPeer peer in SyncPeer.All)
        {
            if (peer == null || peer == own || !peer.Ready || peer.OutTo != ownPeerId)
                continue;

            int seq = peer.OutSeq;
            lastSeqFrom.TryGetValue(peer.PeerId, out int last);
            if (seq == 0 || seq == last)
                continue;

            lastSeqFrom[peer.PeerId] = seq;
            acksChanged = true;

            byte[] data = peer.OutData;
            if (data == null || data.Length < 1)
                continue;
            // A copy: Normcore writes the next chunk into the same array when it's the same
            // length (every full chunk of a long message is), which would overwrite this one.
            data = (byte[])data.Clone();

            if (!partial.TryGetValue(peer.PeerId, out List<byte[]> parts))
                partial[peer.PeerId] = parts = new List<byte[]>();
            parts.Add(data);
            if (data[0] != 1)
                continue;

            int total = 0;
            foreach (byte[] part in parts)
                total += part.Length - 1;
            var framed = new byte[total];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Buffer.BlockCopy(part, 1, framed, offset, part.Length - 1);
                offset += part.Length - 1;
            }
            partial.Remove(peer.PeerId);

            if (total < 4 || BitConverter.ToUInt32(framed, 0) != Checksum(framed, 4, total - 4))
            {
                Debug.LogError($"[Sync] a message from peer {peer.PeerId} arrived damaged ({total} bytes), thrown away");
                MessageDamaged?.Invoke(peer);
                continue;
            }
            var message = new byte[total - 4];
            Buffer.BlockCopy(framed, 4, message, 0, message.Length);

            try
            {
                MessageReceived?.Invoke(peer, message);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Sync] a message from peer {peer.PeerId} failed: {e}");
            }
        }

        // Forget the peers that left, and whatever they hadn't finished sending.
        gone.Clear();
        foreach (int peerId in lastSeqFrom.Keys)
        {
            if (Find(peerId) == null)
                gone.Add(peerId);
        }
        foreach (int peerId in gone)
        {
            lastSeqFrom.Remove(peerId);
            acksChanged = true;
            if (partial.Remove(peerId))
                PeerLost?.Invoke(peerId);
        }

        // A half-received message that outgrows any real one: drop it.
        foreach (KeyValuePair<int, List<byte[]>> entry in partial)
        {
            if (entry.Value.Count * (long)ChunkBytes > MaxMessageBytes)
            {
                entry.Value.Clear();
                Debug.LogWarning($"[Sync] a message from peer {entry.Key} was too long, dropped");
            }
        }

        if (acksChanged)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<int, int> entry in lastSeqFrom)
                text.Append(entry.Key).Append(':').Append(entry.Value).Append(';');
            acksSent = text.ToString();
        }
        own.SetAcks(acksSent);
    }

    void Transmit()
    {
        if (inFlight)
        {
            SyncPeer target = Find(inFlightTo);
            if (target == null)
            {
                DropMessagesTo(inFlightTo, "it left the room");
                return;
            }
            if (target.AckFor(ownPeerId) != inFlightSeq)
            {
                if (Time.unscaledTime - inFlightSince > AckTimeoutSeconds)
                    DropMessagesTo(inFlightTo, "it stopped answering");
                return;
            }
            inFlight = false;
        }

        if (Paused)
            return;

        while (outgoing.Count > 0)
        {
            Chunk chunk = outgoing.Dequeue();
            if (Find(chunk.To) == null)
                continue; // the peer left since this was queued

            if (nextSeq == int.MaxValue)
                nextSeq = 0;
            inFlightSeq = ++nextSeq;
            inFlightTo = chunk.To;
            inFlightSince = Time.unscaledTime;
            inFlight = own.PutChunk(chunk.To, inFlightSeq, chunk.Data);
            return;
        }
    }

    // FNV-1a, 32 bits.
    static uint Checksum(byte[] data, int offset, int length)
    {
        unchecked
        {
            uint hash = 2166136261;
            for (int i = offset; i < offset + length; i++)
                hash = (hash ^ data[i]) * 16777619;
            return hash;
        }
    }

    void DropMessagesTo(int peerId, string why)
    {
        inFlight = false;
        int dropped = 0;
        int count = outgoing.Count;
        for (int i = 0; i < count; i++)
        {
            Chunk chunk = outgoing.Dequeue();
            if (chunk.To == peerId)
                dropped++;
            else
                outgoing.Enqueue(chunk);
        }
        Debug.LogWarning($"[Sync] peer {peerId}: {why}, {dropped + 1} unsent chunks dropped");
        PeerLost?.Invoke(peerId);
    }
}
