using System.Collections.Generic;
using UnityEngine;

// A take next to its shot's reference (the preferida): where the reference's marks are, and how
// this take did at each one. Used by the live cues (LiveCues: where the marks are, which way to
// face along the way) and by the take report the director gets after Corte (RehearsalRecorder).
// Analysis/analizar_tomas.py measures the same things for the thesis; its marks come from the
// reference's stops, these from the timeline's Marca cues (the same stops, CueGenerator's, plus
// whatever the director edited).
//
// Positions are the actor's head, horizontal (x, z), in the set's coordinates: every take starts
// on the shot's start mark, so two takes of a shot share them.
public static class TakeComparison
{
    // Same thresholds as analizar_tomas.py.
    public const float StillSpeed = 0.12f;      // m/s
    public const float SpeedWindow = 0.6f;      // s
    public const float MinStop = 1f;            // s
    public const float MinMarkSpacing = 0.5f;   // m
    public const float HitRadius = 1.5f;        // m
    public const float PassRadius = 0.4f;       // m: walking through this close counts too (LiveCues' arrival)

    public struct Point
    {
        public float T, X, Z, Yaw;
    }

    public class Mark
    {
        public int Number;      // 1, 2, ...
        public float T;         // when the reference got there (s from Acción)
        public float X, Z;
        public float Yaw;       // which way the reference's head faced there
    }

    public class Stop
    {
        public float Start, End, X, Z, Yaw;
    }

    public class MarkResult
    {
        public Mark Mark;
        public bool Reached;
        public bool WithoutStopping;    // reached by walking through it, within PassRadius
        public float Distance;      // reached: from the mark (m)
        public float Delay;         // reached: + late, - early (s)
        public float Facing;        // reached: head direction off (degrees)
        public float Closest;       // how close the path ever came (m)
    }

    public class Result
    {
        public readonly List<MarkResult> Marks = new List<MarkResult>();
        public float PathError;         // mean distance to the reference's path (m)
        public float DurationDifference;
        public float StartOffset;
        public int Reached;
    }

    public static float HeadYaw(float qx, float qy, float qz, float qw)
    {
        Vector3 forward = new Quaternion(qx, qy, qz, qw) * Vector3.forward;
        return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    }

    public static List<Point> FromMetrics(List<Metric> metrics)
    {
        var points = new List<Point>(metrics.Count);
        foreach (Metric m in metrics)
            points.Add(new Point { T = m.TiempoRelativo, X = m.PosX, Z = m.PosZ, Yaw = HeadYaw(m.RotX, m.RotY, m.RotZ, m.RotW) });
        return points;
    }

    // The reference's marks: its Marca cues (CueKind.Checkpoint), each where the reference stood
    // for the first second after it.
    public static List<Mark> MarksFromCues(List<CueEvent> cues, List<Point> reference)
    {
        var marks = new List<Mark>();
        if (reference == null || reference.Count < 2)
            return marks;
        foreach (CueEvent cue in cues)
        {
            if (cue.Kind != CueKind.Checkpoint)
                continue;
            Vector2 p = MeanPosition(reference, cue.T, cue.T + 1f);
            marks.Add(new Mark { Number = marks.Count + 1, T = cue.T, X = p.x, Z = p.y, Yaw = MeanYaw(reference, cue.T, cue.T + 1f) });
        }
        marks.Sort((a, b) => a.T.CompareTo(b.T));
        for (int i = 0; i < marks.Count; i++)
            marks[i].Number = i + 1;
        return marks;
    }

    public static Vector2 PositionAt(List<Point> points, float t)
    {
        if (t <= points[0].T)
            return new Vector2(points[0].X, points[0].Z);
        Point last = points[points.Count - 1];
        if (t >= last.T)
            return new Vector2(last.X, last.Z);
        int lo = 0, hi = points.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].T <= t) lo = mid; else hi = mid;
        }
        Point a = points[lo], b = points[hi];
        float k = b.T > a.T ? (t - a.T) / (b.T - a.T) : 0f;
        return new Vector2(Mathf.Lerp(a.X, b.X, k), Mathf.Lerp(a.Z, b.Z, k));
    }

    static Vector2 MeanPosition(List<Point> points, float t0, float t1)
    {
        Vector2 sum = Vector2.zero;
        int n = 0;
        foreach (Point p in points)
        {
            if (p.T < t0 || p.T > t1) continue;
            sum += new Vector2(p.X, p.Z);
            n++;
        }
        return n > 0 ? sum / n : PositionAt(points, t0);
    }

    public static float MeanYaw(List<Point> points, float t0, float t1)
    {
        float s = 0f, c = 0f;
        foreach (Point p in points)
        {
            if (p.T < t0 || p.T > t1) continue;
            s += Mathf.Sin(p.Yaw * Mathf.Deg2Rad);
            c += Mathf.Cos(p.Yaw * Mathf.Deg2Rad);
        }
        if (Mathf.Approximately(s, 0f) && Mathf.Approximately(c, 0f))
        {
            int i = NearestIndexInTime(points, t0);
            return points[i].Yaw;
        }
        return Mathf.Atan2(s, c) * Mathf.Rad2Deg;
    }

    static int NearestIndexInTime(List<Point> points, float t)
    {
        int best = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (Mathf.Abs(points[i].T - t) < Mathf.Abs(points[best].T - t))
                best = i;
        }
        return best;
    }

    // Where the take stood still for MinStop or more, after moving MinMarkSpacing away from the
    // previous stop (or the start). Standing at the start isn't a stop.
    public static List<Stop> FindStops(List<Point> points)
    {
        var stops = new List<Stop>();
        if (points.Count < 3)
            return stops;

        var run = new List<Point>();
        Vector2 anchor = new Vector2(points[0].X, points[0].Z);
        int j0 = 0, j1 = 0;
        for (int i = 0; i <= points.Count; i++)
        {
            bool still = false;
            if (i < points.Count)
            {
                Point p = points[i];
                while (j0 < i && p.T - points[j0].T > SpeedWindow / 2f) j0++;
                while (j1 < points.Count - 1 && points[j1 + 1].T - p.T <= SpeedWindow / 2f) j1++;
                float span = points[j1].T - points[j0].T;
                float speed = span > 1e-4f ? Vector2.Distance(new Vector2(points[j1].X, points[j1].Z), new Vector2(points[j0].X, points[j0].Z)) / span : 0f;
                still = speed < StillSpeed;
                if (still)
                    run.Add(p);
            }
            if (still || run.Count == 0)
                continue;

            if (run[run.Count - 1].T - run[0].T >= MinStop)
            {
                var stop = new Stop { Start = run[0].T, End = run[run.Count - 1].T };
                float s = 0f, c = 0f;
                foreach (Point q in run)
                {
                    stop.X += q.X / run.Count;
                    stop.Z += q.Z / run.Count;
                    s += Mathf.Sin(q.Yaw * Mathf.Deg2Rad);
                    c += Mathf.Cos(q.Yaw * Mathf.Deg2Rad);
                }
                stop.Yaw = Mathf.Atan2(s, c) * Mathf.Rad2Deg;

                Vector2 at = new Vector2(stop.X, stop.Z);
                if (Vector2.Distance(at, anchor) >= MinMarkSpacing)
                {
                    stops.Add(stop);
                    anchor = at;
                }
                else if (stops.Count > 0 && Vector2.Distance(at, new Vector2(stops[stops.Count - 1].X, stops[stops.Count - 1].Z)) < MinMarkSpacing)
                {
                    stops[stops.Count - 1].End = stop.End;
                }
            }
            run.Clear();
        }
        return stops;
    }

    public static float DistanceToPath(float x, float z, List<Point> path)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            float ax = path[i].X, az = path[i].Z;
            float dx = path[i + 1].X - ax, dz = path[i + 1].Z - az;
            float length2 = dx * dx + dz * dz;
            float k = length2 < 1e-8f ? 0f : Mathf.Clamp01(((x - ax) * dx + (z - az) * dz) / length2);
            float ex = x - (ax + dx * k), ez = z - (az + dz * k);
            best = Mathf.Min(best, ex * ex + ez * ez);
        }
        return path.Count == 1 ? Vector2.Distance(new Vector2(x, z), new Vector2(path[0].X, path[0].Z)) : Mathf.Sqrt(best);
    }

    public static Result Compare(List<Point> take, float takeDuration, List<Point> reference, float referenceDuration, List<Mark> marks)
    {
        var result = new Result();
        if (take == null || take.Count < 2 || reference == null || reference.Count < 2)
            return result;

        result.DurationDifference = takeDuration - referenceDuration;
        result.StartOffset = Vector2.Distance(new Vector2(take[0].X, take[0].Z), new Vector2(reference[0].X, reference[0].Z));
        float sum = 0f;
        foreach (Point p in take)
            sum += DistanceToPath(p.X, p.Z, reference);
        result.PathError = sum / take.Count;

        List<Stop> stops = FindStops(take);
        int next = 0;
        foreach (Mark mark in marks)
        {
            var entry = new MarkResult { Mark = mark, Closest = float.MaxValue };
            float closestAt = 0f;
            foreach (Point p in take)
            {
                float d = Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(mark.X, mark.Z));
                if (d < entry.Closest)
                {
                    entry.Closest = d;
                    closestAt = p.T;
                }
            }

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int k = next; k < stops.Count; k++)
            {
                float d = Vector2.Distance(new Vector2(stops[k].X, stops[k].Z), new Vector2(mark.X, mark.Z));
                if (d <= HitRadius && d < bestDistance)
                {
                    best = k;
                    bestDistance = d;
                }
            }
            if (best >= 0)
            {
                Stop stop = stops[best];
                next = best + 1;
                entry.Reached = true;
                entry.Distance = bestDistance;
                entry.Delay = stop.Start - mark.T;
                entry.Facing = Mathf.Abs(Mathf.DeltaAngle(stop.Yaw, mark.Yaw));
                result.Reached++;
            }
            else if (entry.Closest <= PassRadius)
            {
                entry.Reached = true;
                entry.WithoutStopping = true;
                entry.Distance = entry.Closest;
                entry.Delay = closestAt - mark.T;
                entry.Facing = Mathf.Abs(Mathf.DeltaAngle(MeanYaw(take, closestAt - 0.5f, closestAt + 0.5f), mark.Yaw));
                result.Reached++;
            }
            result.Marks.Add(entry);
        }
        return result;
    }
}
