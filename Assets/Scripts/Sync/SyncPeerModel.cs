using Normal.Realtime;

// One per machine in the room (SyncPeer, a Resources prefab each machine creates for itself and
// owns): who it is, and its end of the pipe that carries data between devices (SyncLink).
//
// role: SyncRole as an int. peerId: random, new on every connection - messages are addressed to
//   it, not to the Normcore client ID, which a later client can get again.
// outTo / outSeq / outData: the one chunk this machine is sending right now - to which peer, its
//   number, its bytes. It stays there until that peer acknowledges it, then the next one goes
//   in. State, not an event: Normcore only promises the latest value of a property arrives.
// acks: the last chunk number received from each peer, "peerId:seq;peerId:seq".
// dataVersion: the server adds one whenever its stored takes or cue timelines change, so the
//   devices know to look again.
// status: a few words about what this machine's sync is doing, for the server's window.
[RealtimeModel(createMetaModel: true)]
public partial class SyncPeerModel
{
    [RealtimeProperty(1, true, true)] private int _role;
    [RealtimeProperty(2, true, true)] private int _peerId;
    [RealtimeProperty(3, true, true)] private int _outTo;
    [RealtimeProperty(4, true, true)] private int _outSeq;
    [RealtimeProperty(5, true, true)] private byte[] _outData;
    [RealtimeProperty(6, true, true)] private string _acks;
    [RealtimeProperty(7, true, true)] private int _dataVersion;
    [RealtimeProperty(8, true, true)] private string _status;
}
