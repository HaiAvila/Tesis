using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLite4Unity3d;
using UnityEngine;
using UnityEngine.Networking;

public class ShotListDatabase : MonoBehaviour
{
    const string DatabaseFileName = "shotlist.db";
    const string CsvFileName = "ShotListDB.csv";
    const string HeaderMarker = "scene_number";

    void Start()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            CreateShotsTable(connection);
            ImportCsv(connection);
            CreateSessionTables(connection);
            EnsureSesionesColumns(connection);
            CreateShotBoundariesTable(connection);
            CreateCameraFramingsTable(connection);
            EnsureCameraFramingsColumns(connection);
            CreateCueTrackTable(connection);
            CreateTakeEventsTable(connection);
        }

        GenerateMissingCueTracks();
        StartCoroutine(RegenerateIfGeneratorChanged());
    }

    const string GeneratorVersionKey = "CueGenerator.Version";

    // A newer CueGenerator makes every preferida's auto cues again - a moment after start, so
    // AvatarPoseSync (the avatar the body is read with) is up.
    System.Collections.IEnumerator RegenerateIfGeneratorChanged()
    {
        if (PlayerPrefs.GetInt(GeneratorVersionKey, 0) == CueGenerator.Version)
            yield break;
        for (int i = 0; i < 30 && AvatarPoseSync.Instance == null; i++)
            yield return null;
        yield return null;

        List<RecordingSession> preferidas;
        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
            preferidas = connection.Query<RecordingSession>("SELECT * FROM Sesiones WHERE es_preferida = 1 AND tipo = ?;", RecordingSession.TipoToma);
        Debug.Log($"[Cues] generator version {CueGenerator.Version}: regenerating {preferidas.Count} preferidas");
        foreach (RecordingSession session in preferidas)
            RegenerateAutoCues(session);
        PlayerPrefs.SetInt(GeneratorVersionKey, CueGenerator.Version);
        PlayerPrefs.Save();
    }

    // Each shot's haptic cue timeline (CueKind, pod, time). source "auto" rows come from the
    // shot's preferida take (CueGenerator) and are replaced whenever another take becomes the
    // preferida; "manual" rows (the timeline editor, later) are the director's and are kept.
    // session_guid: the take an auto row was made from. A brand new table.
    void CreateCueTrackTable(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS CueTrack (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scene_number INTEGER,
                shot_number INTEGER,
                session_guid TEXT,
                t REAL,
                dur REAL NOT NULL DEFAULT 0,
                cue INTEGER,
                pod INTEGER NOT NULL DEFAULT 0,
                param REAL NOT NULL DEFAULT 0,
                source TEXT NOT NULL DEFAULT 'auto'
            );");
        connection.Execute("CREATE INDEX IF NOT EXISTS CueTrack_shot_number ON CueTrack (shot_number);");
    }

    // Moments in a take, in its own seconds, by the take's GUID: evento "accion_set" = the
    // set's scripted action was triggered (SetAction), so the replay plays it back then.
    void CreateTakeEventsTable(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS EventosToma (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_guid TEXT,
                t REAL,
                evento TEXT
            );");
        connection.Execute("CREATE INDEX IF NOT EXISTS EventosToma_session_guid ON EventosToma (session_guid);");
    }

    public void SaveSetActions(string sessionGuid, IReadOnlyList<float> times)
    {
        var rows = new List<TakeEvent>();
        foreach (float t in times)
            rows.Add(new TakeEvent { SessionGuid = sessionGuid, T = t, Evento = TakeEvent.SetAction });
        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
            connection.InsertAll(rows);
        Debug.Log($"[SetAction] saved {rows.Count} triggers with take {sessionGuid}");
    }

    public List<float> GetSetActions(string sessionGuid)
    {
        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
        {
            var times = new List<float>();
            foreach (TakeEvent row in connection.Query<TakeEvent>("SELECT * FROM EventosToma WHERE session_guid = ? AND evento = ? ORDER BY t ASC;", sessionGuid, TakeEvent.SetAction))
                times.Add(row.T);
            return times;
        }
    }

    public List<CueEvent> GetCueTrack(int shotNumber)
    {
        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
        {
            return connection.Query<CueEvent>("SELECT * FROM CueTrack WHERE shot_number = ? ORDER BY t ASC;", shotNumber);
        }
    }

    // The shot's automatic cues, made again from this take (its preferida). Manual rows stay.
    public int RegenerateAutoCues(RecordingSession session)
    {
        if (session == null || session.IsScene)
            return 0;

        Shot shot = GetShot(session.ShotNumber);
        CueGenerator.ShotPlan plan = CueGenerator.ShotPlan.FromNotes(shot != null ? shot.Notes : null);
        BodyTrack body = BodyTrack.FromTake(GetPoseTrackForSession(session.Id), session.AvatarLayout);
        List<CueEvent> cues = CueGenerator.Generate(GetMetricsForSession(session.Id), session.Duracion, plan, body);
        foreach (CueEvent cue in cues)
        {
            cue.SceneNumber = session.SceneNumber;
            cue.ShotNumber = session.ShotNumber;
            cue.SessionGuid = session.SessionGuid;
            cue.Source = CueEvent.SourceAuto;
        }

        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
        {
            connection.RunInTransaction(() =>
            {
                connection.Execute("DELETE FROM CueTrack WHERE shot_number = ? AND source = ?;", session.ShotNumber, CueEvent.SourceAuto);
                connection.InsertAll(cues);
            });
        }

        Debug.Log($"[Cues] shot {session.ShotNumber}: {cues.Count} automatic cues from take {session.Id} ({session.SessionGuid}), {(body != null ? "with the body" : "head only")}, {plan}");
        return cues.Count;
    }

    // Preferida takes approved before cues existed (or on another device's copy of the
    // database) get their automatic timeline once.
    void GenerateMissingCueTracks()
    {
        List<RecordingSession> missing;
        using (var connection = new SQLiteConnection(Path.Combine(Application.persistentDataPath, DatabaseFileName)))
        {
            missing = connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE es_preferida = 1 AND tipo = ? AND shot_number NOT IN (SELECT shot_number FROM CueTrack WHERE source = ?);",
                RecordingSession.TipoToma, CueEvent.SourceAuto);
        }

        foreach (RecordingSession session in missing)
            RegenerateAutoCues(session);
    }

    // Where each shot starts and ends inside a full-scene recording, in the recording's own
    // seconds (the same clock as Metricas/CamaraTrack.tiempo_relativo). Keyed by the session's
    // GUID (Sesiones.session_id). source is "manual" (the director's Siguiente toma, or the
    // first shot opened at record start) or "auto" (automatic suggestions - not implemented;
    // see RehearsalRecorder.SuggestShotBoundariesAuto). A brand new table, so CREATE TABLE IF
    // NOT EXISTS is already safe for existing databases.
    void CreateShotBoundariesTable(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS ShotBoundaries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                shot_number INTEGER,
                t_in REAL,
                t_out REAL,
                source TEXT NOT NULL DEFAULT 'manual'
            );");

        connection.Execute("CREATE INDEX IF NOT EXISTS ShotBoundaries_session_id ON ShotBoundaries (session_id);");
    }

    public List<ShotBoundary> GetBoundariesForSession(string sessionGuid)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<ShotBoundary>(
                "SELECT * FROM ShotBoundaries WHERE session_id = ? ORDER BY t_in ASC;", sessionGuid);
        }
    }

    // Descartar. The boundaries are normally only in memory until Guardar, so this usually
    // finds nothing - it's here so a discarded session never leaves boundaries behind,
    // whatever wrote them.
    public void DeleteBoundaries(string sessionGuid)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            int deleted = connection.Execute("DELETE FROM ShotBoundaries WHERE session_id = ?;", sessionGuid);
            Debug.Log($"[Escena] Descartar: {deleted} boundary row(s) deleted for session {sessionGuid}");
        }
    }

    // A scene's shots in order - Siguiente toma walks through them.
    public List<int> GetShotNumbersOfScene(int sceneNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<Shot>(
                "SELECT * FROM Shots WHERE scene_number = ? ORDER BY shot_number ASC;", sceneNumber)
                .Select(shot => shot.ShotNumber).ToList();
        }
    }

    // Saved director-viewfinder framings. Nothing reads or writes this table anymore - the
    // viewfinder's save/load framing buttons were removed - but it's still created and
    // migrated so the schema stays the same and rows already saved in existing databases are
    // kept, not dropped.
    void CreateCameraFramingsTable(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS CameraFramings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                shot_number INTEGER,
                position_x REAL,
                position_y REAL,
                position_z REAL,
                rotation_x REAL,
                rotation_y REAL,
                rotation_z REAL,
                rotation_w REAL,
                fov REAL,
                timestamp TEXT
            );");
    }

    // The viewfinder's zoom switched from raw FOV to real focal-length vocabulary (named
    // lenses, mm display) after this table already shipped - focal_length_mm is the new
    // authoritative value to restore on load (fov is kept, still populated, as a legacy/
    // informational value only - round-tripping camera state through the derived FOV
    // instead of the driving mm value would lose precision). Same safe PRAGMA-checked
    // ALTER TABLE pattern as EnsureSesionesColumns, so older databases aren't broken.
    void EnsureCameraFramingsColumns(SQLiteConnection connection)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqliteColumnInfo column in connection.Query<SqliteColumnInfo>("PRAGMA table_info(CameraFramings);"))
            existingColumns.Add(column.name);

        if (!existingColumns.Contains("focal_length_mm"))
            connection.Execute("ALTER TABLE CameraFramings ADD COLUMN focal_length_mm REAL NOT NULL DEFAULT 50;");
    }

    // Sesiones predates es_preferida/es_favorita - real databases created before this
    // feature won't have these columns yet. Checked via PRAGMA table_info and only added
    // with ALTER TABLE when actually missing, so opening an older shotlist.db never breaks.
    void EnsureSesionesColumns(SQLiteConnection connection)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqliteColumnInfo column in connection.Query<SqliteColumnInfo>("PRAGMA table_info(Sesiones);"))
            existingColumns.Add(column.name);

        if (!existingColumns.Contains("es_preferida"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN es_preferida INTEGER NOT NULL DEFAULT 0;");

        if (!existingColumns.Contains("es_favorita"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN es_favorita INTEGER NOT NULL DEFAULT 0;");

        // Escena completa: a session is a single shot ("toma") or a whole scene ("escena",
        // shot_number = FullSceneShot), always with its scene. session_id is a GUID made at
        // record start, to match the session across devices (the server, later) - unlike id,
        // the local autoincrement that Metricas/CamaraTrack.sesion_id point to.
        if (!existingColumns.Contains("scene_number"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN scene_number INTEGER;");

        if (!existingColumns.Contains("tipo"))
            connection.Execute($"ALTER TABLE Sesiones ADD COLUMN tipo TEXT NOT NULL DEFAULT '{RecordingSession.TipoToma}';");

        if (!existingColumns.Contains("session_id"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN session_id TEXT;");

        // The avatar's size in the take (AvatarPoseSync's layout). Null without a pose track.
        if (!existingColumns.Contains("avatar_layout"))
            connection.Execute("ALTER TABLE Sesiones ADD COLUMN avatar_layout BLOB;");

        // Rows from before these columns: every one was a single shot (tipo already defaults
        // to "toma"), so its scene is the shot's; and each gets a GUID of its own. Only rows
        // still missing a value are touched, so this is a no-op on every later start.
        int scenesFilled = connection.Execute(
            "UPDATE Sesiones SET scene_number = (SELECT Shots.scene_number FROM Shots WHERE Shots.shot_number = Sesiones.shot_number) " +
            "WHERE scene_number IS NULL;");

        List<RecordingSession> withoutGuid = connection.Query<RecordingSession>("SELECT * FROM Sesiones WHERE session_id IS NULL;");
        if (withoutGuid.Count > 0)
        {
            connection.RunInTransaction(() =>
            {
                foreach (RecordingSession session in withoutGuid)
                    connection.Execute("UPDATE Sesiones SET session_id = ? WHERE id = ?;", Guid.NewGuid().ToString(), session.Id);
            });
        }

        if (scenesFilled > 0 || withoutGuid.Count > 0)
            Debug.Log($"[Escena] Sesiones migrated: scene_number filled on {scenesFilled} row(s), session_id on {withoutGuid.Count}");
    }

    // Creates the Sesiones/Metricas/CamaraTrack record for a finished rehearsal recording in
    // a single transaction: one Sesiones row (with es_preferida/es_favorita already set, if
    // requested, directly in the INSERT - never a follow-up UPDATE), every buffered Metricas
    // sample, and every buffered CamaraTrack sample. One transaction means camera rows can
    // never exist without their session. Marking the new session preferida unmarks any
    // other preferida session of the same shot first, in the same transaction.
    //
    // tipo is RecordingSession.TipoToma (a shot) or TipoEscena (the whole scene, shotNumber =
    // FullSceneShot). A scene recording is never preferida - approving one needs slicing it
    // into per-shot references first, which comes later - so esPreferida is ignored for it.
    // A scene recording's ShotBoundaries go in the same transaction, so they can never exist
    // without their session either.
    public int SaveRecordingSession(int sceneNumber, int shotNumber, string tipo, string sessionGuid, DateTime startedAt, float duration, List<Metric> samples, List<CameraTrackSample> cameraSamples, List<ShotBoundary> boundaries, bool esPreferida = false, bool esFavorita = false, List<PoseSample> poseFrames = null, byte[] avatarLayout = null)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        if (tipo == RecordingSession.TipoEscena)
            esPreferida = false;

        using (var connection = new SQLiteConnection(dbPath))
        {
            var session = new RecordingSession
            {
                ShotNumber = shotNumber,
                SceneNumber = sceneNumber,
                Tipo = tipo,
                SessionGuid = sessionGuid,
                FechaHora = startedAt.ToString("o"),
                Duracion = duration,
                EsPreferida = esPreferida,
                EsFavorita = esFavorita,
                AvatarLayout = avatarLayout
            };

            connection.RunInTransaction(() =>
            {
                if (esPreferida)
                    connection.Execute("UPDATE Sesiones SET es_preferida = 0 WHERE shot_number = ?;", shotNumber);

                connection.Insert(session);

                foreach (Metric sample in samples)
                    sample.SesionId = session.Id;

                connection.InsertAll(samples);

                foreach (CameraTrackSample cameraSample in cameraSamples)
                    cameraSample.SesionId = session.Id;

                connection.InsertAll(cameraSamples);

                if (poseFrames != null)
                {
                    foreach (PoseSample frame in poseFrames)
                        frame.SesionId = session.Id;

                    connection.InsertAll(poseFrames);
                }

                if (boundaries != null)
                {
                    foreach (ShotBoundary boundary in boundaries)
                        boundary.SessionGuid = sessionGuid;

                    connection.InsertAll(boundaries);
                }
            });

            int boundaryCount = boundaries != null ? boundaries.Count : 0;
            if (esPreferida)
                RegenerateAutoCues(session);

            Debug.Log($"{(tipo == RecordingSession.TipoEscena ? "[Escena] " : "")}ShotListDatabase: saved session {session.Id} ({tipo}, scene {sceneNumber}, shot {shotNumber}, guid {sessionGuid}, {samples.Count} metrics, {cameraSamples.Count} camera samples, {boundaryCount} shot boundaries, {duration:F1}s, preferida={esPreferida}, favorita={esFavorita}).");
            return session.Id;
        }
    }

    // Exclusive: unmarks any other preferida session of the same shot first, then marks
    // this one, in a single transaction.
    public void SetPreferida(int sessionId, int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.RunInTransaction(() =>
            {
                connection.Execute("UPDATE Sesiones SET es_preferida = 0 WHERE shot_number = ?;", shotNumber);
                connection.Execute("UPDATE Sesiones SET es_preferida = 1 WHERE id = ?;", sessionId);
            });
        }

        RegenerateAutoCues(GetSession(sessionId));
    }

    // Non-exclusive bookmark - marking or unmarking one session never touches any other.
    public void SetFavorita(int sessionId, bool esFavorita)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.Execute("UPDATE Sesiones SET es_favorita = ? WHERE id = ?;", esFavorita ? 1 : 0, sessionId);
        }
    }

    public SortedDictionary<int, List<Shot>> GetShotsGroupedByScene()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);
        var grouped = new SortedDictionary<int, List<Shot>>();

        using (var connection = new SQLiteConnection(dbPath))
        {
            List<Shot> shots = connection.Query<Shot>(
                "SELECT * FROM Shots ORDER BY scene_number, shot_number;");

            foreach (Shot shot in shots)
            {
                if (!grouped.TryGetValue(shot.SceneNumber, out List<Shot> sceneShots))
                {
                    sceneShots = new List<Shot>();
                    grouped[shot.SceneNumber] = sceneShots;
                }
                sceneShots.Add(shot);
            }
        }

        foreach (var scene in grouped)
        {
            Debug.Log($"Scene {scene.Key}: {scene.Value.Count} shots");
            foreach (Shot shot in scene.Value)
                Debug.Log($"  Shot {shot.ShotNumber}: {ShortenForLog(shot.Description)}");
        }

        return grouped;
    }

    // The whole scene starts where its first shot (lowest shot_number) starts.
    public int? GetFirstShotOfScene(int sceneNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            Shot shot = connection.Query<Shot>(
                "SELECT * FROM Shots WHERE scene_number = ? ORDER BY shot_number ASC LIMIT 1;", sceneNumber).FirstOrDefault();

            return shot != null ? shot.ShotNumber : (int?)null;
        }
    }

    public int? GetSceneNumberForShot(int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            Shot shot = connection.Query<Shot>(
                "SELECT * FROM Shots WHERE shot_number = ? LIMIT 1;", shotNumber).FirstOrDefault();

            return shot != null ? shot.SceneNumber : (int?)null;
        }
    }

    // Most recent recording for a shot (highest id - Sesiones has no unique timestamp
    // guarantee, but ids are assigned in insertion order). Null if none exists yet.
    public RecordingSession GetMostRecentSession(int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE shot_number = ? ORDER BY id DESC LIMIT 1;", shotNumber).FirstOrDefault();
        }
    }

    // All recorded sessions for a shot, preferida first (if any), then most recent first -
    // matches how the multi-session list in Inspección de Escenas should sort/highlight.
    public List<RecordingSession> GetSessionsForShot(int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE shot_number = ? ORDER BY es_preferida DESC, id DESC;", shotNumber);
        }
    }

    // A scene's full-scene recordings (tipo "escena"), most recent first. Kept apart from the
    // single-shot takes, which the shot queries above find by shot_number (scene recordings
    // have shot_number 0, which is never a shot).
    public List<RecordingSession> GetSceneSessions(int sceneNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE tipo = ? AND scene_number = ? ORDER BY id DESC;", RecordingSession.TipoEscena, sceneNumber);
        }
    }

    public RecordingSession GetSession(int sessionId)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE id = ? LIMIT 1;", sessionId).FirstOrDefault();
        }
    }

    // Every favorited session across every scene/shot, most recent first - for the
    // Favoritos screen.
    public List<RecordingSession> GetAllSessions()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>("SELECT * FROM Sesiones ORDER BY id ASC;");
        }
    }

    public List<RecordingSession> GetFavoriteSessions()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<RecordingSession>(
                "SELECT * FROM Sesiones WHERE es_favorita = 1 ORDER BY id DESC;");
        }
    }

    public Shot GetShot(int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<Shot>(
                "SELECT * FROM Shots WHERE shot_number = ? LIMIT 1;", shotNumber).FirstOrDefault();
        }
    }

    // Empty for sessions recorded before the camera track existed - playback treats that as
    // "ghost only".
    public List<CameraTrackSample> GetCameraTrackForSession(int sessionId)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<CameraTrackSample>(
                "SELECT * FROM CamaraTrack WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", sessionId);
        }
    }

    // What the scene list shows next to each shot: its takes, the latest one's date, whether one
    // is preferida, and its notes (those of its takes and its general ones). Keyed by
    // (scene, shot), shot 0 being the whole scene's recordings.
    public Dictionary<(int scene, int shot), TakeSummary> GetTakeSummaries()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);
        var result = new Dictionary<(int, int), TakeSummary>();

        using (var connection = new SQLiteConnection(dbPath))
        {
            foreach (SummaryRow row in connection.Query<SummaryRow>(
                "SELECT scene_number, shot_number, COUNT(*) AS takes, MAX(fecha_hora) AS latest, MAX(es_preferida) AS preferida FROM Sesiones GROUP BY scene_number, shot_number;"))
                result[(row.SceneNumber, row.ShotNumber)] = new TakeSummary { Takes = row.Takes, Latest = row.Latest, HasPreferida = row.Preferida != 0 };

            foreach (SummaryRow row in connection.Query<SummaryRow>(
                "SELECT scene_number, shot_number, COUNT(*) AS takes FROM NotasVoz GROUP BY scene_number, shot_number;"))
            {
                if (!result.TryGetValue((row.SceneNumber, row.ShotNumber), out TakeSummary summary))
                    result[(row.SceneNumber, row.ShotNumber)] = summary = new TakeSummary();
                summary.Notes = row.Takes;
            }
        }
        return result;
    }

    public void SaveVoiceNote(VoiceNote note)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.Insert(note);
        }
    }

    // A take's notes, by the moment they were said, then the general notes of its shot.
    public List<VoiceNote> GetVoiceNotes(string takeGuid, int sceneNumber, int shotNumber)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<VoiceNote>(
                "SELECT * FROM NotasVoz WHERE session_guid = ? OR (session_guid IS NULL AND scene_number = ? AND shot_number = ?) " +
                "ORDER BY session_guid IS NULL, t_toma, fecha_hora;",
                takeGuid ?? "", sceneNumber, shotNumber);
        }
    }

    public List<VoiceNote> GetVoiceNotesOfTake(string takeGuid)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<VoiceNote>("SELECT * FROM NotasVoz WHERE session_guid = ? ORDER BY t_toma, fecha_hora;", takeGuid);
        }
    }

    public List<VoiceNote> GetVoiceNotesWithoutTake()
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<VoiceNote>("SELECT * FROM NotasVoz WHERE session_guid IS NULL ORDER BY scene_number, shot_number, fecha_hora;");
        }
    }

    public void DeleteVoiceNote(VoiceNote note)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.Execute("DELETE FROM NotasVoz WHERE id = ?;", note.Id);
        }
        DeleteNoteFile(note);
    }

    static void DeleteNoteFile(VoiceNote note)
    {
        string path = Path.Combine(VoiceNoteRecorder.NotesFolder, note.Archivo);
        if (File.Exists(path))
            File.Delete(path);
    }

    // The take was discarded: its notes stay, as notes of the shot.
    public void DetachVoiceNotes(string takeGuid)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.Execute("UPDATE NotasVoz SET session_guid = NULL, t_toma = NULL WHERE session_guid = ?;", takeGuid);
        }
    }

    // A take and everything recorded with it, its notes included.
    public void DeleteSession(RecordingSession session)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);
        List<VoiceNote> notes = GetVoiceNotesOfTake(session.SessionGuid);

        using (var connection = new SQLiteConnection(dbPath))
        {
            connection.RunInTransaction(() =>
            {
                connection.Execute("DELETE FROM Metricas WHERE sesion_id = ?;", session.Id);
                connection.Execute("DELETE FROM CamaraTrack WHERE sesion_id = ?;", session.Id);
                connection.Execute("DELETE FROM PoseTrack WHERE sesion_id = ?;", session.Id);
                connection.Execute("DELETE FROM ShotBoundaries WHERE session_id = ?;", session.SessionGuid);
                connection.Execute("DELETE FROM NotasVoz WHERE session_guid = ?;", session.SessionGuid);
                connection.Execute("DELETE FROM CueTrack WHERE session_guid = ? AND source = ?;", session.SessionGuid, CueEvent.SourceAuto);
                connection.Execute("DELETE FROM EventosToma WHERE session_guid = ?;", session.SessionGuid);
                connection.Execute("DELETE FROM Sesiones WHERE id = ?;", session.Id);
            });
        }

        foreach (VoiceNote note in notes)
            DeleteNoteFile(note);
        TakeAudioRecorder.DeleteTracks(session.SessionGuid);

        Debug.Log($"ShotListDatabase: deleted session {session.Id} ({session.SessionGuid}) and its {notes.Count} notes");
    }

    // Empty for sessions recorded before the pose track existed, or without pods.
    public List<PoseSample> GetPoseTrackForSession(int sessionId)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<PoseSample>(
                "SELECT * FROM PoseTrack WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", sessionId);
        }
    }

    public List<Metric> GetMetricsForSession(int sessionId)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            return connection.Query<Metric>(
                "SELECT * FROM Metricas WHERE sesion_id = ? ORDER BY tiempo_relativo ASC;", sessionId);
        }
    }

    void CreateShotsTable(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Shots (
                shot_number INTEGER PRIMARY KEY,
                scene_number INTEGER,
                location TEXT,
                int_ext TEXT,
                day_night TEXT,
                take_number INTEGER,
                subject TEXT,
                description TEXT,
                status TEXT,
                notes TEXT
            );");
    }

    void CreateSessionTables(SQLiteConnection connection)
    {
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Sesiones (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                shot_number INTEGER,
                fecha_hora TEXT,
                duracion REAL
            );");

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Metricas (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sesion_id INTEGER,
                tiempo_relativo REAL,
                pos_x REAL,
                pos_y REAL,
                pos_z REAL,
                rot_x REAL,
                rot_y REAL,
                rot_z REAL,
                rot_w REAL,
                FOREIGN KEY (sesion_id) REFERENCES Sesiones(id)
            );");

        // Director's viewfinder camera, sampled on the exact same ticks (same tiempo_relativo)
        // as Metricas - one row per actor sample. A brand new table, so CREATE TABLE IF NOT
        // EXISTS is already safe for existing databases.
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS CamaraTrack (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sesion_id INTEGER,
                tiempo_relativo REAL,
                pos_x REAL,
                pos_y REAL,
                pos_z REAL,
                rot_x REAL,
                rot_y REAL,
                rot_z REAL,
                rot_w REAL,
                focal_mm REAL,
                visor_activo INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (sesion_id) REFERENCES Sesiones(id)
            );");

        // The actor's avatar during the take, from what this machine was showing: one frame per
        // row, on its own ticks (faster than Metricas). See AvatarPoseSync.TryCaptureFrame.
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS PoseTrack (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sesion_id INTEGER,
                tiempo_relativo REAL,
                datos BLOB,
                FOREIGN KEY (sesion_id) REFERENCES Sesiones(id)
            );");
        connection.Execute("CREATE INDEX IF NOT EXISTS PoseTrack_sesion_id ON PoseTrack (sesion_id);");

        // The director's spoken notes (the audio is a file in VoiceNoteRecorder.NotesFolder). By
        // the session's GUID, not its id: a note can be said while the take isn't saved yet.
        // session_guid NULL = a note of the shot in general; t_toma = the moment in the take.
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS NotasVoz (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_guid TEXT,
                scene_number INTEGER,
                shot_number INTEGER,
                t_toma REAL,
                fecha_hora TEXT,
                duracion REAL,
                archivo TEXT
            );");
        connection.Execute("CREATE INDEX IF NOT EXISTS NotasVoz_session_guid ON NotasVoz (session_guid);");
    }

    void ImportCsv(SQLiteConnection connection)
    {
        string csvPath = Path.Combine(Application.streamingAssetsPath, CsvFileName);
        string[] lines = ReadStreamingAssetLines(csvPath);
        if (lines == null)
        {
            Debug.LogError($"ShotListDatabase: CSV not found at {csvPath}");
            return;
        }

        Dictionary<string, int> columnIndex = FindHeaderRow(lines, out int headerLineIndex);
        if (columnIndex == null)
        {
            Debug.LogError($"ShotListDatabase: header row starting with '{HeaderMarker}' not found in {csvPath}");
            return;
        }

        int imported = 0;
        for (int i = headerLineIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            string[] fields = ParseCsvLine(lines[i]);
            int? shotNumber = ParseNullableInt(GetField(fields, columnIndex, "shot_number"));
            if (shotNumber == null)
                continue; // fila vacia/incompleta al final del CSV

            int sceneNumber = ParseNullableInt(GetField(fields, columnIndex, "scene_number")) ?? 0;
            string location = GetField(fields, columnIndex, "location");
            string intExt = GetField(fields, columnIndex, "int_ext");
            string dayNight = GetField(fields, columnIndex, "day_night");
            int? takeNumber = ParseNullableInt(GetField(fields, columnIndex, "take_number"));
            string subject = GetField(fields, columnIndex, "subject");
            string description = GetField(fields, columnIndex, "description");
            string status = GetField(fields, columnIndex, "status");
            string notes = GetField(fields, columnIndex, "notes");

            connection.Execute(
                "INSERT OR REPLACE INTO Shots " +
                "(shot_number, scene_number, location, int_ext, day_night, take_number, subject, description, status, notes) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                shotNumber.Value, sceneNumber, location, intExt, dayNight, takeNumber, subject, description, status, notes);

            imported++;
            Debug.Log($"Shot {shotNumber.Value}: {ShortenForLog(description)}");
        }

        Debug.Log($"ShotListDatabase: {imported} shots imported from {csvPath} into {connection.DatabasePath}");
    }

    static Dictionary<string, int> FindHeaderRow(string[] lines, out int headerLineIndex)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string[] fields = ParseCsvLine(lines[i]);
            if (fields.Length > 0 && fields[0].Trim() == HeaderMarker)
            {
                var columnIndex = new Dictionary<string, int>();
                for (int c = 0; c < fields.Length; c++)
                    columnIndex[fields[c].Trim()] = c;

                headerLineIndex = i;
                return columnIndex;
            }
        }

        headerLineIndex = -1;
        return null;
    }

    static string GetField(string[] fields, Dictionary<string, int> columnIndex, string column)
    {
        if (!columnIndex.TryGetValue(column, out int idx) || idx >= fields.Length)
            return string.Empty;
        return fields[idx].Trim();
    }

    static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return int.TryParse(value.Trim(), out int result) ? (int?)result : null;
    }

    static string ShortenForLog(string description)
    {
        const int maxLength = 60;
        if (string.IsNullOrEmpty(description) || description.Length <= maxLength)
            return description;
        return description.Substring(0, maxLength) + "...";
    }

    // Parser CSV simple que respeta comas dentro de comillas y comillas escapadas ("").
    // On Android (Quest) StreamingAssets lives inside the APK, so streamingAssetsPath is a
    // jar:file:// URL that System.IO can't open - File.Exists is always false there. Only
    // UnityWebRequest can read it. The wait is synchronous on purpose: the rest of Start()
    // and every other component's first query expect the shots to be in the DB already, and
    // reading a small file out of the local APK finishes almost immediately. On PC the path
    // is a plain folder and keeps using File.
    static string[] ReadStreamingAssetLines(string path)
    {
        if (!path.Contains("://"))
            return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : null;

        using (UnityWebRequest request = UnityWebRequest.Get(path))
        {
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone) { }

            if (request.result != UnityWebRequest.Result.Success)
                return null;

            // Same result as File.ReadAllLines(path, UTF8): a leading BOM dropped, split on
            // either line ending.
            string text = Encoding.UTF8.GetString(request.downloadHandler.data).TrimStart('﻿');
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }
    }

    static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

[Table("Shots")]
public class Shot
{
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("scene_number")] public int SceneNumber { get; set; }
    [Column("location")] public string Location { get; set; }
    [Column("int_ext")] public string IntExt { get; set; }
    [Column("day_night")] public string DayNight { get; set; }
    [Column("take_number")] public int? TakeNumber { get; set; }
    [Column("subject")] public string Subject { get; set; }
    [Column("description")] public string Description { get; set; }
    [Column("status")] public string Status { get; set; }
    [Column("notes")] public string Notes { get; set; }
}

[Table("Sesiones")]
public class RecordingSession
{
    public const string TipoToma = "toma";
    public const string TipoEscena = "escena";

    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("scene_number")] public int SceneNumber { get; set; }
    [Column("tipo")] public string Tipo { get; set; }
    // The cross-device GUID - not Id, the local key that Metricas/CamaraTrack.sesion_id use.
    [Column("session_id")] public string SessionGuid { get; set; }
    [Column("fecha_hora")] public string FechaHora { get; set; }
    [Column("duracion")] public float Duracion { get; set; }
    [Column("es_preferida")] public bool EsPreferida { get; set; }
    [Column("es_favorita")] public bool EsFavorita { get; set; }
    [Column("avatar_layout")] public byte[] AvatarLayout { get; set; }

    [Ignore] public bool IsScene => Tipo == TipoEscena;
}

// Maps a single row of "PRAGMA table_info(Sesiones);" - only the column name is needed,
// to check whether es_preferida/es_favorita already exist before altering the table.
class SqliteColumnInfo
{
    public string name { get; set; }
}

[Table("ShotBoundaries")]
public class ShotBoundary
{
    public const string SourceManual = "manual";
    public const string SourceAuto = "auto";

    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    // Sesiones.session_id (the GUID), not Sesiones.id.
    [Indexed, Column("session_id")] public string SessionGuid { get; set; }
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("t_in")] public float TIn { get; set; }
    // Set when the shot closes (the next Siguiente toma, or stop).
    [Column("t_out")] public float? TOut { get; set; }
    [Column("source")] public string Source { get; set; }
}

[Table("Metricas")]
public class Metric
{
    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Indexed, Column("sesion_id")] public int SesionId { get; set; }
    [Column("tiempo_relativo")] public float TiempoRelativo { get; set; }
    [Column("pos_x")] public float PosX { get; set; }
    [Column("pos_y")] public float PosY { get; set; }
    [Column("pos_z")] public float PosZ { get; set; }
    [Column("rot_x")] public float RotX { get; set; }
    [Column("rot_y")] public float RotY { get; set; }
    [Column("rot_z")] public float RotZ { get; set; }
    [Column("rot_w")] public float RotW { get; set; }
}

public class TakeSummary
{
    public int Takes;
    public string Latest;
    public bool HasPreferida;
    public int Notes;
}

// A row of GetTakeSummaries' queries.
class SummaryRow
{
    [Column("scene_number")] public int SceneNumber { get; set; }
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("takes")] public int Takes { get; set; }
    [Column("latest")] public string Latest { get; set; }
    [Column("preferida")] public int Preferida { get; set; }
}

[Table("NotasVoz")]
public class VoiceNote
{
    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Indexed, Column("session_guid")] public string SessionGuid { get; set; }
    [Column("scene_number")] public int SceneNumber { get; set; }
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("t_toma")] public float? TakeTime { get; set; }
    [Column("fecha_hora")] public string FechaHora { get; set; }
    [Column("duracion")] public float Duracion { get; set; }
    [Column("archivo")] public string Archivo { get; set; }
}

[Table("EventosToma")]
public class TakeEvent
{
    public const string SetAction = "accion_set";

    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Indexed, Column("session_guid")] public string SessionGuid { get; set; }
    [Column("t")] public float T { get; set; }
    [Column("evento")] public string Evento { get; set; }
}

[Table("CueTrack")]
public class CueEvent
{
    public const string SourceAuto = "auto";
    public const string SourceManual = "manual";

    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Column("scene_number")] public int SceneNumber { get; set; }
    [Indexed, Column("shot_number")] public int ShotNumber { get; set; }
    [Column("session_guid")] public string SessionGuid { get; set; }
    // Seconds from Acción.
    [Column("t")] public float T { get; set; }
    // 0 = a single cue; otherwise a span (Pasos: the walk).
    [Column("dur")] public float Dur { get; set; }
    // A CueKind.
    [Column("cue")] public int Cue { get; set; }
    // PodIds; 0 = every pod.
    [Column("pod")] public int Pod { get; set; }
    // Per kind: Pasos = steps per second; Giro = degrees (> 0 right).
    [Column("param")] public float Param { get; set; }
    [Column("source")] public string Source { get; set; }

    [Ignore] public CueKind Kind => (CueKind)Cue;
}

[Table("PoseTrack")]
public class PoseSample
{
    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Indexed, Column("sesion_id")] public int SesionId { get; set; }
    [Column("tiempo_relativo")] public float TiempoRelativo { get; set; }
    [Column("datos")] public byte[] Datos { get; set; }
}

[Table("CamaraTrack")]
public class CameraTrackSample
{
    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Indexed, Column("sesion_id")] public int SesionId { get; set; }
    [Column("tiempo_relativo")] public float TiempoRelativo { get; set; }
    [Column("pos_x")] public float PosX { get; set; }
    [Column("pos_y")] public float PosY { get; set; }
    [Column("pos_z")] public float PosZ { get; set; }
    [Column("rot_x")] public float RotX { get; set; }
    [Column("rot_y")] public float RotY { get; set; }
    [Column("rot_z")] public float RotZ { get; set; }
    [Column("rot_w")] public float RotW { get; set; }
    [Column("focal_mm")] public float FocalMM { get; set; }
    [Column("visor_activo")] public bool VisorActivo { get; set; }
}
