using UnityEngine;

// The haptic cue vocabulary (CLAUDE.md, "Haptic cue vocabulary"), numbered as there. This is
// what a cue MEANS; which firmware pattern plays it (CLICK, PULSE, TRIPLE... - PodReceiver's
// cue IDs) is the cue engine's business, so a pattern can change without touching stored
// timelines. Stored as the number in CueTrack.cue.
public enum CueKind
{
    Proximity = 1,
    Orientation = 2,
    Timing = 3,
    CameraStart = 4,
    Cut = 5,
    MarkConfirmation = 6,
    Checkpoint = 7,
    Contact = 8,
    Anticipation = 9,
    SpeedDeviation = 10,
    HoldPosition = 11,
    ExclusionZone = 12,
    OtherActor = 13,
    // Experimental tier, #15 "breathing rhythm / emotional pacing": used as a heartbeat for one
    // scene (scene 1, shot 4: fear), from the shot list's notes ("latido").
    EmotionalPacing = 15,
}

// Pod IDs by body part (Docs/pod-map.md). 0 = every pod.
public static class PodIds
{
    public const int All = 0;
    public const int LeftWrist = 1;
    public const int RightWrist = 2;
    public const int LeftBicep = 3;
    public const int RightBicep = 4;
    public const int LeftQuad = 5;
    public const int RightQuad = 6;
    public const int LeftShin = 7;
    public const int RightShin = 8;
    public const int Sternum = 9;
}

public static class HapticCues
{
    // Spanish, for the UI.
    public static string Name(CueKind kind)
    {
        switch (kind)
        {
            case CueKind.Proximity: return "Proximidad";
            case CueKind.Orientation: return "Giro";
            case CueKind.Timing: return "Pasos";
            case CueKind.CameraStart: return "Acción";
            case CueKind.Cut: return "Corte";
            case CueKind.MarkConfirmation: return "Marca inicial";
            case CueKind.Checkpoint: return "Marca";
            case CueKind.Contact: return "Contacto";
            case CueKind.Anticipation: return "Aviso";
            case CueKind.SpeedDeviation: return "Ritmo";
            case CueKind.HoldPosition: return "Sostener";
            case CueKind.ExclusionZone: return "Límite";
            case CueKind.OtherActor: return "Otro actor";
            case CueKind.EmotionalPacing: return "Latido";
            default: return kind.ToString();
        }
    }

    public static Color Color(CueKind kind)
    {
        switch (kind)
        {
            case CueKind.CameraStart:
            case CueKind.Cut: return new Color(1f, 1f, 1f, 1f);
            case CueKind.Orientation: return new Color(0.12f, 0.71f, 0.92f, 1f);
            case CueKind.Timing: return new Color(0.66f, 0.52f, 1f, 1f);
            case CueKind.Anticipation: return new Color(1f, 0.62f, 0.22f, 1f);
            case CueKind.Checkpoint: return new Color(0.35f, 0.88f, 0.5f, 1f);
            case CueKind.Proximity: return new Color(1f, 0.45f, 0.72f, 1f);
            case CueKind.Contact: return new Color(0.98f, 0.86f, 0.3f, 1f);
            case CueKind.HoldPosition: return new Color(0.62f, 0.76f, 1f, 1f);
            case CueKind.EmotionalPacing: return new Color(0.94f, 0.33f, 0.38f, 1f);
            default: return new Color(0.8f, 0.8f, 0.8f, 1f);
        }
    }
}
