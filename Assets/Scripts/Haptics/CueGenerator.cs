using System.Collections.Generic;
using UnityEngine;

// Builds a take's automatic cue timeline from the approved reference (the preferida take):
// what the actor did there becomes what the pods tell them next time. From the headset track
// (Metricas, ~10 Hz) always, and from the body (BodyTrack, from the pods) when the take has one.
//
//   start            -> Acción (CameraStart), every pod
//   end              -> Corte (Cut), every pod
//   a walk           -> Pasos (Timing): with a body, one cue per real step on that leg's shin
//                       (a knee's bend peak); without, a span with a cadence from the speed
//   a stop after one -> Marca (Checkpoint) on arrival, sternum; Aviso (Anticipation) before it
//                       only where the shot list asks for the alerta anticipatoria
//   a turn           -> Giro (Orientation) on the wrist of that side, a little before it
//                       starts; with a body the hips' turn (looking around doesn't count)
//   the hand (only where the shot list asks, on the wrist of the hand that moved):
//     proximidad     -> Proximidad span over each reach: pulses that speed up, then a double
//     contacto       -> Contacto span over the hold with the hand out: a sustained buzz
//     mantener       -> Sostener span over the last hold after a move: a pulse, then a double
//
// The thresholds were tuned on the first preferida takes (Nov 8 and 9, 2026): the head track
// is full of small shuffles and looking around, so walks are merged and must cover a distance,
// and a turn only counts if the new facing is held. Placeholder values, to tune by feel.
public static class CueGenerator
{
    // Bump when the generator changes: the database regenerates every preferida's auto cues.
    public const int Version = 3;

    // Which cues a shot calls for beyond the movement ones, from its notes in the shot list.
    public struct ShotPlan
    {
        public bool Anticipation, HandProximity, Contact, Hold;

        public static ShotPlan FromNotes(string notes)
        {
            string n = Plain(notes);
            return new ShotPlan
            {
                Anticipation = n.Contains("anticipatoria"),
                HandProximity = n.Contains("proximidad") && n.Contains("mano"),
                Contact = n.Contains("contacto"),
                Hold = n.Contains("mantener posicion") || n.Contains("(hold)"),
            };
        }

        static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            string lower = text.ToLowerInvariant();
            var plain = new System.Text.StringBuilder(lower.Length);
            foreach (char c in lower.Normalize(System.Text.NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    plain.Append(c);
            return plain.ToString();
        }

        public override string ToString() =>
            $"aviso={Anticipation} mano={HandProximity} contacto={Contact} mantener={Hold}";
    }
    // Smoothing windows, seconds.
    const float PositionWindow = 0.6f;
    const float SpeedWindow = 0.6f;
    const float YawWindow = 1f;

    // Walking: hysteresis on the smoothed horizontal speed, m/s. Low enough for a slow, careful
    // walk (shot 7 asks for one: 0.2-0.3 m/s), with a distance that rules out shuffling.
    const float WalkStartSpeed = 0.18f;
    const float WalkStopSpeed = 0.08f;
    const float WalkMergeGap = 0.8f;      // shorter pauses are part of the same walk
    const float MinWalkDistance = 0.8f;   // metres; less is shuffling, not walking

    // Steps from the knees: a bend peak at least this many degrees, this far from the last one
    // on the same leg, inside or near a walk.
    const float MinKneeBend = 12f;
    const float MinStepGap = 0.6f;       // per leg: one knee can peak twice in a single step
    const float StepWalkMargin = 0.6f;

    // The hand, relative to the chest (m, m/s).
    const float HandWindow = 0.2f;
    const float HandMoveSpeed = 0.12f;
    const float HandStillSpeed = 0.05f;
    const float HandMergeGap = 0.3f;
    const float MinHandMove = 0.12f;
    const float MinHandStill = 0.6f;
    const float ReachGain = 0.08f;        // a reach ends this much further from the chest
    const float HandOutDistance = 0.3f;   // the hand counts as "out" beyond this

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

    // Without a plan: every kind of cue the movement shows (the replay's preview).
    public static List<CueEvent> Generate(List<Metric> samples, float duration) =>
        Generate(samples, duration, new ShotPlan { Anticipation = true }, null);

    public static List<CueEvent> Generate(List<Metric> samples, float duration, ShotPlan plan, BodyTrack body)
    {
        var cues = new List<CueEvent>();
        if (samples == null || samples.Count < 2)
            return cues;

        duration = Mathf.Max(duration, samples[samples.Count - 1].TiempoRelativo);
        cues.Add(Cue(CueKind.CameraStart, 0f, PodIds.All));

        List<Walk> walks = FindWalks(samples);
        List<CueEvent> steps = body != null ? FindSteps(body, walks) : null;
        bool realSteps = steps != null && steps.Count >= 2;
        if (realSteps)
            cues.AddRange(steps);

        for (int i = 0; i < walks.Count; i++)
        {
            Walk walk = walks[i];
            if (!realSteps)
            {
                float cadence = Mathf.Clamp(CadenceAtRest + CadencePerSpeed * walk.Speed, MinCadence, MaxCadence);
                cues.Add(new CueEvent { T = walk.Start, Dur = walk.End - walk.Start, Cue = (int)CueKind.Timing, Pod = PodIds.All, Param = cadence });
            }

            float nextStart = i + 1 < walks.Count ? walks[i + 1].Start : duration;
            if (nextStart - walk.End >= MinStopSeconds)
            {
                if (plan.Anticipation)
                    cues.Add(Cue(CueKind.Anticipation, Mathf.Max(walk.Start, walk.End - AnticipationLead), PodIds.Sternum));
                cues.Add(Cue(CueKind.Checkpoint, walk.End, PodIds.Sternum));
            }
        }

        if (body != null && (plan.HandProximity || plan.Contact || plan.Hold))
            AddHandCues(cues, body, plan, duration);

        foreach (Turn turn in body != null ? FindBodyTurns(body) : FindTurns(samples))
        {
            CueEvent cue = Cue(CueKind.Orientation, Mathf.Max(0f, turn.Start - TurnLead), turn.Degrees > 0f ? PodIds.RightWrist : PodIds.LeftWrist);
            cue.Param = turn.Degrees;
            cues.Add(cue);
        }

        cues.Add(Cue(CueKind.Cut, duration, PodIds.All));
        cues.Sort((a, b) => a.T.CompareTo(b.T));
        return cues;
    }

    // A step = a bend peak of one knee, near a walk. Alternating legs come out on their own.
    static List<CueEvent> FindSteps(BodyTrack body, List<Walk> walks)
    {
        var steps = new List<CueEvent>();
        AddKneePeaks(steps, body, body.LeftKnee, PodIds.LeftShin, walks);
        AddKneePeaks(steps, body, body.RightKnee, PodIds.RightShin, walks);
        steps.Sort((a, b) => a.T.CompareTo(b.T));
        return steps;
    }

    static void AddKneePeaks(List<CueEvent> steps, BodyTrack body, float[] knee, int pod, List<Walk> walks)
    {
        float[] bend = Smooth(knee, body.Time, 0.15f);
        float last = -10f;
        for (int i = 1; i < bend.Length - 1; i++)
        {
            if (bend[i] < MinKneeBend || bend[i] < bend[i - 1] || bend[i] < bend[i + 1])
                continue;
            float t = body.Time[i];
            if (t - last < MinStepGap || !NearWalk(t, walks))
                continue;
            steps.Add(new CueEvent { T = t, Dur = 0f, Cue = (int)CueKind.Timing, Pod = pod, Param = bend[i] });
            last = t;
        }
    }

    static bool NearWalk(float t, List<Walk> walks)
    {
        foreach (Walk walk in walks)
            if (t >= walk.Start - StepWalkMargin && t <= walk.End + StepWalkMargin)
                return true;
        return false;
    }

    // Turns of the hips (the chest pod): looking around with the head doesn't count.
    public static List<Turn> FindBodyTurns(BodyTrack body) =>
        DetectTurns(body.Time, Smooth(body.HipsYaw, body.Time, YawWindow));

    // The hand that moved the most: its reaches, a hold with it out, and its last hold.
    static void AddHandCues(List<CueEvent> cues, BodyTrack body, ShotPlan plan, float duration)
    {
        bool right = PathLength(body.RightHand) >= PathLength(body.LeftHand);
        Vector3[] hand = right ? body.RightHand : body.LeftHand;
        int pod = right ? PodIds.RightWrist : PodIds.LeftWrist;
        float[] t = body.Time;
        int n = t.Length;

        float[] x = Smooth(Component(hand, 0), t, HandWindow);
        float[] y = Smooth(Component(hand, 1), t, HandWindow);
        float[] z = Smooth(Component(hand, 2), t, HandWindow);
        var speed = new float[n];
        for (int i = 1; i < n; i++)
            speed[i] = new Vector3(x[i] - x[i - 1], y[i] - y[i - 1], z[i] - z[i - 1]).magnitude / Mathf.Max(t[i] - t[i - 1], 0.001f);
        speed = Smooth(speed, t, HandWindow);
        float Reach(int i) => new Vector3(x[i], y[i], z[i]).magnitude;

        // Moving spans (hysteresis, short pauses merged, enough path), and the still ones between.
        var moves = new List<Vector2Int>();
        bool moving = false;
        int from = 0;
        for (int i = 0; i < n; i++)
        {
            if (!moving && speed[i] > HandMoveSpeed) { moving = true; from = i; }
            else if (moving && speed[i] < HandStillSpeed) { moving = false; moves.Add(new Vector2Int(from, i)); }
        }
        if (moving)
            moves.Add(new Vector2Int(from, n - 1));
        var merged = new List<Vector2Int>();
        foreach (Vector2Int m in moves)
        {
            if (merged.Count > 0 && t[m.x] - t[merged[merged.Count - 1].y] < HandMergeGap)
                merged[merged.Count - 1] = new Vector2Int(merged[merged.Count - 1].x, m.y);
            else
                merged.Add(m);
        }
        merged.RemoveAll(m => PathLength(hand, m.x, m.y) < MinHandMove);

        var stills = new List<Vector2>();
        float stillFrom = 0f;
        foreach (Vector2Int m in merged)
        {
            if (t[m.x] - stillFrom >= MinHandStill)
                stills.Add(new Vector2(stillFrom, t[m.x]));
            stillFrom = t[m.y];
        }
        if (duration - stillFrom >= MinHandStill)
            stills.Add(new Vector2(stillFrom, duration));

        if (plan.HandProximity && merged.Count > 0)
        {
            bool any = false;
            foreach (Vector2Int m in merged)
            {
                if (Reach(m.y) - Reach(m.x) < ReachGain)
                    continue;
                cues.Add(new CueEvent { T = t[m.x], Dur = t[m.y] - t[m.x], Cue = (int)CueKind.Proximity, Pod = pod });
                any = true;
            }
            if (!any)
            {
                Vector2Int longest = merged[0];
                foreach (Vector2Int m in merged)
                    if (t[m.y] - t[m.x] > t[longest.y] - t[longest.x]) longest = m;
                cues.Add(new CueEvent { T = t[longest.x], Dur = t[longest.y] - t[longest.x], Cue = (int)CueKind.Proximity, Pod = pod });
            }
        }

        if (plan.Contact)
        {
            bool found = false;
            Vector2 best = default;
            foreach (Vector2 s in stills)
            {
                if (Reach(IndexAt(t, (s.x + s.y) / 2f)) < HandOutDistance)
                    continue;
                if (!found || s.y - s.x > best.y - best.x)
                    best = s;
                found = true;
            }
            if (found)
                AddSpan(cues, CueKind.Contact, Trim(best, duration), pod);
        }

        if (plan.Hold && stills.Count > 0)
        {
            Vector2 hold = stills[stills.Count - 1];
            if (merged.Count == 0)
                foreach (Vector2 s in stills)
                    if (s.y - s.x > hold.y - hold.x) hold = s;
            AddSpan(cues, CueKind.HoldPosition, Trim(hold, duration), pod);
        }
    }

    // Holds keep clear of Acción and Corte, which have their own pulses.
    static Vector2 Trim(Vector2 span, float duration) => new Vector2(Mathf.Max(span.x, 0.4f), Mathf.Min(span.y, duration - 0.4f));

    static void AddSpan(List<CueEvent> cues, CueKind kind, Vector2 span, int pod)
    {
        if (span.y > span.x)
            cues.Add(new CueEvent { T = span.x, Dur = span.y - span.x, Cue = (int)kind, Pod = pod });
    }

    static float[] Component(Vector3[] v, int axis)
    {
        var c = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            c[i] = v[i][axis];
        return c;
    }

    static float PathLength(Vector3[] v, int from = 0, int to = -1)
    {
        if (to < 0) to = v.Length - 1;
        float length = 0f;
        for (int i = from; i < to; i++)
            length += (v[i + 1] - v[i]).magnitude;
        return length;
    }

    static int IndexAt(float[] t, float time)
    {
        int i = 0;
        while (i + 1 < t.Length && t[i + 1] <= time)
            i++;
        return i;
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
        return DetectTurns(t, Smooth(yaw, t, YawWindow));
    }

    // A facing change of TurnDegrees within TurnWindow that is then held; back-to-back turns the
    // same way merged. yaw: smoothed and unwrapped, degrees.
    static List<Turn> DetectTurns(float[] t, float[] yaw)
    {
        int n = t.Length;
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
