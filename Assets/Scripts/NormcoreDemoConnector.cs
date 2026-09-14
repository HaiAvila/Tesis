using Normal.Realtime;
using UnityEngine;

public class NormcoreDemoConnector : MonoBehaviour
{
    public Realtime realtime;
    public string roomName = "NormcoreDemoRoom";

    private void Start()
    {
        realtime.didConnectToRoom += OnConnected;
        realtime.didDisconnectFromRoom += OnDisconnected;
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
}
