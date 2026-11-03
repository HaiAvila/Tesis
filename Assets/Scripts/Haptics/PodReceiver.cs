using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives pod orientation from bridge.py over UDP and sends haptic cues back.
/// Put this on one GameObject in the scene.
/// </summary>
public class PodReceiver : MonoBehaviour
{
    public enum Cue { Stop = 0, Click = 1, Pulse = 2, Triple = 3, Long = 4, Double = 5 }

    [Header("Network")]
    public int listenPort = 5005;
    public int commandPort = 5006;
    public string bridgeHost = "127.0.0.1";

    [Header("Orientation")]
    [Tooltip("If pods rotate the wrong way, try the other conversions.")]
    public int conversion = 0;

    [Header("Status (read only)")]
    public int connectedPods;
    public int targetPods;

    readonly Dictionary<int, Quaternion> rotations = new Dictionary<int, Quaternion>();
    readonly Dictionary<int, float> lastSeen = new Dictionary<int, float>();
    readonly object gate = new object();

    UdpClient listener;
    UdpClient sender;
    Thread rxThread;
    volatile bool running;

    void Start()
    {
        sender = new UdpClient();
        listener = new UdpClient(listenPort);
        running = true;
        rxThread = new Thread(ReceiveLoop) { IsBackground = true };
        rxThread.Start();
        Debug.Log($"PodReceiver listening on {listenPort}");
    }

    void OnDestroy()
    {
        running = false;
        listener?.Close();
        sender?.Close();
        rxThread?.Join(200);
    }

    void ReceiveLoop()
    {
        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                string line = Encoding.UTF8.GetString(listener.Receive(ref any));
                Parse(line);
            }
            catch (SocketException) { if (running) continue; }
            catch (ObjectDisposedException) { break; }
        }
    }

    void Parse(string line)
    {
        string[] f = line.Split(',');
        if (f.Length == 0) return;

        if (f[0] == "P" && f.Length == 6)
        {
            if (!int.TryParse(f[1], out int id)) return;
            if (!float.TryParse(f[2], out float qi)) return;
            if (!float.TryParse(f[3], out float qj)) return;
            if (!float.TryParse(f[4], out float qk)) return;
            if (!float.TryParse(f[5], out float qr)) return;

            lock (gate)
            {
                rotations[id] = ToUnity(qi, qj, qk, qr);
                lastSeen[id] = Time.realtimeSinceStartup;
            }
        }
        else if (f[0] == "S" && f.Length >= 3)
        {
            int.TryParse(f[1], out connectedPods);
            int.TryParse(f[2], out targetPods);
        }
    }

    /// <summary>
    /// BNO085 is right-handed; Unity is left-handed and Y-up. Which axis swap
    /// is correct depends on how the pod sits in its housing, so verify it:
    /// hold a pod, rotate it, and check the object follows. If it mirrors or
    /// spins on the wrong axis, change `conversion` in the inspector.
    /// </summary>
    Quaternion ToUnity(float i, float j, float k, float r)
    {
        switch (conversion)
        {
            case 1:  return new Quaternion(-i, -k, -j, r);
            case 2:  return new Quaternion(i, k, j, -r);
            case 3:  return new Quaternion(j, k, i, -r);
            default: return new Quaternion(-i, -k, -j, -r);
        }
    }

    // ---- public API -------------------------------------------------------

    /// <summary>Latest orientation for a pod. Identity if never seen.</summary>
    public Quaternion GetRotation(int podId)
    {
        lock (gate)
            return rotations.TryGetValue(podId, out var q) ? q : Quaternion.identity;
    }

    /// <summary>True if that pod sent data in the last `timeout` seconds.</summary>
    public bool IsAlive(int podId, float timeout = 1f)
    {
        lock (gate)
            return lastSeen.TryGetValue(podId, out float t)
                   && Time.realtimeSinceStartup - t < timeout;
    }

    public int[] ActivePods()
    {
        lock (gate)
        {
            var list = new List<int>();
            foreach (var kv in lastSeen)
                if (Time.realtimeSinceStartup - kv.Value < 1f) list.Add(kv.Key);
            return list.ToArray();
        }
    }

    /// <summary>Fire a cue. podId 0 broadcasts to every pod.</summary>
    public void SendCue(int podId, Cue cue, int param = 0)
    {
        string msg = $"C,{podId},{(int)cue},{Mathf.Clamp(param, 0, 255)}";
        byte[] data = Encoding.UTF8.GetBytes(msg);
        try { sender.Send(data, data.Length, bridgeHost, commandPort); }
        catch (SocketException e) { Debug.LogWarning($"SendCue failed: {e.Message}"); }
    }

    // ---- the five MVP cues ------------------------------------------------

    /// <summary>Proximity: faster as distance shrinks. waistPod only.</summary>
    public void CueProximity(int podId, float distance, float maxDistance = 3f)
    {
        float t = Mathf.Clamp01(distance / maxDistance);
        int gapMs = Mathf.RoundToInt(Mathf.Lerp(80f, 800f, t));
        SendCue(podId, Cue.Pulse, gapMs / 10);
    }

    public void CueProximityStop(int podId) => SendCue(podId, Cue.Stop);

    /// <summary>Orientation: a directional nudge on one wrist.</summary>
    public void CueOrientation(int podId) => SendCue(podId, Cue.Click);

    /// <summary>Timing: one footstep. Call alternately on L and R calves.</summary>
    public void CueStep(int podId) => SendCue(podId, Cue.Click);

    /// <summary>Camera start: three pulses on every pod.</summary>
    public void CueCameraStart() => SendCue(0, Cue.Triple);

    /// <summary>Cut: one long pulse on every pod.</summary>
    public void CueCut(int ms = 600) => SendCue(0, Cue.Long, ms / 10);
}
