using System;
using Normal.Realtime;

// Synced remote-rehearsal state, written only by the director (who requests ownership
// before every write). Lives on the same persistent RehearsalSession object as
// RehearsalSessionSync, which is the only thing that talks to it.
//
// Detecting the director leaving: Normcore's Room has no client-left event, but when a
// client leaves the room the server clears ownership of every model it owned. So the
// director's ownership of this model going back to "unowned" means the director
// disconnected or closed the app - no timeout involved.
public class RehearsalSessionState : RealtimeComponent<RehearsalSessionStateModel>
{
    public const int StateNone = 0;
    public const int StateActive = 1;
    public const int StateEnded = 2;

    const int NoOwner = -1;

    // Raised on every client whenever the state becomes Ended.
    public event Action RehearsalEnded;

    // Raised on clients other than the previous owner when that owner's ownership is
    // cleared, i.e. the director left the room.
    public event Action OwnerLeft;

    int lastKnownOwnerID = NoOwner;

    public int State => model != null ? model.sessionState : StateNone;

    public bool HasOwner => model != null && model.ownerIDSelf != NoOwner;

    // Director only. Requests ownership first, same explicit pattern as every other synced
    // write in this project (RehearsalSessionSync.SelectShot, ActorPositionSync).
    public void SetState(int state)
    {
        if (model == null)
            return;

        model.RequestOwnership();
        model.sessionState = state;
    }

    protected override void OnRealtimeModelReplaced(RehearsalSessionStateModel previousModel, RehearsalSessionStateModel currentModel)
    {
        if (previousModel != null)
        {
            previousModel.sessionStateDidChange -= OnSessionStateDidChange;
            previousModel.ownerIDSelfDidChange -= OnOwnerIDDidChange;
        }

        if (currentModel != null)
        {
            if (currentModel.isFreshModel)
                currentModel.sessionState = StateNone;

            lastKnownOwnerID = currentModel.ownerIDSelf;
            currentModel.sessionStateDidChange += OnSessionStateDidChange;
            currentModel.ownerIDSelfDidChange += OnOwnerIDDidChange;
        }
    }

    void OnSessionStateDidChange(RehearsalSessionStateModel changedModel, int value)
    {
        if (value == StateEnded)
            RehearsalEnded?.Invoke();
    }

    void OnOwnerIDDidChange(RealtimeModel changedModel, int ownerID)
    {
        int previousOwnerID = lastKnownOwnerID;
        lastKnownOwnerID = ownerID;

        bool ownerWasSomeoneElse = previousOwnerID != NoOwner && previousOwnerID != realtime.clientID;
        if (ownerID == NoOwner && ownerWasSomeoneElse)
            OwnerLeft?.Invoke();
    }
}
