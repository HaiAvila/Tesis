using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLite4Unity3d;
using UnityEngine;

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
        }
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
    }

    // Creates the Sesiones/Metricas record for a finished rehearsal recording in a single
    // transaction: one Sesiones row (with es_preferida/es_favorita already set, if
    // requested, directly in the INSERT - never a follow-up UPDATE) plus every buffered
    // Metricas sample. Marking the new session preferida unmarks any other preferida
    // session of the same shot first, in the same transaction.
    public int SaveRecordingSession(int shotNumber, DateTime startedAt, float duration, List<Metric> samples, bool esPreferida = false, bool esFavorita = false)
    {
        string dbPath = Path.Combine(Application.persistentDataPath, DatabaseFileName);

        using (var connection = new SQLiteConnection(dbPath))
        {
            var session = new RecordingSession
            {
                ShotNumber = shotNumber,
                FechaHora = startedAt.ToString("o"),
                Duracion = duration,
                EsPreferida = esPreferida,
                EsFavorita = esFavorita
            };

            connection.RunInTransaction(() =>
            {
                if (esPreferida)
                    connection.Execute("UPDATE Sesiones SET es_preferida = 0 WHERE shot_number = ?;", shotNumber);

                connection.Insert(session);

                foreach (Metric sample in samples)
                    sample.SesionId = session.Id;

                connection.InsertAll(samples);
            });

            Debug.Log($"ShotListDatabase: saved session {session.Id} (shot {shotNumber}, {samples.Count} metrics, {duration:F1}s, preferida={esPreferida}, favorita={esFavorita}).");
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
    }

    void ImportCsv(SQLiteConnection connection)
    {
        string csvPath = Path.Combine(Application.streamingAssetsPath, CsvFileName);
        if (!File.Exists(csvPath))
        {
            Debug.LogError($"ShotListDatabase: CSV not found at {csvPath}");
            return;
        }

        string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);

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
    [PrimaryKey, AutoIncrement, Column("id")] public int Id { get; set; }
    [Column("shot_number")] public int ShotNumber { get; set; }
    [Column("fecha_hora")] public string FechaHora { get; set; }
    [Column("duracion")] public float Duracion { get; set; }
    [Column("es_preferida")] public bool EsPreferida { get; set; }
    [Column("es_favorita")] public bool EsFavorita { get; set; }
}

// Maps a single row of "PRAGMA table_info(Sesiones);" - only the column name is needed,
// to check whether es_preferida/es_favorita already exist before altering the table.
class SqliteColumnInfo
{
    public string name { get; set; }
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
