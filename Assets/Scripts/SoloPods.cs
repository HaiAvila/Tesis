using UnityEngine;

// Where the haptic pods plug into the actor's Ensayar Solo. Nothing here does anything yet
// and there is no UI for it - these are the two hooks SoloRehearsalController already calls
// (or will call) at the right moments, so wiring the pods later doesn't touch the solo flow.
public static class SoloPods
{
    // Called when a take loads in solo, with the shot's start mark.
    // TODO (pods + PC server): guide the actor to the mark through the pods - proximity on the
    // waist, orientation on a wrist - driven by the server, which knows the pods and the
    // actor's pose.
    public static void GuideToMark(Vector3 markPosition, Quaternion markRotation)
    {
    }

    // TODO (pods + PC server): pod status for the solo view ("pods 9/9", low battery), fed by
    // the server's pod status model. Not called by anything yet.
    public static void OnPodStatus(int podId, bool connected, float batteryLevel)
    {
    }
}
