using System.Collections.Generic;
using System.IO;
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
