using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Receives pod orientation from bridge.py over UDP and sends haptic cues back.
/// Put this on one GameObject in the scene.
/// </summary>
/// <remarks>
/// On the server the receiver's USB port is read directly (SerialBridge): the same lines come
/// in through Feed and cues go out through directCueOut, with no bridge.py. UDP keeps listening
/// either way, so bridge.py still works there if the port can't be opened.
///
/// Lines are parsed on a background thread, so nothing in Parse may touch the Unity API
/// (Time, transforms, ...): it threw on the first sample and killed the thread. Times come
/// from a Stopwatch, which any thread can read.
///
/// A pod that's connected but has no IMU reading sends an all-zero quaternion
/// ("P,id,0,0,0,0"): it counts as alive, but HasOrientation is false and its last rotation
/// is kept.
/// </remarks>
public class PodReceiver : MonoBehaviour
{
    // Step (8, param = total ms/10) and Rise (9): pod firmware v5; older pods ignore them.
    public enum Cue { Stop = 0, Click = 1, Pulse = 2, Triple = 3, Long = 4, Double = 5, Done = 6, Fail = 7, Step = 8, Rise = 9 }

    /// <summary>A pod's latest H line: its battery and charge status (read on the pod) and
    /// its signal (as the receiver sees it).</summary>
    public struct PodHealth
    {
        public const int LowBatteryMillivolts = 3600;

        public int batteryMillivolts;  // 0 = the pod reports no battery (firmware older than v4)
        public int charging;           // 1 charging, 0 not, -1 unknown (firmware older than v4)
        public int rssi;               // dBm, negative; 0 = not available

        public bool HasBattery => batteryMillivolts > 0;
        public bool IsCharging => charging == 1;
        public bool HasRssi => rssi != 0;
        public bool IsLow => HasBattery && !IsCharging && batteryMillivolts < LowBatteryMillivolts;
        public int BatteryPercent => HasBattery ? PercentFromMillivolts(batteryMillivolts) : -1;

        // A typical LiPo's voltage-to-charge curve: an estimate, and it reads high while
        // charging and low under a buzz. (mV, %)
        static readonly int[,] curve =
        {
            { 3270, 0 }, { 3610, 5 }, { 3690, 10 }, { 3710, 15 }, { 3730, 20 }, { 3750, 25 },
            { 3770, 30 }, { 3790, 35 }, { 3800, 40 }, { 3820, 45 }, { 3840, 50 }, { 3850, 55 },
            { 3870, 60 }, { 3910, 65 }, { 3950, 70 }, { 3980, 75 }, { 4020, 80 }, { 4080, 85 },
            { 4110, 90 }, { 4150, 95 }, { 4200, 100 },
        };

        public static int PercentFromMillivolts(int mv)
        {
            int last = curve.GetLength(0) - 1;
            if (mv <= curve[0, 0]) return 0;
            if (mv >= curve[last, 0]) return 100;
            for (int i = 1; i <= last; i++)
            {
                if (mv > curve[i, 0])
                    continue;
                float t = (mv - curve[i - 1, 0]) / (float)(curve[i, 0] - curve[i - 1, 0]);
                return Mathf.RoundToInt(Mathf.Lerp(curve[i - 1, 1], curve[i, 1], t));
            }
            return 100;
        }

        /// <summary>For the test screens, rich text: "3.92 V, 67 %, -58 dBm".</summary>
        public string ToLabel()
        {
            string battery = !HasBattery ? ""
                : $"{batteryMillivolts / 1000f:F2} V, {BatteryPercent} %" + (IsCharging ? ", cargando" : "");
            if (IsLow)
                battery = $"<color=#ffaa33>{battery}, batería baja</color>";
            string signal = HasRssi ? $"{rssi} dBm" : "";
            return battery.Length > 0 && signal.Length > 0 ? $"{battery}, {signal}" : battery + signal;
        }
    }

    [Header("Network")]
    public int listenPort = 5005;
    public int commandPort = 5006;
    public string bridgeHost = "127.0.0.1";

    [Header("Orientation")]
    // Settled on hardware (PodAxisTest, 2026-11-03): conversion 1, no flips. It's the only one
    // that maps the BNO's right-handed, Z-up frame to Unity's left-handed, Y-up frame; the
    // others are inverted or mirrored. 2 is the same rotation (q and -q).
    [Tooltip("1 = verified on hardware (BNO085 right-handed Z-up -> Unity left-handed Y-up), with no flips. Applied when read, so a change shows at once. 1 and 2 are the same rotation (q and -q).")]
    public int conversion = 1;

    // Mirror a Unity axis after the conversion, to fix a direction without code. Mirroring
    // X makes turns about Y and Z go the other way (turns about X don't change); likewise for
    // Y and Z. That's the only consistent way to flip an axis: if exactly one kind of turn is
    // backwards, the conversion itself is wrong - try another one first.
    [Tooltip("Mirror Unity's X axis: turns about Y and Z reverse, turns about X stay.")]
    public bool flipX;
    [Tooltip("Mirror Unity's Y axis: turns about X and Z reverse, turns about Y stay.")]
    public bool flipY;
    [Tooltip("Mirror Unity's Z axis: turns about X and Y reverse, turns about Z stay.")]
    public bool flipZ;

    [Header("Status (read only)")]
    public int connectedPods;
    public int targetPods;

    // Raw BNO085 quaternion (i, j, k, real) per pod, as received - converted on read.
    readonly Dictionary<int, Vector4> rawRotations = new Dictionary<int, Vector4>();
    readonly Dictionary<int, double> lastSeen = new Dictionary<int, double>();
    readonly Dictionary<int, bool> hasOrientation = new Dictionary<int, bool>();
    readonly Dictionary<int, long> packetCounts = new Dictionary<int, long>();
    readonly Dictionary<int, PodHealth> health = new Dictionary<int, PodHealth>();
    readonly Dictionary<int, double> healthSeen = new Dictionary<int, double>();
    readonly object gate = new object();

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double Now => clock.Elapsed.TotalSeconds;

    UdpClient listener;
    UdpClient sender;
    Thread rxThread;
    volatile bool running;
    int badLines;

    /// <summary>The server's built-in bridge: sends a cue line straight to the receiver's USB
    /// port. True if it went out; false (or null) falls back to UDP and bridge.py.</summary>
    public Func<string, bool> directCueOut;

    void Start()
    {
        sender = new UdpClient();
        try
        {
            listener = new UdpClient(listenPort);
        }
        catch (SocketException e)
        {
            // Another copy of the app on this PC (the server .exe, or the Editor) already listens.
            Debug.LogWarning($"PodReceiver: UDP {listenPort} is taken ({e.SocketErrorCode}) - another copy of the app on this PC hears bridge.py instead of this one.");
            return;
        }
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
            string line;
            try
            {
                line = Encoding.UTF8.GetString(listener.Receive(ref any));
            }
            catch (SocketException) { if (running) continue; break; }
            catch (ObjectDisposedException) { break; }

            // One bad line must never stop the thread.
            try
            {
                Parse(line.Trim());
            }
            catch (Exception e)
            {
                if (badLines++ < 5)
                    Debug.LogWarning($"PodReceiver: couldn't parse \"{line}\": {e.Message}");
            }
        }
    }

    /// <summary>A line from the receiver that didn't come over UDP (SerialBridge). Any thread.</summary>
    public void Feed(string line)
    {
        try
        {
            Parse(line.Trim());
        }
        catch (Exception e)
        {
            if (badLines++ < 5)
                Debug.LogWarning($"PodReceiver: couldn't parse \"{line}\": {e.Message}");
        }
    }

    static bool TryParseFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    void Parse(string line)
    {
        string[] f = line.Split(',');
        if (f.Length == 0) return;

        if (f[0] == "P" && f.Length == 6)
        {
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return;
            if (!TryParseFloat(f[2], out float qi)) return;
            if (!TryParseFloat(f[3], out float qj)) return;
            if (!TryParseFloat(f[4], out float qk)) return;
            if (!TryParseFloat(f[5], out float qr)) return;

            // All zeros = the pod has no IMU reading (yet). A real one is unit length.
            bool valid = qi * qi + qj * qj + qk * qk + qr * qr > 0.25f;

            lock (gate)
            {
                lastSeen[id] = Now;
                hasOrientation[id] = valid;
                packetCounts[id] = (packetCounts.TryGetValue(id, out long n) ? n : 0) + 1;
                if (valid)
                    rawRotations[id] = new Vector4(qi, qj, qk, qr);
            }
        }
        else if (f[0] == "H" && f.Length == 5)
        {
            // H,<id>,<battery_mV>,<charging>,<rssi>, every 2 s per connected pod.
            if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mv)) return;
            if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int charging)) return;
            if (!int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rssi)) return;

            lock (gate)
            {
                health[id] = new PodHealth { batteryMillivolts = mv, charging = charging, rssi = rssi };
                healthSeen[id] = Now;
            }
        }
        else if (f[0] == "S" && f.Length >= 3)
        {
            int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out connectedPods);
            int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetPods);
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
        Quaternion q;
        switch (conversion)
        {
            case 1:  q = new Quaternion(-i, -k, -j, r); break;
            case 2:  q = new Quaternion(i, k, j, -r); break;
            case 3:  q = new Quaternion(j, k, i, -r); break;
            default: q = new Quaternion(-i, -k, -j, -r); break;
        }
        return ApplyFlips(q);
    }

    // Mirroring Unity axes is a change of frame by S = diag(±1, ±1, ±1): the rotation's
    // vector part becomes det(S) · S · v, the real part stays. So flipping X alone gives
    // (x, -y, -z, w). Flipping all three changes nothing; that's expected (it's -I).
    Quaternion ApplyFlips(Quaternion q)
    {
        if (!flipX && !flipY && !flipZ)
            return q;

        float sx = flipX ? -1f : 1f, sy = flipY ? -1f : 1f, sz = flipZ ? -1f : 1f;
        float det = sx * sy * sz;
        return new Quaternion(det * sx * q.x, det * sy * q.y, det * sz * q.z, q.w);
    }

    // ---- public API -------------------------------------------------------

    /// <summary>Latest orientation for a pod, with the current conversion. Identity if never seen.</summary>
    public Quaternion GetRotation(int podId)
    {
        Vector4 q;
        lock (gate)
        {
            if (!rawRotations.TryGetValue(podId, out q))
                return Quaternion.identity;
        }
        return ToUnity(q.x, q.y, q.z, q.w);
    }

    /// <summary>A raw BNO085 quaternion (i, j, k, real) in Unity, with the current conversion
    /// and flips - the same as GetRotation applies.</summary>
    public Quaternion ToUnityRotation(Vector4 raw) => ToUnity(raw.x, raw.y, raw.z, raw.w);

    /// <summary>The pod's latest real sample as the BNO085 sent it (i, j, k, real), before
    /// any conversion. False if it never sent a real one.</summary>
    public bool TryGetRawRotation(int podId, out Vector4 raw)
    {
        lock (gate)
            return rawRotations.TryGetValue(podId, out raw);
    }

    /// <summary>How many P lines that pod has sent since this receiver started, the
    /// all-zero "no IMU" ones included. Sample twice to get packets per second.</summary>
    public long GetPacketCount(int podId)
    {
        lock (gate)
            return packetCounts.TryGetValue(podId, out long n) ? n : 0;
    }

    /// <summary>True if that pod sent data in the last `timeout` seconds.</summary>
    public bool IsAlive(int podId, float timeout = 1f)
    {
        lock (gate)
            return lastSeen.TryGetValue(podId, out double t) && Now - t < timeout;
    }

    /// <summary>True if that pod is alive and its latest sample was a real IMU reading
    /// (not the all-zero "no IMU" quaternion).</summary>
    public bool HasOrientation(int podId, float timeout = 1f)
    {
        lock (gate)
            return lastSeen.TryGetValue(podId, out double t) && Now - t < timeout
                   && hasOrientation.TryGetValue(podId, out bool ok) && ok;
    }

    public int[] ActivePods()
    {
        lock (gate)
        {
            double now = Now;
            var list = new List<int>();
            foreach (var kv in lastSeen)
                if (now - kv.Value < 1.0) list.Add(kv.Key);
            return list.ToArray();
        }
    }

    /// <summary>The pod's battery, charge status and signal, from its latest H line. False if
    /// none arrived in the last `timeout` seconds (they come every 2 s while it's connected).</summary>
    public bool TryGetHealth(int podId, out PodHealth podHealth, float timeout = 5f)
    {
        lock (gate)
        {
            if (healthSeen.TryGetValue(podId, out double t) && Now - t < timeout)
                return health.TryGetValue(podId, out podHealth);
        }
        podHealth = default;
        return false;
    }

    /// <summary>Fire a cue. podId 0 broadcasts to every pod.</summary>
    public void SendCue(int podId, Cue cue, int param = 0)
    {
        string msg = $"C,{podId},{(int)cue},{Mathf.Clamp(param, 0, 255)}";
        if (directCueOut != null && directCueOut(msg))
            return;
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
