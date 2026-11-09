using System.Collections.Generic;
using UnityEngine;

// Builds a take's automatic cue timeline from the approved reference (the preferida take):
// what the actor did there becomes what the pods tell them next time. Works from the headset
// track alone (Metricas, ~10 Hz), so every take has it, pods or not.
//
//   start            -> Acción (CameraStart), every pod
//   end              -> Corte (Cut), every pod
//   a walk           -> Pasos (Timing): a segment with a cadence; the engine alternates the shins
//   a stop after one -> Marca (Checkpoint) on arrival, Aviso (Anticipation) before it, sternum
//   a turn           -> Giro (Orientation) on the wrist of that side, a little before it starts
//
// The thresholds were tuned on the first three preferida takes (Nov 8, 2026): the head track
// is full of small shuffles and looking around, so walks are merged and must cover a metre,
// and a turn only counts if the new facing is held. Placeholder values, to tune by feel with
// the pods on.
public static class CueGenerator
{
    // Smoothing windows, seconds.
    const float PositionWindow = 0.6f;
    const float SpeedWindow = 0.6f;
    const float YawWindow = 1f;

    // Walking: hysteresis on the smoothed horizontal speed, m/s.
    const float WalkStartSpeed = 0.3f;
    const float WalkStopSpeed = 0.15f;
    const float WalkMergeGap = 0.8f;      // shorter pauses are part of the same walk
    const float MinWalkDistance = 1f;     // metres; less is shuffling, not walking

    // Marks: a stop at least this long after a walk.
    const float MinStopSeconds = 1f;
    const float AnticipationLead = 1.5f;  // the alert comes this long before arrival

    // Turns: a facing change of at least TurnDegrees within TurnWindow, then held.
    const float TurnDegrees = 45f;
    const float TurnWindow = 3f;
    const float TurnSettleRate = 25f;     // deg/s below which the turn has ended
    const float TurnHoldSeconds = 1.5f;
    const float TurnHoldTolerance = 25f;
    const float TurnMergeGap = 1f;        // back-to-back turns the same way are one turn
    const float TurnLead = 0.5f;          // the cue comes this long before the turn starts

    // Walking cadence from speed, steps per second: about 1.65 at 1 m/s. An approximation
    // until the shin pods' own steps are used.
    const float CadenceAtRest = 1.1f;
    const float CadencePerSpeed = 0.55f;
    const float MinCadence = 1.2f;
    const float MaxCadence = 2.3f;

    public struct Walk
    {
        public float Start, End, Distance;
        public float Speed => Distance / Mathf.Max(End - Start, 0.01f);
    }

    public struct Turn
    {
        public float Start, End, Degrees; // Degrees > 0 = to the right
    }

    public static List<CueEvent> Generate(List<Metric> samples, float duration)
    {
        var cues = new List<CueEvent>();
        if (samples == null || samples.Count < 2)
            return cues;

        duration = Mathf.Max(duration, samples[samples.Count - 1].TiempoRelativo);
        cues.Add(Cue(CueKind.CameraStart, 0f, PodIds.All));

        List<Walk> walks = FindWalks(samples);
        for (int i = 0; i < walks.Count; i++)
        {
            Walk walk = walks[i];
            float cadence = Mathf.Clamp(CadenceAtRest + CadencePerSpeed * walk.Speed, MinCadence, MaxCadence);
            cues.Add(new CueEvent { T = walk.Start, Dur = walk.End - walk.Start, Cue = (int)CueKind.Timing, Pod = PodIds.All, Param = cadence });

            float nextStart = i + 1 < walks.Count ? walks[i + 1].Start : duration;
            if (nextStart - walk.End >= MinStopSeconds)
            {
                cues.Add(Cue(CueKind.Anticipation, Mathf.Max(walk.Start, walk.End - AnticipationLead), PodIds.Sternum));
                cues.Add(Cue(CueKind.Checkpoint, walk.End, PodIds.Sternum));
            }
        }

        foreach (Turn turn in FindTurns(samples))
        {
            CueEvent cue = Cue(CueKind.Orientation, Mathf.Max(0f, turn.Start - TurnLead), turn.Degrees > 0f ? PodIds.RightWrist : PodIds.LeftWrist);
            cue.Param = turn.Degrees;
            cues.Add(cue);
        }

        cues.Add(Cue(CueKind.Cut, duration, PodIds.All));
        cues.Sort((a, b) => a.T.CompareTo(b.T));
        return cues;
    }

    static CueEvent Cue(CueKind kind, float t, int pod) =>
        new CueEvent { T = t, Dur = 0f, Cue = (int)kind, Pod = pod, Param = 0f };

    public static List<Walk> FindWalks(List<Metric> samples)
    {
        int n = samples.Count;
        float[] t = Times(samples);
        float[] x = Smooth(Values(samples, s => s.PosX), t, PositionWindow);
        float[] z = Smooth(Values(samples, s => s.PosZ), t, PositionWindow);

        var speed = new float[n];
        for (int i = 1; i < n; i++)
            speed[i] = Mathf.Sqrt((x[i] - x[i - 1]) * (x[i] - x[i - 1]) + (z[i] - z[i - 1]) * (z[i] - z[i - 1])) / Mathf.Max(t[i] - t[i - 1], 0.001f);
        speed = Smooth(speed, t, SpeedWindow);

        // Moving spans by index, with hysteresis, then short pauses merged.
        var spans = new List<Vector2Int>();
        bool moving = false;
        int from = 0;
        for (int i = 0; i < n; i++)
        {
            if (!moving && speed[i] > WalkStartSpeed) { moving = true; from = i; }
            else if (moving && speed[i] < WalkStopSpeed) { moving = false; spans.Add(new Vector2Int(from, i)); }
        }
        if (moving)
            spans.Add(new Vector2Int(from, n - 1));

        var merged = new List<Vector2Int>();
        foreach (Vector2Int span in spans)
        {
            if (merged.Count > 0 && t[span.x] - t[merged[merged.Count - 1].y] < WalkMergeGap)
                merged[merged.Count - 1] = new Vector2Int(merged[merged.Count - 1].x, span.y);
            else
                merged.Add(span);
        }

        var walks = new List<Walk>();
        foreach (Vector2Int span in merged)
        {
            float distance = 0f;
            for (int i = span.x; i < span.y; i++)
                distance += Mathf.Sqrt((x[i + 1] - x[i]) * (x[i + 1] - x[i]) + (z[i + 1] - z[i]) * (z[i + 1] - z[i]));
            if (distance >= MinWalkDistance)
                walks.Add(new Walk { Start = t[span.x], End = t[span.y], Distance = distance });
        }
        return walks;
    }

    public static List<Turn> FindTurns(List<Metric> samples)
    {
        int n = samples.Count;
        float[] t = Times(samples);

        // The head's facing, unwrapped so a turn past 180° stays continuous.
        var yaw = new float[n];
        for (int i = 0; i < n; i++)
        {
            Metric s = samples[i];
            Vector3 forward = new Quaternion(s.RotX, s.RotY, s.RotZ, s.RotW) * Vector3.forward;
            float raw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            yaw[i] = i == 0 ? raw : yaw[i - 1] + Mathf.DeltaAngle(yaw[i - 1], raw);
        }
        yaw = Smooth(yaw, t, YawWindow);

        var turns = new List<Turn>();
        int start = 0;
        while (start < n)
        {
            int end = -1;
            for (int j = start; j < n && t[j] - t[start] <= TurnWindow; j++)
            {
                if (Mathf.Abs(yaw[j] - yaw[start]) < TurnDegrees)
                    continue;

                // Ride the turn out, then the new facing has to hold.
                int e = j;
                while (e + 1 < n && Mathf.Abs(yaw[e + 1] - yaw[e]) / Mathf.Max(t[e + 1] - t[e], 0.001f) > TurnSettleRate)
                    e++;
                bool held = t[n - 1] - t[e] >= 1f;
                for (int k = e; held && k < n && t[k] - t[e] <= TurnHoldSeconds; k++)
                    held = Mathf.Abs(yaw[k] - yaw[e]) <= TurnHoldTolerance;
                if (held)
                    end = e;
                break;
            }

            if (end < 0)
            {
                start++;
                continue;
            }

            float degrees = yaw[end] - yaw[start];
            Turn last = turns.Count > 0 ? turns[turns.Count - 1] : default;
            if (turns.Count > 0 && t[start] - last.End <= TurnMergeGap && Mathf.Sign(last.Degrees) == Mathf.Sign(degrees))
                turns[turns.Count - 1] = new Turn { Start = last.Start, End = t[end], Degrees = last.Degrees + degrees };
            else
                turns.Add(new Turn { Start = t[start], End = t[end], Degrees = degrees });
            start = end + 1;
        }
        return turns;
    }

    static float[] Times(List<Metric> samples)
    {
        var t = new float[samples.Count];
        for (int i = 0; i < t.Length; i++)
            t[i] = samples[i].TiempoRelativo;
        return t;
    }

    static float[] Values(List<Metric> samples, System.Func<Metric, float> get)
    {
        var v = new float[samples.Count];
        for (int i = 0; i < v.Length; i++)
            v[i] = get(samples[i]);
        return v;
    }

    // Centered moving average over a time window (the samples aren't evenly spaced).
    static float[] Smooth(float[] values, float[] t, float window)
    {
        int n = values.Length;
        var result = new float[n];
        int lo = 0, hi = 0;
        double sum = values.Length > 0 ? values[0] : 0;
        for (int i = 0; i < n; i++)
        {
            while (hi + 1 < n && t[hi + 1] <= t[i] + window / 2f)
                sum += values[++hi];
            while (t[lo] < t[i] - window / 2f)
                sum -= values[lo++];
            result[i] = (float)(sum / (hi - lo + 1));
        }
        return result;
    }
}
