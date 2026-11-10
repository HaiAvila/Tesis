using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

// Persistent (added to the Bootstrap object). Keeps every device's takes and cue timelines the
// same as the server's, which is the store of record: whatever a director records, marks, edits
// or deletes ends up on the server PC, and from there on every other device that joins the room
// - so Ensayar Solo and a shoot day can run from the server alone, and a headset is never the
// only place a take lives. Through SyncLink (Normcore), so it works with the director somewhere
// else.
//
// The server only answers. Each device (director or actor) runs a pass whenever it sees the
// server, something changed here, or the server says its data changed (SyncPeer.dataVersion):
//   1. asks for the server's catalog: its takes, the ones deleted, each shot's cue timeline,
//      each with the stamp of its last change (ShotListDatabase.Sync.cs);
//   2. deletions go both ways first;
//   3. takes the server lacks are sent, takes this device lacks are fetched (whole: head track,
//      camera, avatar, boundaries, set events);
//   4. marks (preferida, favorita) and cue timelines: the newer stamp wins; with equal stamps
//      the server's copy stays - except a cue timeline made from the take that is the shot's
//      preferida on the server, which replaces one that isn't.
// A pass that sent anything is followed by another, until one finds nothing to do.
//
// Not synced yet: the takes' audio (conversation) and the voice notes - they stay on the device
// that recorded them.
//
// Nothing is sent while a take is recording (SyncLink.Paused): the room is for the pose, the
// voice and the cues then.
public class TakeSync : MonoBehaviour
{
    public static TakeSync Instance { get; private set; }

    enum Msg : byte
    {
        Catalog = 1, CatalogReply = 2,
        GetTake = 3, TakeReply = 4,
        PutTake = 5, Result = 6,
        SetMarks = 7,
        Delete = 8,
        GetCues = 9, CuesReply = 10,
        PutCues = 11,
        Failed = 12,    // the server couldn't read the request: the pass stops and is tried again
    }

    const int HeaderBytes = 6;              // type, flags, request id
    const byte FlagDeflated = 1;
    const float FirstPassDelay = 3f;        // after the server shows up
    const float ChangeDebounce = 2f;        // after the last local change
    const float RetryDelay = 10f;           // after a pass that failed
    const float ReplyTimeout = 120f;        // not counting the time paused by a recording
    const int MaxPassesInARow = 4;

    // For whoever shows it (the server's window; later a director's panel).
    public string StatusText { get; private set; } = "";
    public bool Busy => running;
    // A pass brought takes or timelines from the server (how many items).
    public static event Action<int> DataArrived;

    SyncLink link;
    ShotListDatabase database;

    // Device
    bool running;
    bool localDirty = true;
    float dirtyTime;
    int syncedServerPeer;
    int syncedVersion = -1;
    float serverSeenAt = -1f;
    float nextAttempt;
    int requestId;
    int awaiting;
    bool failed;
    Msg replyType;
    byte[] replyBody;
    int serverPeerId;

    // Takes the server wouldn't keep (unreadable, or deleted there): not offered again this run.
    readonly HashSet<string> refused = new HashSet<string>();

    // Server
    bool storeChanged;
    int storedTakes;
    float nextCountTime;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        link = GetComponent<SyncLink>();
        database = FindFirstObjectByType<ShotListDatabase>();
        if (link == null || database == null)
        {
            Debug.LogError("[Sync] TakeSync needs SyncLink on the same object and a ShotListDatabase.");
            enabled = false;
            return;
        }

        link.MessageReceived += OnMessage;
        link.PeerLost += OnPeerLost;
        link.MessageDamaged += OnMessageDamaged;
        ShotListDatabase.Changed += OnLocalChange;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (link != null)
        {
            link.MessageReceived -= OnMessage;
            link.PeerLost -= OnPeerLost;
            link.MessageDamaged -= OnMessageDamaged;
        }
        ShotListDatabase.Changed -= OnLocalChange;
    }

    void OnLocalChange()
    {
        localDirty = true;
        dirtyTime = Time.unscaledTime;
        storeChanged = true;
    }

    static bool Recording => RehearsalSessionSync.Instance != null && RehearsalSessionSync.Instance.IsRecording;

    void Update()
    {
        link.Paused = Recording;
        if (!link.Connected)
        {
            if (!running)
                StatusText = "Sin conexión";
            return;
        }

        if (SyncLink.LocalRole == SyncRole.Server)
        {
            if (storeChanged)
            {
                storeChanged = false;
                link.Own.BumpDataVersion();
            }
            if (Time.unscaledTime >= nextCountTime)
            {
                nextCountTime = Time.unscaledTime + 2f;
                storedTakes = database.CountTakes();
            }
            StatusText = $"{storedTakes} tomas guardadas";
            return;
        }

        if (running)
            return;

        SyncPeer server = link.Find(SyncRole.Server);
        if (server == null)
        {
            serverSeenAt = -1f;
            StatusText = "Sin servidor en la sala";
            Report("");
            return;
        }
        if (serverSeenAt < 0f)
            serverSeenAt = Time.unscaledTime;

        bool serverIsNew = server.PeerId != syncedServerPeer;
        bool serverChanged = server.DataVersion != syncedVersion;
        bool localReady = localDirty && Time.unscaledTime - dirtyTime >= ChangeDebounce;
        if (!serverIsNew && !serverChanged && !localReady)
            return;
        if (Recording || Time.unscaledTime < nextAttempt || Time.unscaledTime - serverSeenAt < FirstPassDelay)
            return;

        StartCoroutine(RunPasses(server));
    }

    void Report(string status)
    {
        if (link.Own != null)
            link.Own.SetStatus(status);
    }

    void Say(string status)
    {
        StatusText = status;
        Report(status);
    }

    // --- device: the passes ---------------------------------------------------------------------

    IEnumerator RunPasses(SyncPeer server)
    {
        running = true;
        serverPeerId = server.PeerId;
        int received = 0;

        for (int pass = 0; pass < MaxPassesInARow; pass++)
        {
            int versionAtStart = server != null ? server.DataVersion : 0;
            localDirty = false;
            failed = false;

            var counts = new PassCounts();
            yield return Pass(counts);
            received += counts.Received;

            if (failed || server == null || link.Find(serverPeerId) == null)
            {
                failed = true;
                break;
            }
            if (counts.Sent == 0)
            {
                syncedServerPeer = serverPeerId;
                syncedVersion = versionAtStart;
                break;
            }
            if (pass == MaxPassesInARow - 1)
            {
                // Still sending after this many rounds: something keeps not sticking. Later.
                nextAttempt = Time.unscaledTime + 60f;
                Debug.LogWarning("[Sync] still not settled after several passes, trying again in a minute");
            }
        }

        if (failed)
        {
            localDirty = true;
            dirtyTime = Time.unscaledTime;
            nextAttempt = Time.unscaledTime + RetryDelay;
            Say("Sincronización interrumpida, se reintenta");
            Debug.LogWarning("[Sync] pass interrupted, will retry");
        }
        else
        {
            Say($"Sincronizado ({database.CountTakes()} tomas)");
        }

        if (received > 0)
        {
            Debug.Log($"[Sync] {received} items received from the server");
            DataArrived?.Invoke(received);
        }
        running = false;
    }

    class PassCounts
    {
        public int Sent;
        public int Received;
    }

    IEnumerator Pass(PassCounts counts)
    {
        Say("Sincronizando…");
        yield return Ask(Msg.Catalog, null);
        if (failed || replyType != Msg.CatalogReply)
        {
            failed = true;
            yield break;
        }

        Catalog theirs;
        try
        {
            theirs = Catalog.Read(replyBody);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sync] the server's catalog couldn't be read: {e.Message}");
            failed = true;
            yield break;
        }

        var mine = new Dictionary<string, ShotListDatabase.TakeStamp>();
        foreach (ShotListDatabase.TakeStamp stamp in database.GetTakeStamps())
            mine[stamp.Guid] = stamp;
        Dictionary<string, long> myDeleted = database.GetDeletedTakes();

        // Deleted here: deleted there.
        foreach (KeyValuePair<string, long> deleted in myDeleted)
        {
            if (theirs.Deleted.ContainsKey(deleted.Key))
                continue;
            yield return Ask(Msg.Delete, Write(w => { w.Write(deleted.Key); w.Write(deleted.Value); }));
            if (failed) yield break;
            theirs.Takes.Remove(deleted.Key);
            counts.Sent++;
        }

        // Deleted there: deleted here.
        foreach (KeyValuePair<string, long> deleted in theirs.Deleted)
        {
            if (myDeleted.ContainsKey(deleted.Key))
                continue;
            if (database.DeleteTakeFromSync(deleted.Key, deleted.Value))
            {
                Debug.Log($"[Sync] take {deleted.Key} deleted here: it was deleted on another device");
                counts.Received++;
            }
            mine.Remove(deleted.Key);
        }

        // Takes the server lacks.
        var toSend = new List<string>();
        foreach (string guid in mine.Keys)
        {
            if (!theirs.Takes.ContainsKey(guid) && !theirs.Deleted.ContainsKey(guid) && !refused.Contains(guid))
                toSend.Add(guid);
        }
        for (int i = 0; i < toSend.Count; i++)
        {
            Say($"Enviando toma {i + 1} de {toSend.Count} al servidor");
            byte[] take = database.ExportTake(toSend[i]);
            if (take == null)
                continue;
            yield return Ask(Msg.PutTake, take);
            if (failed) yield break;
            var result = replyBody != null && replyBody.Length > 0 ? (ShotListDatabase.ImportResult)replyBody[0] : ShotListDatabase.ImportResult.Unreadable;
            if (result == ShotListDatabase.ImportResult.Added)
                counts.Sent++;
            else if (result != ShotListDatabase.ImportResult.AlreadyHere)
            {
                refused.Add(toSend[i]);
                Debug.LogWarning($"[Sync] the server didn't keep take {toSend[i]} ({result})");
            }
        }

        // Takes this device lacks.
        var toFetch = new List<string>();
        foreach (string guid in theirs.Takes.Keys)
        {
            if (!mine.ContainsKey(guid) && !myDeleted.ContainsKey(guid))
                toFetch.Add(guid);
        }
        for (int i = 0; i < toFetch.Count; i++)
        {
            Say($"Recibiendo toma {i + 1} de {toFetch.Count} del servidor");
            yield return Ask(Msg.GetTake, Write(w => w.Write(toFetch[i])));
            if (failed) yield break;
            if (replyType == Msg.TakeReply && replyBody != null && replyBody.Length > 0
                && database.ImportTake(replyBody) == ShotListDatabase.ImportResult.Added)
                counts.Received++;
        }

        // Marks of the takes both have.
        foreach (KeyValuePair<string, ShotListDatabase.TakeStamp> entry in mine)
        {
            if (!theirs.Takes.TryGetValue(entry.Key, out ShotListDatabase.TakeStamp server))
                continue;
            ShotListDatabase.TakeStamp local = entry.Value;
            bool same = local.EsPreferida == server.EsPreferida && local.EsFavorita == server.EsFavorita;

            if (local.Rev > server.Rev)
            {
                yield return Ask(Msg.SetMarks, Write(w =>
                {
                    w.Write(local.Guid);
                    w.Write(local.Rev);
                    w.Write(local.EsPreferida);
                    w.Write(local.EsFavorita);
                }));
                if (failed) yield break;
                counts.Sent++;
            }
            else if (server.Rev > local.Rev || !same)
            {
                if (database.ApplyTakeMarks(local.Guid, server.Rev, server.EsPreferida, server.EsFavorita, force: true) && !same)
                    counts.Received++;
            }
        }

        // Each shot's cue timeline. The preferidas are read again: fetched takes and received
        // marks may have changed them.
        var serverPreferida = new Dictionary<int, string>();
        foreach (ShotListDatabase.TakeStamp stamp in theirs.Takes.Values)
        {
            if (stamp.EsPreferida && stamp.Tipo != RecordingSession.TipoEscena)
                serverPreferida[stamp.ShotNumber] = stamp.Guid;
        }

        Dictionary<int, ShotListDatabase.CueStamp> myCues = database.GetCueStamps();
        var shots = new HashSet<int>(myCues.Keys);
        shots.UnionWith(theirs.Cues.Keys);
        foreach (int shot in shots)
        {
            myCues.TryGetValue(shot, out ShotListDatabase.CueStamp local);
            theirs.Cues.TryGetValue(shot, out ShotListDatabase.CueStamp server);
            local = local ?? new ShotListDatabase.CueStamp { ShotNumber = shot };
            server = server ?? new ShotListDatabase.CueStamp { ShotNumber = shot };
            if (local.Rev == server.Rev && local.Count == server.Count && local.Hash == server.Hash)
                continue;

            bool send;
            if (local.Rev != server.Rev)
                send = local.Rev > server.Rev;
            else if (server.Count == 0)
                send = true;
            else if (local.Count == 0)
                send = false;
            else
            {
                // Same stamp, different timelines (both from before syncing): the one made from
                // the take that is the shot's preferida on the server is the right one.
                serverPreferida.TryGetValue(shot, out string preferida);
                send = !string.IsNullOrEmpty(preferida) && local.AutoGuid == preferida && server.AutoGuid != preferida;
            }

            if (send)
            {
                byte[] rows = database.ExportCues(shot);
                yield return Ask(Msg.PutCues, Write(w =>
                {
                    w.Write(shot);
                    w.Write(local.Rev);
                    w.Write(rows.Length);
                    w.Write(rows);
                }));
                if (failed) yield break;
                counts.Sent++;
            }
            else
            {
                yield return Ask(Msg.GetCues, Write(w => w.Write(shot)));
                if (failed) yield break;
                if (replyType != Msg.CuesReply || replyBody == null)
                    continue;
                try
                {
                    using (var reader = new BinaryReader(new MemoryStream(replyBody), Encoding.UTF8))
                    {
                        long rev = reader.ReadInt64();
                        byte[] rows = reader.ReadBytes(reader.ReadInt32());
                        if (database.ImportCues(shot, rev, rows))
                            counts.Received++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Sync] shot {shot}'s cues from the server couldn't be read: {e.Message}");
                }
            }
        }

        Debug.Log($"[Sync] pass done: {counts.Sent} sent, {counts.Received} received");
    }

    // Sends a request to the server and waits for its answer (replyType, replyBody). Sets failed
    // if the server leaves or doesn't answer.
    IEnumerator Ask(Msg type, byte[] body)
    {
        while (Recording)
            yield return null;

        int id = ++requestId;
        awaiting = id;
        replyBody = null;
        replyType = 0;
        if (!link.Send(serverPeerId, Pack(type, id, body)))
        {
            awaiting = 0;
            failed = true;
            yield break;
        }

        float waited = 0f;
        while (awaiting == id && !failed)
        {
            if (!Recording)
                waited += Time.unscaledDeltaTime;
            if (waited > ReplyTimeout || !link.Connected)
                failed = true;
            yield return null;
        }
        awaiting = 0;
    }

    void OnPeerLost(int peerId)
    {
        if (running && peerId == serverPeerId)
            failed = true;
    }

    // A request or an answer arrived damaged: this pass stops and is tried again later. The
    // server can't tell which request it was, so it says so with no request id.
    void OnMessageDamaged(SyncPeer from)
    {
        if (SyncLink.LocalRole == SyncRole.Server)
            Reply(from, Msg.Failed, 0, null);
        else if (running && from.PeerId == serverPeerId)
            failed = true;
    }

    // --- both: messages ---------------------------------------------------------------------

    void OnMessage(SyncPeer from, byte[] message)
    {
        if (message == null || message.Length < HeaderBytes)
            return;

        var type = (Msg)message[0];
        int id = BitConverter.ToInt32(message, 2);
        byte[] body;
        try
        {
            body = (message[1] & FlagDeflated) != 0 ? Inflate(message, HeaderBytes) : Slice(message, HeaderBytes);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sync] a message from peer {from.PeerId} couldn't be unpacked: {e.Message}");
            if (SyncLink.LocalRole == SyncRole.Server)
                Reply(from, Msg.Failed, id, null);
            else if (from.PeerId == serverPeerId && id == awaiting)
                failed = true;
            return;
        }

        if (SyncLink.LocalRole == SyncRole.Server)
        {
            Answer(from, type, id, body);
            return;
        }

        // A device: only the answer it's waiting for, from the server it asked.
        if (from.PeerId != serverPeerId)
            return;
        if (type == Msg.Failed && (id == awaiting || id == 0))
        {
            if (running)
                failed = true;
            return;
        }
        if (id != awaiting)
            return;
        replyType = type;
        replyBody = body;
        awaiting = 0;
    }

    // --- server: answers ------------------------------------------------------------------------

    void Answer(SyncPeer from, Msg type, int id, byte[] body)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8))
            {
                switch (type)
                {
                    case Msg.Catalog:
                        Reply(from, Msg.CatalogReply, id, Catalog.From(database).Write());
                        break;

                    case Msg.GetTake:
                        Reply(from, Msg.TakeReply, id, database.ExportTake(reader.ReadString()) ?? new byte[0]);
                        break;

                    case Msg.PutTake:
                    {
                        ShotListDatabase.ImportResult result = database.ImportTake(body);
                        if (result == ShotListDatabase.ImportResult.Added)
                            StoreChanged();
                        Reply(from, Msg.Result, id, new[] { (byte)result });
                        break;
                    }

                    case Msg.SetMarks:
                    {
                        string guid = reader.ReadString();
                        long rev = reader.ReadInt64();
                        bool preferida = reader.ReadBoolean();
                        bool favorita = reader.ReadBoolean();
                        if (database.ApplyTakeMarks(guid, rev, preferida, favorita, force: false))
                            StoreChanged();
                        Reply(from, Msg.Result, id, null);
                        break;
                    }

                    case Msg.Delete:
                    {
                        string guid = reader.ReadString();
                        long when = reader.ReadInt64();
                        bool wasHere = database.DeleteTakeFromSync(guid, when);
                        Debug.Log($"[Sync] take {guid} deleted on {from.Role}'s device" + (wasHere ? ": deleted here too" : ""));
                        StoreChanged();
                        Reply(from, Msg.Result, id, null);
                        break;
                    }

                    case Msg.GetCues:
                    {
                        int shot = reader.ReadInt32();
                        database.GetCueStamps().TryGetValue(shot, out ShotListDatabase.CueStamp stamp);
                        byte[] rows = database.ExportCues(shot);
                        Reply(from, Msg.CuesReply, id, Write(w =>
                        {
                            w.Write(stamp != null ? stamp.Rev : 0L);
                            w.Write(rows.Length);
                            w.Write(rows);
                        }));
                        break;
                    }

                    case Msg.PutCues:
                    {
                        int shot = reader.ReadInt32();
                        long rev = reader.ReadInt64();
                        byte[] rows = reader.ReadBytes(reader.ReadInt32());
                        database.GetCueStamps().TryGetValue(shot, out ShotListDatabase.CueStamp stamp);
                        // Never an older timeline over a newer one; a tie is the device's call
                        // (it has both stamps and the rule - see Pass).
                        if ((stamp == null || rev >= stamp.Rev) && database.ImportCues(shot, rev, rows))
                            StoreChanged();
                        Reply(from, Msg.Result, id, null);
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sync] {type} from peer {from.PeerId} failed: {e}");
            Reply(from, Msg.Failed, id, null);
        }
    }

    void StoreChanged()
    {
        storeChanged = true;
        nextCountTime = 0f;
    }

    void Reply(SyncPeer to, Msg type, int id, byte[] body)
    {
        link.Send(to.PeerId, Pack(type, id, body));
    }

    // --- the catalog ------------------------------------------------------------------------

    class Catalog
    {
        public readonly Dictionary<string, ShotListDatabase.TakeStamp> Takes = new Dictionary<string, ShotListDatabase.TakeStamp>();
        public Dictionary<string, long> Deleted = new Dictionary<string, long>();
        public Dictionary<int, ShotListDatabase.CueStamp> Cues = new Dictionary<int, ShotListDatabase.CueStamp>();

        public static Catalog From(ShotListDatabase database)
        {
            var catalog = new Catalog { Deleted = database.GetDeletedTakes(), Cues = database.GetCueStamps() };
            foreach (ShotListDatabase.TakeStamp stamp in database.GetTakeStamps())
                catalog.Takes[stamp.Guid] = stamp;
            return catalog;
        }

        public byte[] Write()
        {
            return TakeSync.Write(w =>
            {
                w.Write(Takes.Count);
                foreach (ShotListDatabase.TakeStamp take in Takes.Values)
                {
                    w.Write(take.Guid);
                    w.Write(take.Rev);
                    w.Write(take.SceneNumber);
                    w.Write(take.ShotNumber);
                    w.Write(take.Tipo ?? RecordingSession.TipoToma);
                    w.Write(take.FechaHora ?? "");
                    w.Write(take.EsPreferida);
                    w.Write(take.EsFavorita);
                }

                w.Write(Deleted.Count);
                foreach (KeyValuePair<string, long> deleted in Deleted)
                {
                    w.Write(deleted.Key);
                    w.Write(deleted.Value);
                }

                w.Write(Cues.Count);
                foreach (ShotListDatabase.CueStamp cue in Cues.Values)
                {
                    w.Write(cue.ShotNumber);
                    w.Write(cue.Rev);
                    w.Write(cue.Count);
                    w.Write(cue.Hash);
                    w.Write(cue.AutoGuid ?? "");
                }
            });
        }

        public static Catalog Read(byte[] data)
        {
            var catalog = new Catalog();
            using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                for (int i = reader.ReadInt32(); i > 0; i--)
                {
                    var take = new ShotListDatabase.TakeStamp
                    {
                        Guid = reader.ReadString(),
                        Rev = reader.ReadInt64(),
                        SceneNumber = reader.ReadInt32(),
                        ShotNumber = reader.ReadInt32(),
                        Tipo = reader.ReadString(),
                        FechaHora = reader.ReadString(),
                        EsPreferida = reader.ReadBoolean(),
                        EsFavorita = reader.ReadBoolean(),
                    };
                    catalog.Takes[take.Guid] = take;
                }

                for (int i = reader.ReadInt32(); i > 0; i--)
                {
                    string guid = reader.ReadString();
                    catalog.Deleted[guid] = reader.ReadInt64();
                }

                for (int i = reader.ReadInt32(); i > 0; i--)
                {
                    var cue = new ShotListDatabase.CueStamp
                    {
                        ShotNumber = reader.ReadInt32(),
                        Rev = reader.ReadInt64(),
                        Count = reader.ReadInt32(),
                        Hash = reader.ReadInt32(),
                        AutoGuid = reader.ReadString(),
                    };
                    catalog.Cues[cue.ShotNumber] = cue;
                }
            }
            return catalog;
        }
    }

    // --- packing ----------------------------------------------------------------------------

    static byte[] Write(Action<BinaryWriter> fill)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            fill(writer);
            writer.Flush();
            return stream.ToArray();
        }
    }

    // type, flags, request id, then the body - deflated when that makes it smaller.
    static byte[] Pack(Msg type, int id, byte[] body)
    {
        body = body ?? new byte[0];
        byte flags = 0;
        if (body.Length > 256)
        {
            try
            {
                byte[] deflated = Deflate(body);
                if (deflated.Length < body.Length)
                {
                    body = deflated;
                    flags = FlagDeflated;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Sync] couldn't compress a message, sent as it is: {e.Message}");
            }
        }

        var message = new byte[HeaderBytes + body.Length];
        message[0] = (byte)type;
        message[1] = flags;
        Buffer.BlockCopy(BitConverter.GetBytes(id), 0, message, 2, 4);
        Buffer.BlockCopy(body, 0, message, HeaderBytes, body.Length);
        return message;
    }

    static byte[] Slice(byte[] data, int offset)
    {
        var slice = new byte[data.Length - offset];
        Buffer.BlockCopy(data, offset, slice, 0, slice.Length);
        return slice;
    }

    static byte[] Deflate(byte[] data)
    {
        using (var output = new MemoryStream())
        {
            using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
                deflate.Write(data, 0, data.Length);
            return output.ToArray();
        }
    }

    static byte[] Inflate(byte[] data, int offset)
    {
        using (var input = new MemoryStream(data, offset, data.Length - offset))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }
}
