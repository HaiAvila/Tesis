using System.Collections.Generic;

// Scene 1's giant (the fungal golem): what it does in each shot and when, counted from the set's
// action trigger (SetAction, which fires at Acción). Shared by the walk you see (GolemWalk, in the
// scene, on every headset) and by the haptics the actor feels from it (CueEngine, on the machine
// with the pods, which has no scene loaded) - so both follow the same clock and stay together,
// a director's "Repetir" included.
//
//   shot 1   still inside the cave
//   shot 2   rumbles inside the cave from Acción: the ground shakes (both shins), then the
//            alert (Aviso, sternum)
//   shot 3   1.5 s after Acción walks out of the cave
//   shot 4   right at Acción keeps walking, up to the protagonist
//   shot 5   1.5 s after Acción turns to its right and steps a little closer
// Every footfall of a walk is a heavy thud on both thighs (the quads, pods 5 and 6, which carry
// no other cue: the shins are the actor's own steps, the thighs are the giant's).
//
// The walks' lengths come from where the user placed each end (GolemWalk's waypoints, Nov 11) at
// about 0.6 m/s, a slow heavy pace; the durations here are what both sides use.
public static class GolemPlan
{
    public enum Kind { None, Rumble, Walk }

    public struct Action
    {
        public Kind Kind;
        public float Delay;      // s after the trigger
        public float Duration;   // s
    }

    public const float StrideSeconds = 0.9f;       // one footfall every this long
    // The tremor is felt right after Acción's three pulses (every pod: they would cut it short),
    // the visual rumble starts at Acción itself.
    public const float RumbleTremorAt = 0.5f;
    public const float RumbleTremorSeconds = 1.2f; // the shins buzz this long
    public const float RumbleAlertAt = 1.9f;       // then the Aviso on the sternum
    public const int ThudParam = 15;               // LONG 150 ms per footfall
    public const int TremorGapParam = 10;          // PULSE every 100 ms: a rattle

    public static Action For(int shot)
    {
        switch (shot)
        {
            case 2: return new Action { Kind = Kind.Rumble, Delay = 0f, Duration = 2f };
            case 3: return new Action { Kind = Kind.Walk, Delay = 1.5f, Duration = 3.8f };   // 2.3 m
            case 4: return new Action { Kind = Kind.Walk, Delay = 0f, Duration = 5.1f };     // 3.0 m
            case 5: return new Action { Kind = Kind.Walk, Delay = 1.5f, Duration = 2.4f };   // 1.1 m and a quarter turn
            default: return new Action { Kind = Kind.None };
        }
    }

    public static bool HasAction(int shot) => For(shot).Kind != Kind.None;

    // Seconds from the trigger of each footfall of that shot's walk (the body is lowest then).
    public static List<float> Footfalls(int shot)
    {
        var times = new List<float>();
        Action action = For(shot);
        if (action.Kind != Kind.Walk)
            return times;
        for (float t = StrideSeconds / 2f; t < action.Duration; t += StrideSeconds)
            times.Add(action.Delay + t);
        return times;
    }
}
