// Whether a shot has an approved reference take - the recording marked as preferida, kept by
// the PC server - that the actor can practice against in Ensayar Solo. SceneSelect only
// offers those shots in solo.
//
// TODO (server): query the PC server through Normcore once it exists. The approved reference
// lives on the server, not on the actor's Quest, so the Quest's own database can't answer
// this. Until then every shot counts as having one.
public static class ApprovedReferences
{
    public static bool HasApprovedReference(int sceneNumber, int shotNumber)
    {
        return true;
    }
}
