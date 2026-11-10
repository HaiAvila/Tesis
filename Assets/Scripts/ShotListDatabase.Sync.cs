using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLite4Unity3d;
using UnityEngine;

// What TakeSync needs from the database to keep the devices' copies the same, with the server's
// as the store of record:
//
//   Sesiones.rev      when a take (or its preferida / favorita marks) last changed, on the device
//                     that changed it (UTC ticks). The newer one wins.
//   CueTrackRev       the same for each shot's cue timeline, which travels whole.
//   DeletedTakes      takes deleted on purpose, by GUID, so they're deleted everywhere instead of
//                     coming back from a device that still has them.
//
// Takes are matched across devices by their GUID (Sesiones.session_id), never by the local id.
// A take's recorded tracks never change after it's saved; only its marks do.
//
// Changes made here on this device raise Changed (TakeSync then sends them). The methods below
// that apply what another device sent never do, and never make up a stamp of their own: they
// store the one that came with the data.
public partial class ShotListDatabase
{
    // A take was saved, marked or deleted, or a cue timeline changed, on this device.
    public static event Action Changed;

    static void RaiseChanged() => Changed?.Invoke();

    static long lastRev;

    // UTC ticks, never the same twice on this device.
    static long NewRev()
    {
        long now = DateTime.UtcNow.Ticks;
        if (now <= lastRev)
            now = lastRev + 1;
        lastRev = now;
        return now;
    }

    void CreateSyncTables(SQLiteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqliteColumnInfo column in connection.Query<SqliteColumnInfo>("PRAGMA table_info(Sesiones);"))
            columns.Add(column.name);
        if (!columns.Contains("rev"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN rev INTEGER NOT NULL DEFAULT 0;");

        connection.Execute("CREATE TABLE IF NOT EXISTS CueTrackRev (shot_number INTEGER PRIMARY KEY, rev INTEGER NOT NULL DEFAULT 0);");
        connection.Execute("CREATE TABLE IF NOT EXISTS DeletedTakes (session_guid TEXT PRIMARY KEY, deleted INTEGER NOT NULL DEFAULT 0);");
    }

    // The shot's cue timeline changed on this device.
    static void TouchCues(SQLiteConnection connection, int shotNumber)
    {
        connection.Execute("INSERT OR REPLACE INTO CueTrackRev (shot_number, rev) VALUES (?, ?);", shotNumber, NewRev());
    }

    // --- what this device has -----------------------------------------------------------------

    public class TakeStamp
    {
        [Column("session_id")] public string Guid { get; set; }
        [Column("rev")] public long Rev { get; set; }
        [Column("scene_number")] public int SceneNumber { get; set; }
        [Column("shot_number")] public int ShotNumber { get; set; }
        [Column("tipo")] public string Tipo { get; set; }
        [Column("fecha_hora")] public string FechaHora { get; set; }
        [Column("es_preferida")] public bool EsPreferida { get; set; }
        [Column("es_favorita")] public bool EsFavorita { get; set; }
    }

    public class CueStamp
    {
        public int ShotNumber;
        public long Rev;
        public int Count;
        public int Hash;
        // The take the automatic cues were made from ("" if there are none).
        public string AutoGuid = "";
    }

    class DeletedRow
    {
        [Column("session_guid")] public string Guid { get; set; }
        [Column("deleted")] public long Deleted { get; set; }
    }

    class CueRevRow
    {
        [Column("shot_number")] public int ShotNumber { get; set; }
        [Column("rev")] public long Rev { get; set; }
    }

    public List<TakeStamp> GetTakeStamps()
    {
        using (var connection = new SQLiteConnection(DbPath))
            return connection.Query<TakeStamp>("SELECT session_id, rev, scene_number, shot_number, tipo, fecha_hora, es_preferida, es_favorita FROM Sesiones WHERE session_id IS NOT NULL ORDER BY id ASC;");
    }

    public Dictionary<string, long> GetDeletedTakes()
    {
        var result = new Dictionary<string, long>();
        using (var connection = new SQLiteConnection(DbPath))
        {
            foreach (DeletedRow row in connection.Query<DeletedRow>("SELECT * FROM DeletedTakes;"))
                result[row.Guid] = row.Deleted;
        }
        return result;
    }

    public int CountTakes()
    {
        using (var connection = new SQLiteConnection(DbPath))
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Sesiones;");
    }

    // Every shot with cue rows or a stamp. The hash is of the rows themselves, ids aside, so two
    // devices with the same timeline get the same one.
    public Dictionary<int, CueStamp> GetCueStamps()
    {
        var result = new Dictionary<int, CueStamp>();
        using (var connection = new SQLiteConnection(DbPath))
        {
            foreach (CueRevRow row in connection.Query<CueRevRow>("SELECT * FROM CueTrackRev;"))
                result[row.ShotNumber] = new CueStamp { ShotNumber = row.ShotNumber, Rev = row.Rev };

            foreach (IGrouping<int, CueEvent> shot in connection.Query<CueEvent>("SELECT * FROM CueTrack;").GroupBy(cue => cue.ShotNumber))
            {
                if (!result.TryGetValue(shot.Key, out CueStamp stamp))
                    result[shot.Key] = stamp = new CueStamp { ShotNumber = shot.Key };
                List<CueEvent> rows = SortedCues(shot);
                stamp.Count = rows.Count;
                stamp.Hash = HashCues(rows);
                CueEvent auto = rows.Find(cue => cue.Source == CueEvent.SourceAuto);
                stamp.AutoGuid = auto != null ? auto.SessionGuid ?? "" : "";
            }
        }
        return result;
    }

    static List<CueEvent> SortedCues(IEnumerable<CueEvent> cues) =>
        cues.OrderBy(cue => cue.T).ThenBy(cue => cue.Cue).ThenBy(cue => cue.Pod).ThenBy(cue => cue.Source, StringComparer.Ordinal).ThenBy(cue => cue.Dur).ToList();

    static int HashCues(List<CueEvent> rows)
    {
        unchecked
        {
            uint hash = 2166136261;
            void Mix(int value)
            {
                for (int shift = 0; shift < 32; shift += 8)
                    hash = (hash ^ (uint)((value >> shift) & 0xFF)) * 16777619;
            }
            foreach (CueEvent cue in rows)
            {
                Mix(Mathf.RoundToInt(cue.T * 1000f));
                Mix(Mathf.RoundToInt(cue.Dur * 1000f));
                Mix(cue.Cue);
                Mix(cue.Pod);
                Mix(Mathf.RoundToInt(cue.Param * 100f));
                foreach (char c in cue.Source ?? "")
                    Mix(c);
                foreach (char c in cue.SessionGuid ?? "")
                    Mix(c);
            }
            return (int)hash;
        }
    }

    // --- a whole take, to send and to receive ---------------------------------------------------

    const int TakeFormat = 1;

    // The take with everything recorded with it (its audio and voice notes aside), or null if
    // this device doesn't have it.
    public byte[] ExportTake(string guid)
    {
        using (var connection = new SQLiteConnection(DbPath))
        {
            RecordingSession session = connection.Query<RecordingSession>("SELECT * FROM Sesiones WHERE session_id = ? LIMIT 1;", guid).FirstOrDefault();
            if (session == null)
                return null;

            List<Metric> metrics = connection.Query<Metric>("SELECT * FROM Metricas WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", session.Id);
            List<CameraTrackSample> camera = connection.Query<CameraTrackSample>("SELECT * FROM CamaraTrack WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", session.Id);
            List<PoseSample> pose = connection.Query<PoseSample>("SELECT * FROM PoseTrack WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", session.Id);
            List<ShotBoundary> boundaries = connection.Query<ShotBoundary>("SELECT * FROM ShotBoundaries WHERE session_id = ? ORDER BY t_in ASC;", guid);
            List<TakeEvent> events = connection.Query<TakeEvent>("SELECT * FROM EventosToma WHERE session_guid = ? ORDER BY t ASC;", guid);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(TakeFormat);
                writer.Write(session.SessionGuid);
                writer.Write(session.SceneNumber);
                writer.Write(session.ShotNumber);
                writer.Write(session.Tipo ?? RecordingSession.TipoToma);
                writer.Write(session.FechaHora ?? "");
                writer.Write(session.Duracion);
                writer.Write(session.EsPreferida);
                writer.Write(session.EsFavorita);
                writer.Write(session.Rev);
                WriteBytes(writer, session.AvatarLayout);

                writer.Write(metrics.Count);
                foreach (Metric m in metrics)
                {
                    writer.Write(m.TiempoRelativo);
                    writer.Write(m.PosX); writer.Write(m.PosY); writer.Write(m.PosZ);
                    writer.Write(m.RotX); writer.Write(m.RotY); writer.Write(m.RotZ); writer.Write(m.RotW);
                }

                writer.Write(camera.Count);
                foreach (CameraTrackSample c in camera)
                {
                    writer.Write(c.TiempoRelativo);
                    writer.Write(c.PosX); writer.Write(c.PosY); writer.Write(c.PosZ);
                    writer.Write(c.RotX); writer.Write(c.RotY); writer.Write(c.RotZ); writer.Write(c.RotW);
                    writer.Write(c.FocalMM);
                    writer.Write(c.VisorActivo);
                }

                writer.Write(pose.Count);
                foreach (PoseSample p in pose)
                {
                    writer.Write(p.TiempoRelativo);
                    WriteBytes(writer, p.Datos);
                }

                writer.Write(boundaries.Count);
                foreach (ShotBoundary b in boundaries)
                {
                    writer.Write(b.ShotNumber);
                    writer.Write(b.TIn);
                    writer.Write(b.TOut.HasValue);
                    writer.Write(b.TOut ?? 0f);
                    writer.Write(b.Source ?? ShotBoundary.SourceManual);
                }

                writer.Write(events.Count);
                foreach (TakeEvent e in events)
                {
                    writer.Write(e.T);
                    writer.Write(e.Evento ?? "");
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }

    public enum ImportResult { Added, AlreadyHere, Deleted, Unreadable }

    // A take another device sent. Added only if this device doesn't have it and never deleted
    // it. Its preferida mark is kept only if it beats the shot's preferida here (a newer stamp;
    // with equal stamps, the take recorded later) - the loser is unmarked, with no new stamp.
    public ImportResult ImportTake(byte[] data)
    {
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                if (reader.ReadInt32() != TakeFormat)
                    return ImportResult.Unreadable;

                var session = new RecordingSession
                {
                    SessionGuid = reader.ReadString(),
                    SceneNumber = reader.ReadInt32(),
                    ShotNumber = reader.ReadInt32(),
                    Tipo = reader.ReadString(),
                    FechaHora = reader.ReadString(),
                    Duracion = reader.ReadSingle(),
                    EsPreferida = reader.ReadBoolean(),
                    EsFavorita = reader.ReadBoolean(),
                    Rev = reader.ReadInt64(),
                    AvatarLayout = ReadBytes(reader),
                };

                var metrics = new List<Metric>(reader.ReadInt32());
                for (int i = metrics.Capacity; i > 0; i--)
                    metrics.Add(new Metric
                    {
                        TiempoRelativo = reader.ReadSingle(),
                        PosX = reader.ReadSingle(), PosY = reader.ReadSingle(), PosZ = reader.ReadSingle(),
                        RotX = reader.ReadSingle(), RotY = reader.ReadSingle(), RotZ = reader.ReadSingle(), RotW = reader.ReadSingle(),
                    });

                var camera = new List<CameraTrackSample>(reader.ReadInt32());
                for (int i = camera.Capacity; i > 0; i--)
                    camera.Add(new CameraTrackSample
                    {
                        TiempoRelativo = reader.ReadSingle(),
                        PosX = reader.ReadSingle(), PosY = reader.ReadSingle(), PosZ = reader.ReadSingle(),
                        RotX = reader.ReadSingle(), RotY = reader.ReadSingle(), RotZ = reader.ReadSingle(), RotW = reader.ReadSingle(),
                        FocalMM = reader.ReadSingle(),
                        VisorActivo = reader.ReadBoolean(),
                    });

                var pose = new List<PoseSample>(reader.ReadInt32());
                for (int i = pose.Capacity; i > 0; i--)
                    pose.Add(new PoseSample { TiempoRelativo = reader.ReadSingle(), Datos = ReadBytes(reader) });

                var boundaries = new List<ShotBoundary>(reader.ReadInt32());
                for (int i = boundaries.Capacity; i > 0; i--)
                {
                    var boundary = new ShotBoundary { SessionGuid = session.SessionGuid, ShotNumber = reader.ReadInt32(), TIn = reader.ReadSingle() };
                    bool hasOut = reader.ReadBoolean();
                    float tOut = reader.ReadSingle();
                    boundary.TOut = hasOut ? tOut : (float?)null;
                    boundary.Source = reader.ReadString();
                    boundaries.Add(boundary);
                }

                var events = new List<TakeEvent>(reader.ReadInt32());
                for (int i = events.Capacity; i > 0; i--)
                    events.Add(new TakeEvent { SessionGuid = session.SessionGuid, T = reader.ReadSingle(), Evento = reader.ReadString() });

                if (string.IsNullOrEmpty(session.SessionGuid))
                    return ImportResult.Unreadable;

                using (var connection = new SQLiteConnection(DbPath))
                {
                    if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM DeletedTakes WHERE session_guid = ?;", session.SessionGuid) > 0)
                        return ImportResult.Deleted;
                    if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Sesiones WHERE session_id = ?;", session.SessionGuid) > 0)
                        return ImportResult.AlreadyHere;

                    if (session.IsScene)
                        session.EsPreferida = false;

                    connection.RunInTransaction(() =>
                    {
                        if (session.EsPreferida)
                            session.EsPreferida = TakePreferidaFrom(connection, session);

                        connection.Insert(session);
                        foreach (Metric m in metrics) m.SesionId = session.Id;
                        foreach (CameraTrackSample c in camera) c.SesionId = session.Id;
                        foreach (PoseSample p in pose) p.SesionId = session.Id;
                        connection.InsertAll(metrics);
                        connection.InsertAll(camera);
                        connection.InsertAll(pose);
                        connection.InsertAll(boundaries);
                        connection.InsertAll(events);
                    });
                }

                Debug.Log($"[Sync] take received: scene {session.SceneNumber}, shot {session.ShotNumber}, {session.Duracion:F1} s, {metrics.Count} head samples, {pose.Count} avatar frames ({session.SessionGuid})");
                return ImportResult.Added;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sync] a received take couldn't be read: {e.Message}");
            return ImportResult.Unreadable;
        }
    }

    // The incoming take wants to be its shot's preferida. True if it gets it: the shot has none
    // here, or the incoming one is newer (by stamp; then by when it was recorded). The one it
    // replaces is unmarked, its stamp untouched.
    static bool TakePreferidaFrom(SQLiteConnection connection, RecordingSession incoming)
    {
        RecordingSession current = connection.Query<RecordingSession>(
            "SELECT * FROM Sesiones WHERE shot_number = ? AND es_preferida = 1 AND tipo = ? LIMIT 1;", incoming.ShotNumber, RecordingSession.TipoToma).FirstOrDefault();
        if (current == null)
            return true;

        bool wins = incoming.Rev != current.Rev
            ? incoming.Rev > current.Rev
            : string.CompareOrdinal(incoming.FechaHora ?? "", current.FechaHora ?? "") > 0;
        if (wins)
            connection.Execute("UPDATE Sesiones SET es_preferida = 0 WHERE shot_number = ? AND es_preferida = 1;", incoming.ShotNumber);
        return wins;
    }

    // Another device's marks for a take this one has, with their stamp. Applied only if the
    // stamp is newer than this device's, or equal with force (the server's copy wins a tie).
    // True if anything changed.
    public bool ApplyTakeMarks(string guid, long rev, bool preferida, bool favorita, bool force)
    {
        using (var connection = new SQLiteConnection(DbPath))
        {
            RecordingSession session = connection.Query<RecordingSession>("SELECT * FROM Sesiones WHERE session_id = ? LIMIT 1;", guid).FirstOrDefault();
            if (session == null)
                return false;
            if (rev < session.Rev || (rev == session.Rev && !force))
                return false;
            if (rev == session.Rev && session.EsPreferida == preferida && session.EsFavorita == favorita)
                return false;

            if (session.IsScene)
                preferida = false;

            connection.RunInTransaction(() =>
            {
                if (preferida && !session.EsPreferida)
                    connection.Execute("UPDATE Sesiones SET es_preferida = 0 WHERE shot_number = ? AND es_preferida = 1;", session.ShotNumber);
                connection.Execute("UPDATE Sesiones SET es_preferida = ?, es_favorita = ?, rev = ? WHERE id = ?;", preferida ? 1 : 0, favorita ? 1 : 0, rev, session.Id);
            });
            Debug.Log($"[Sync] marks received for shot {session.ShotNumber}'s take {guid}: preferida={preferida}, favorita={favorita}");
            return true;
        }
    }

    // A take deleted on another device. True if it was here.
    public bool DeleteTakeFromSync(string guid, long deletedAt)
    {
        RecordingSession session;
        using (var connection = new SQLiteConnection(DbPath))
        {
            session = connection.Query<RecordingSession>("SELECT * FROM Sesiones WHERE session_id = ? LIMIT 1;", guid).FirstOrDefault();
            if (session == null)
            {
                connection.Execute("INSERT OR REPLACE INTO DeletedTakes (session_guid, deleted) VALUES (?, ?);", guid, deletedAt);
                return false;
            }
        }

        DeleteSessionRows(session, deletedAt, touchCues: false);
        return true;
    }

    // --- a shot's cue timeline, whole -----------------------------------------------------------

    public byte[] ExportCues(int shotNumber)
    {
        using (var connection = new SQLiteConnection(DbPath))
        {
            List<CueEvent> rows = SortedCues(connection.Query<CueEvent>("SELECT * FROM CueTrack WHERE shot_number = ?;", shotNumber));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(rows.Count);
                foreach (CueEvent cue in rows)
                {
                    writer.Write(cue.SceneNumber);
                    writer.Write(cue.SessionGuid ?? "");
                    writer.Write(cue.T);
                    writer.Write(cue.Dur);
                    writer.Write(cue.Cue);
                    writer.Write(cue.Pod);
                    writer.Write(cue.Param);
                    writer.Write(cue.Source ?? CueEvent.SourceAuto);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }
    }

    // Replaces the shot's timeline with another device's, stamp included. False if unreadable.
    public bool ImportCues(int shotNumber, long rev, byte[] data)
    {
        var rows = new List<CueEvent>();
        try
        {
            using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                for (int i = reader.ReadInt32(); i > 0; i--)
                    rows.Add(new CueEvent
                    {
                        ShotNumber = shotNumber,
                        SceneNumber = reader.ReadInt32(),
                        SessionGuid = reader.ReadString(),
                        T = reader.ReadSingle(),
                        Dur = reader.ReadSingle(),
                        Cue = reader.ReadInt32(),
                        Pod = reader.ReadInt32(),
                        Param = reader.ReadSingle(),
                        Source = reader.ReadString(),
                    });
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Sync] shot {shotNumber}'s received cues couldn't be read: {e.Message}");
            return false;
        }

        using (var connection = new SQLiteConnection(DbPath))
        {
            connection.RunInTransaction(() =>
            {
                connection.Execute("DELETE FROM CueTrack WHERE shot_number = ?;", shotNumber);
                connection.InsertAll(rows);
                connection.Execute("INSERT OR REPLACE INTO CueTrackRev (shot_number, rev) VALUES (?, ?);", shotNumber, rev);
            });
        }
        Debug.Log($"[Sync] shot {shotNumber}: cue timeline received ({rows.Count} rows)");
        return true;
    }

    static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes != null ? bytes.Length : -1);
        if (bytes != null)
            writer.Write(bytes);
    }

    static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length < 0 ? null : reader.ReadBytes(length);
    }
}
