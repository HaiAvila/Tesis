using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// The takes on this machine as CSV files, for analysis in Python. Director only, from the pause
// menu in Inspección de Escenas. Each take gets its own folder under Exports, and one already
// exported is left alone, so exporting again only adds what's new:
//
//   sesion.csv   one row: scene, shot, type, date, duration, preferida, favorita, avatar scale
//   cabeza.csv   t + the actor's head (position, rotation), 10 Hz (Metricas)
//   camara.csv   t + the director's viewfinder (position, rotation, focal length, visor up)
//   cuerpo.csv   t + the avatar's root, head and 16 bones' local rotations, 20 Hz (PoseTrack)
//   tomas.csv    a full-scene take's shot boundaries
//   notas.csv    the director's voice notes, and their audio under notas/
//   eventos.csv  moments in the take: when the set's scripted action (scene 3's drone) started
//   pistas.csv   the preferida take's shot's haptic cue timeline (CueTrack): t, duration, cue
//                number and name, pod, parameter, auto/manual
//   conversacion_director.wav, conversacion_actor.wav   what each one said during the take
//
// Notes can be added after a take was exported, and the preferida (and so the cues) can change,
// so those are brought up to date every time.
// Notes of a shot in general, with no take, go in notas_sin_toma.
// A file with no rows isn't written. Decimal points, whatever the machine's language.
public static class TakeExporter
{
    public static string ExportRoot => Path.Combine(AppPaths.DataRoot, "Exports");

    public static int ExportAll(ShotListDatabase database, out int alreadyExported)
    {
        alreadyExported = 0;
        int exported = 0;
        Directory.CreateDirectory(ExportRoot);

        foreach (RecordingSession session in database.GetAllSessions())
        {
            string folder = Path.Combine(ExportRoot, FolderName(session));
            if (Directory.Exists(folder))
            {
                alreadyExported++;
                ExportNotes(database.GetVoiceNotesOfTake(session.SessionGuid), folder);
                ExportConversation(session, folder);
                ExportCues(database, session, folder);
                continue;
            }

            // Into place only once it's complete, so a failure can't look like an exported take.
            string temporary = folder + ".tmp";
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, true);
            Directory.CreateDirectory(temporary);

            Export(database, session, temporary);
            ExportNotes(database.GetVoiceNotesOfTake(session.SessionGuid), temporary);
            ExportConversation(session, temporary);
            ExportCues(database, session, temporary);
            Directory.Move(temporary, folder);
            exported++;
        }

        List<VoiceNote> withoutTake = database.GetVoiceNotesWithoutTake();
        if (withoutTake.Count > 0)
        {
            string folder = Path.Combine(ExportRoot, "notas_sin_toma");
            Directory.CreateDirectory(folder);
            ExportNotes(withoutTake, folder);
        }

        Debug.Log($"[Export] {exported} takes exported, {alreadyExported} already there, in {ExportRoot}");
        return exported;
    }

    static string FolderName(RecordingSession session)
    {
        string take = session.IsScene ? "completa" : $"shot{session.ShotNumber}";
        string date = DateTime.TryParse(session.FechaHora, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            : "sinfecha";
        return $"escena{session.SceneNumber}_{take}_{date}_{session.Id}";
    }

    static void Export(ShotListDatabase database, RecordingSession session, string folder)
    {
        float scale = session.AvatarLayout != null && session.AvatarLayout.Length >= 4 ? BitConverter.ToSingle(session.AvatarLayout, 0) : 0f;
        WriteFile(folder, "sesion.csv", "id,guid,escena,shot,tipo,fecha,duracion,preferida,favorita,escala_avatar", new[]
        {
            Row(session.Id, session.SessionGuid, session.SceneNumber, session.ShotNumber, session.Tipo, session.FechaHora,
                session.Duracion, session.EsPreferida ? 1 : 0, session.EsFavorita ? 1 : 0, scale),
        });

        var rows = new List<string>();
        foreach (Metric m in database.GetMetricsForSession(session.Id))
            rows.Add(Row(m.TiempoRelativo, m.PosX, m.PosY, m.PosZ, m.RotX, m.RotY, m.RotZ, m.RotW));
        WriteFile(folder, "cabeza.csv", "t,px,py,pz,qx,qy,qz,qw", rows);

        rows.Clear();
        foreach (CameraTrackSample c in database.GetCameraTrackForSession(session.Id))
            rows.Add(Row(c.TiempoRelativo, c.PosX, c.PosY, c.PosZ, c.RotX, c.RotY, c.RotZ, c.RotW, c.FocalMM, c.VisorActivo ? 1 : 0));
        WriteFile(folder, "camara.csv", "t,px,py,pz,qx,qy,qz,qw,focal_mm,visor_activo", rows);

        rows.Clear();
        foreach (PoseSample frame in database.GetPoseTrackForSession(session.Id))
            rows.Add(BodyRow(frame));
        WriteFile(folder, "cuerpo.csv", BodyHeader(), rows);

        rows.Clear();
        foreach (float t in database.GetSetActions(session.SessionGuid))
            rows.Add(Row(t, TakeEvent.SetAction));
        WriteFile(folder, "eventos.csv", "t,evento", rows);

        if (session.IsScene)
        {
            rows.Clear();
            foreach (ShotBoundary b in database.GetBoundariesForSession(session.SessionGuid))
                rows.Add(Row(b.ShotNumber, b.TIn, b.TOut.HasValue ? (object)b.TOut.Value : "", b.Source));
            WriteFile(folder, "tomas.csv", "shot,t_in,t_out,origen", rows);
        }
    }

    // Only in the preferida's folder: the cues belong to the shot and come from that take.
    static void ExportCues(ShotListDatabase database, RecordingSession session, string folder)
    {
        string path = Path.Combine(folder, "pistas.csv");
        if (File.Exists(path))
            File.Delete(path);
        if (!session.EsPreferida || session.IsScene)
            return;

        var rows = new List<string>();
        foreach (CueEvent cue in database.GetCueTrack(session.ShotNumber))
            rows.Add(Row(cue.T, cue.Dur, cue.Cue, cue.Kind.ToString(), cue.Pod, cue.Param, cue.Source));
        WriteFile(folder, "pistas.csv", "t,duracion,cue,nombre,pod,param,origen", rows);
    }

    static void ExportConversation(RecordingSession session, string folder)
    {
        foreach (string track in new[] { TakeAudioRecorder.DirectorTrack, TakeAudioRecorder.ActorTrack })
        {
            string source = TakeAudioRecorder.TrackPath(session.SessionGuid, track);
            string destination = Path.Combine(folder, $"conversacion_{track}.wav");
            if (File.Exists(source) && !File.Exists(destination))
                File.Copy(source, destination);
        }
    }

    static void ExportNotes(List<VoiceNote> notes, string folder)
    {
        if (notes.Count == 0)
            return;

        string audioFolder = Path.Combine(folder, "notas");
        Directory.CreateDirectory(audioFolder);

        var rows = new List<string>();
        foreach (VoiceNote note in notes)
        {
            rows.Add(Row(note.SceneNumber, note.ShotNumber, note.TakeTime.HasValue ? (object)note.TakeTime.Value : "", note.FechaHora, note.Duracion, note.Archivo));

            string source = Path.Combine(VoiceNoteRecorder.NotesFolder, note.Archivo);
            string destination = Path.Combine(audioFolder, note.Archivo);
            if (File.Exists(source) && !File.Exists(destination))
                File.Copy(source, destination);
        }
        WriteFile(folder, "notas.csv", "escena,shot,t_toma,fecha,duracion,archivo", rows);
    }

    static string BodyHeader()
    {
        var header = new StringBuilder("t,raiz_x,raiz_y,raiz_z,raiz_yaw,cabeza_qx,cabeza_qy,cabeza_qz,cabeza_qw");
        foreach (HumanBodyBones bone in AvatarPoseSync.SyncedBones)
            header.Append($",{bone}_qx,{bone}_qy,{bone}_qz,{bone}_qw");
        return header.ToString();
    }

    static string BodyRow(PoseSample frame)
    {
        AvatarPoseSync.ReadFrameRoot(frame.Datos, out Vector3 position, out float yaw, out Quaternion head);

        var values = new List<object> { frame.TiempoRelativo, position.x, position.y, position.z, yaw, head.x, head.y, head.z, head.w };
        for (int i = 0; i < AvatarPoseSync.SyncedBones.Length; i++)
        {
            Quaternion q = AvatarPoseSync.ReadFrameBone(frame.Datos, i);
            values.Add(q.x);
            values.Add(q.y);
            values.Add(q.z);
            values.Add(q.w);
        }
        return Row(values.ToArray());
    }

    static string Row(params object[] values)
    {
        var parts = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
            parts[i] = values[i] is float f ? f.ToString("0.#####", CultureInfo.InvariantCulture) : Convert.ToString(values[i], CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    static void WriteFile(string folder, string name, string header, IReadOnlyCollection<string> rows)
    {
        if (rows.Count == 0)
            return;

        using (var writer = new StreamWriter(Path.Combine(folder, name), false, new UTF8Encoding(false)))
        {
            writer.WriteLine(header);
            foreach (string row in rows)
                writer.WriteLine(row);
        }
    }
}
