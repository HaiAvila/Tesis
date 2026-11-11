using System;
using System.Collections.Generic;
using UnityEngine;

// The live cues: the two MVP cues that react to where the actor actually is, instead of firing
// at the second the reference did something (CueEngine's timeline). They run on the machine with
// the pods during a take, from Acción, alongside the timeline, and need the actor's headset
// (where they are) - so not on a shoot day, where only the pods are worn. Off with the guide
// cues (Pistas de guía apagadas).
//
//   Proximity (MVP 1)    the sternum pulses (firmware PULSE) as the actor nears the next mark,
//                        faster the closer: from SlowGap at Range to FastGap at the mark. Within
//                        Arrive of it: Marca (DOUBLE) - when they really get there, so the
//                        timeline's own Marca at the reference's time is left out. The marks are
//                        the timeline's Marca cues (TakeComparison.MarksFromCues), the director's
//                        edits included. A mark the actor walks past (the reference has moved on
//                        well beyond it) is skipped without a Marca.
//   Orientation (MVP 2)  when the actor faces more than OffBy away from where the reference faced
//                        at the same point of the shot for Sustain seconds, the wrist on the side
//                        to turn clicks, every Repeat seconds, until they're within Back of it.
//                        Not near one of the timeline's own Giro clicks, which say "turn now";
//                        this is the correction. At most MaxClicks per correction, and none once
//                        the reference has ended (a longer take is past what it can say).
//
// "The same point of the shot" is found by position along the reference's path, not by time, so
// an actor who's late still gets the facing for where they are: the nearest reference sample to
// the actor among the next few seconds of the reference, the one nearest in time when several are
// about as close (turning on the spot).
//
// Facing: the body (the avatar's hips, from the chest pod) when this machine has the actor's
// avatar and the reference recorded one; otherwise the head, for both.
public class LiveCues
{
    const float StartDelay = 1.2f;      // after Acción (its TRIPLE plays on every pod)
    const float TickSeconds = 0.2f;

    const float Range = 2.5f;           // m: the pulse starts this close to the mark
    const float Arrive = 0.4f;          // m
    const float SlowGap = 0.8f;         // s between pulses at Range
    const float FastGap = 0.1f;         // s at the mark
    const float GapStep = 0.05f;        // the gap is resent only when it changes this much
    const float Resend = 1.5f;          // s: or this long after the last send (another cue on the sternum ends a PULSE)
    const float AfterArrival = 1.5f;    // s before pulsing toward the next mark
    const float PassedAfter = 4f;       // s of the reference past a mark: the actor walked past it

    const float OffBy = 35f;            // degrees (was 45; the turns detected from 30°, Nov 11)
    const float Back = 20f;
    const float Sustain = 1.5f;         // s
    const float Repeat = 1.5f;          // s
    const float NearTimelineTurn = 2f;  // s around a timeline Giro
    const int MaxClicks = 3;            // per correction: then it waits until the actor is back within Back
    const float ProgressLookAhead = 4f; // s of the reference searched ahead of the last match
    const float SameSpot = 0.15f;       // m: about as close

    readonly Action<int, PodReceiver.Cue, int, string> send;
    readonly List<TakeComparison.Point> reference;
    readonly List<TakeComparison.Mark> marks;
    readonly List<float> timelineTurns = new List<float>();
    readonly bool bodyFacing;
    readonly int shot;

    float startTime;
    float nextTick;
    int nextMark;
    bool pulsing;
    float sentGap;
    float pulseSentAt;
    float arrivedAt = -100f;
    int progress;
    float wrongSince = -1f;
    float lastTurnCue = -100f;
    bool correcting;
    int clicks;

    public int MarkCount => marks.Count;

    // Off while something else owns the sternum (the shot's heartbeat).
    public bool ProximityEnabled = true;

    LiveCues(int shot, List<TakeComparison.Point> reference, List<TakeComparison.Mark> marks, bool bodyFacing, Action<int, PodReceiver.Cue, int, string> send)
    {
        this.shot = shot;
        this.reference = reference;
        this.marks = marks;
        this.bodyFacing = bodyFacing;
        this.send = send;
    }

    // The shot's reference (its preferida) and marks; null when there's none to follow.
    public static LiveCues Load(ShotListDatabase database, int shot, List<CueEvent> cues, Action<int, PodReceiver.Cue, int, string> send)
    {
        RecordingSession preferida = database.GetSessionsForShot(shot).Find(s => s.EsPreferida && !s.IsScene);
        if (preferida == null)
            return null;
        List<TakeComparison.Point> points = TakeComparison.FromMetrics(database.GetMetricsForSession(preferida.Id));
        if (points.Count < 5)
            return null;
        List<TakeComparison.Mark> marks = TakeComparison.MarksFromCues(cues, points);

        // The body's facing over the reference, from its recorded avatar, if this machine has one
        // to compare it with.
        bool bodyFacing = false;
        AvatarPoseSync avatar = AvatarPoseSync.Instance;
        if (avatar != null && avatar.HasBody)
        {
            List<PoseSample> frames = database.GetPoseTrackForSession(preferida.Id);
            if (frames.Count > 10)
            {
                bodyFacing = true;
                for (int i = 0; i < points.Count; i++)
                {
                    TakeComparison.Point p = points[i];
                    p.Yaw = avatar.FrameBodyYaw(NearestFrame(frames, p.T).Datos);
                    points[i] = p;
                }
            }
        }

        var live = new LiveCues(shot, points, marks, bodyFacing, send);
        foreach (CueEvent cue in cues)
        {
            if (cue.Kind == CueKind.Orientation)
                live.timelineTurns.Add(cue.T);
        }
        Debug.Log($"[LiveCues] shot {shot}: reference take {preferida.Id}, {marks.Count} marks, facing from the {(bodyFacing ? "body" : "head")}");
        return live;
    }

    static PoseSample NearestFrame(List<PoseSample> frames, float t)
    {
        int lo = 0, hi = frames.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (frames[mid].TiempoRelativo <= t) lo = mid; else hi = mid;
        }
        return Mathf.Abs(frames[lo].TiempoRelativo - t) <= Mathf.Abs(frames[hi].TiempoRelativo - t) ? frames[lo] : frames[hi];
    }

    public void Begin(float now)
    {
        startTime = now;
        nextTick = now + StartDelay;
    }

    // position: the actor's head; bodyYaw: the avatar's facing (used if the reference's is the body's too).
    public void Update(float now, Vector3 head, float headYaw, bool hasBody, float bodyYaw)
    {
        if (now < nextTick)
            return;
        nextTick = now + TickSeconds;
        float elapsed = now - startTime;

        UpdateProgress(head, elapsed);
        if (ProximityEnabled)
            UpdateProximity(now, head);
        float yaw = bodyFacing ? (hasBody ? bodyYaw : float.NaN) : headYaw;
        if (!float.IsNaN(yaw))
            UpdateOrientation(now, elapsed, yaw);
    }

    public void End()
    {
        if (pulsing)
            send(PodIds.Sternum, PodReceiver.Cue.Stop, 0, "Proximidad: fin");
        pulsing = false;
    }

    // --- where in the shot the actor is ----------------------------------------------------------

    void UpdateProgress(Vector3 head, float elapsed)
    {
        float from = reference[progress].T - 0.5f;
        float to = reference[progress].T + ProgressLookAhead;
        float best = float.MaxValue;
        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i].T < from) continue;
            if (reference[i].T > to) break;
            best = Mathf.Min(best, Distance(head, reference[i]));
        }

        int chosen = progress;
        float chosenTimeGap = float.MaxValue;
        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i].T < from) continue;
            if (reference[i].T > to) break;
            if (Distance(head, reference[i]) > best + SameSpot) continue;
            float timeGap = Mathf.Abs(reference[i].T - elapsed);
            if (timeGap < chosenTimeGap)
            {
                chosen = i;
                chosenTimeGap = timeGap;
            }
        }
        progress = chosen;
    }

    static float Distance(Vector3 head, TakeComparison.Point p) => Vector2.Distance(new Vector2(head.x, head.z), new Vector2(p.X, p.Z));

    // --- proximity --------------------------------------------------------------------------------

    void UpdateProximity(float now, Vector3 head)
    {
        if (nextMark >= marks.Count)
            return;

        TakeComparison.Mark mark = marks[nextMark];
        float d = Vector2.Distance(new Vector2(head.x, head.z), new Vector2(mark.X, mark.Z));

        if (d <= Arrive)
        {
            send(PodIds.Sternum, PodReceiver.Cue.Double, 0, $"Marca {mark.Number}: llegó");
            pulsing = false;
            arrivedAt = now;
            nextMark++;
            return;
        }

        // Walked past it: the reference is well beyond it, and the actor isn't near it.
        if (reference[progress].T > mark.T + PassedAfter && d > Range * 0.5f)
        {
            if (pulsing)
                send(PodIds.Sternum, PodReceiver.Cue.Stop, 0, $"Marca {mark.Number}: se pasó");
            Debug.Log($"[LiveCues] shot {shot}: mark {mark.Number} passed at {d:F1} m without stopping");
            pulsing = false;
            nextMark++;
            return;
        }

        if (d > Range || now - arrivedAt < AfterArrival)
        {
            if (pulsing)
                send(PodIds.Sternum, PodReceiver.Cue.Stop, 0, "Proximidad: lejos");
            pulsing = false;
            return;
        }

        float gap = Mathf.Round(Mathf.Lerp(FastGap, SlowGap, Mathf.InverseLerp(Arrive, Range, d)) / GapStep) * GapStep;
        if (!pulsing || Mathf.Abs(gap - sentGap) >= GapStep - 0.001f || now - pulseSentAt >= Resend)
        {
            send(PodIds.Sternum, PodReceiver.Cue.Pulse, Mathf.Clamp(Mathf.RoundToInt(gap * 100f), 1, 255), $"Proximidad marca {mark.Number}: {d:F1} m");
            pulsing = true;
            sentGap = gap;
            pulseSentAt = now;
        }
    }

    // --- orientation ------------------------------------------------------------------------------

    void UpdateOrientation(float now, float elapsed, float yaw)
    {
        float expected = TakeComparison.MeanYaw(reference, reference[progress].T - 0.4f, reference[progress].T + 0.4f);
        float error = Mathf.DeltaAngle(yaw, expected);   // > 0: the actor should turn right
        float off = Mathf.Abs(error);

        bool nearTimelineTurn = timelineTurns.Exists(t => Mathf.Abs(t - elapsed) < NearTimelineTurn);
        bool referenceOver = elapsed > reference[reference.Count - 1].T;
        if (off < Back || nearTimelineTurn || referenceOver)
        {
            wrongSince = -1f;
            correcting = false;
            clicks = 0;
            return;
        }
        if (clicks >= MaxClicks)
            return;
        if (!correcting && off < OffBy)
        {
            wrongSince = -1f;
            return;
        }

        if (wrongSince < 0f)
            wrongSince = now;
        if (!correcting && now - wrongSince < Sustain)
            return;
        correcting = true;
        if (now - lastTurnCue < Repeat)
            return;

        lastTurnCue = now;
        clicks++;
        bool right = error > 0f;
        send(right ? PodIds.RightWrist : PodIds.LeftWrist, PodReceiver.Cue.Click, 0, $"Orientación: gira a la {(right ? "derecha" : "izquierda")} ({off:F0}°)");
    }
}
