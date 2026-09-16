using Normal.Realtime;
using Normal.Realtime.Native;
using UnityEngine;

public class NormcoreDemoConnector : MonoBehaviour
{
    public Realtime realtime;
    public string roomName = "NormcoreDemoRoom";

    private void Start()
    {
        Plugin.logLevel = Plugin.LogLevel.LogLevelInfo;

        realtime.didConnectToRoom += OnConnected;
        realtime.didDisconnectFromRoom += OnDisconnected;
        realtime.didDisconnectFromRoomWithEvent += OnDisconnectedWithEvent;
        realtime.Connect(roomName);
    }

    private void OnConnected(Realtime rt)
    {
        Debug.Log($"[Normcore] Connected to room '{rt.roomName}' as clientID={rt.clientID}");
    }

    private void OnDisconnected(Realtime rt)
    {
        Debug.Log("[Normcore] Disconnected from room");
    }

    private void OnDisconnectedWithEvent(Realtime rt, DisconnectEvent disconnectEvent)
    {
        Debug.LogError($"[Normcore] Disconnect reason: {disconnectEvent.message}");
    }
}
