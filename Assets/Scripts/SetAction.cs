using System;
using UnityEngine;

// The set's scripted action - scene 3's drone, scene 1's giant: one trigger moment and the shot
// it belongs to, from which every client works out the same motion (see DroneFlight, GolemWalk).
// Where that moment comes from:
//
//   Ensayo Remoto  a Normcore room time in the session model, written by the director, so the
//                  actor's and the director's drones move together, a late joiner included
//   Solo, Inspección  a local trigger on this device
//   a replay       the replay's own clock and the take's recorded triggers (ReplayElapsed),
//                  so the action plays back when it happened in the take
//
// The scene's action object registers itself (Register) so the pause menu can offer "Repetir"
// with its name. The director's recorder keeps each trigger with the take (Triggered).
public static class SetAction
{
    public const float NotTriggered = -1f;

    static double localTrigger = -1;
    static int localShot;

    // Set by the replay while one is open: seconds since the last trigger at the replay's
    // moment (NotTriggered before any), and where the actor's head was then.
    public static bool ReplayActive { get; private set; }
    public static float ReplayElapsed { get; private set; } = NotTriggered;
    public static Vector3? ReplayActorHead { get; private set; }
    public static int ReplayShot { get; private set; }

    // Fired on the machine that triggers, for the recorder.
    public static event Action Triggered;

    // The action in the open scene, if any: its name for the pause menu ("dron"), and optionally
    // its own menu description and the message when it's repeated.
    public static string TargetName { get; private set; }
    public static float TargetDelay { get; private set; }
    public static string TargetDescription { get; private set; }
    public static string TargetMessage { get; private set; }
    public static bool HasTarget => TargetName != null;

    public static void Register(string name, float delay, string description = null, string message = null)
    {
        TargetName = name;
        TargetDelay = delay;
        TargetDescription = description;
        TargetMessage = message;
    }
    public static void Unregister(string name)
    {
        if (TargetName == name)
            TargetName = null;
    }

    // In Ensayo Remoto only the director may trigger (it owns the session model).
    public static bool CanTrigger
    {
        get
        {
            if (!UsesRoomClock)
                return true;
#if DIRECTOR_BUILD
            return true;
#else
            return false;
#endif
        }
    }

    static bool UsesRoomClock => Bootstrap.Mode == Bootstrap.AppMode.Remote
                                 && RehearsalSessionSync.Instance != null && RehearsalSessionSync.Instance.Online;

    // shot: the shot the action belongs to (default: the open take's).
    public static void Trigger(string reason, int shot = -1)
    {
        if (!CanTrigger)
            return;

        // Repetir in an escena completa: the shot of the last trigger.
        if (shot < 0)
            shot = Bootstrap.IsFullScene && Shot > 0 ? Shot : Bootstrap.CurrentShotNumber;
        if (UsesRoomClock)
            RehearsalSessionSync.Instance.SetSetActionTime(RehearsalSessionSync.Instance.RoomTime, shot);
        else
        {
            localTrigger = Time.timeAsDouble;
            localShot = shot;
        }

        Debug.Log($"[SetAction] triggered for shot {shot} ({reason})");
        Triggered?.Invoke();
    }

    // Back to its start: a new take, or a take that isn't the action's shot.
    public static void Clear(string reason)
    {
        if (!CanTrigger)
            return;

        if (UsesRoomClock)
        {
            if (RehearsalSessionSync.Instance.SetActionTime != 0)
                RehearsalSessionSync.Instance.SetSetActionTime(0);
        }
        localTrigger = -1;
        Debug.Log($"[SetAction] cleared ({reason})");
    }

    // The shot of the last trigger (0 = none).
    public static int Shot
    {
        get
        {
            if (ReplayActive)
                return ReplayShot;
            if (UsesRoomClock)
                return RehearsalSessionSync.Instance.SetActionShot;
            return localTrigger >= 0 ? localShot : 0;
        }
    }

    // Seconds since the trigger, or NotTriggered.
    public static float Elapsed
    {
        get
        {
            if (ReplayActive)
                return ReplayElapsed;

            if (UsesRoomClock)
            {
                double trigger = RehearsalSessionSync.Instance.SetActionTime;
                return trigger > 0 ? (float)(RehearsalSessionSync.Instance.RoomTime - trigger) : NotTriggered;
            }

            return localTrigger >= 0 ? (float)(Time.timeAsDouble - localTrigger) : NotTriggered;
        }
    }

    // shot: the shot of the take at the replay's moment (a full scene's, from its boundaries).
    public static void SetReplay(float elapsed, Vector3? actorHead, int shot = 0)
    {
        ReplayActive = true;
        ReplayElapsed = elapsed;
        ReplayActorHead = actorHead;
        ReplayShot = shot;
    }

    public static void EndReplay()
    {
        ReplayActive = false;
        ReplayElapsed = NotTriggered;
        ReplayActorHead = null;
        ReplayShot = 0;
    }
}
