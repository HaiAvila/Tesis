// Scene 3's levitating robot (the drone, shot 14): when it dashes and when it passes in front of
// the protagonist, counted from the set's action trigger (Acción of shot 14). Shared by the flight
// you see (DroneFlight) and the sweep the actor feels as it goes by (CueEngine, on the machine
// with the pods, which has no scene loaded), so both follow the same clock - "Repetir dron"
// included.
//
// It crosses 0.4 m in front of shot 15's mark from the actor's right to their left (Nov 11 start
// marks), so the sweep runs the same way: right biceps, then left biceps (pods 4 and 3, which
// carry no other cue). The crossing moment comes from the dash's ease-in-out curve: the actor's
// line is about two thirds of the way along it, reached ~0.74 s into the 1.3 s dash.
public static class DronePlan
{
    public const int Shot = 14;
    public const float Delay = 1.5f;          // s after Acción (the user, Nov 11; was 5)
    public const float DashSeconds = 1.3f;

    public const float PassRightAt = Delay + 0.55f;  // right biceps, just before the crossing
    public const float PassLeftAt = Delay + 0.85f;   // left biceps, just after it
    public const int PassParam = 10;                 // LONG 100 ms each
}
