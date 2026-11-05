using System.Collections.Generic;
using UnityEngine;

// One pod's state as every machine sees it: the machine with the pods builds it from PodReceiver
// and sends it through AvatarPoseModel.podStatus, since the headsets never hear the pods.
public struct PodStatusEntry
{
    public int podId;
    public HumanBodyBones bone;
    public bool connected;
    public bool hasSensor;     // connected, and the IMU is reporting
    public bool hasHealth;     // an H line arrived recently
    public PodReceiver.PodHealth health;
    public bool low;           // set by PodStatusController, which keeps it from flickering

    public string Name => $"Pod {podId} · {PodStatus.BodyPart(bone)}";
}

public static class PodStatus
{
    // Long enough that the pods' sample gaps don't read as a pod coming and going.
    const float ConnectedTimeoutSeconds = 3f;
    const float HealthTimeoutSeconds = 6f;

    // From pod-reference.md: 3.2 m in the open reads -55 to -70 dBm, with a body in the way
    // about -85, and the receiver's floor is about -95.
    const int GoodSignalDbm = -70;
    const int FairSignalDbm = -85;

    // id, flags, battery mV (uint16 LE), RSSI (sbyte)
    const int BytesPerPod = 5;
    const int FlagConnected = 1, FlagSensor = 2, FlagHealth = 4, FlagCharging = 8, FlagNotCharging = 16;

    public static string BodyPart(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.LeftLowerArm: return "Muñeca izquierda";
            case HumanBodyBones.RightLowerArm: return "Muñeca derecha";
            case HumanBodyBones.LeftUpperArm: return "Bíceps izquierdo";
            case HumanBodyBones.RightUpperArm: return "Bíceps derecho";
            case HumanBodyBones.LeftUpperLeg: return "Cuádriceps izquierdo";
            case HumanBodyBones.RightUpperLeg: return "Cuádriceps derecho";
            case HumanBodyBones.LeftLowerLeg: return "Espinilla izquierda";
            case HumanBodyBones.RightLowerLeg: return "Espinilla derecha";
            case HumanBodyBones.Chest:
            case HumanBodyBones.UpperChest: return "Esternón";
            default: return bone.ToString();
        }
    }

    public static string SignalLabel(int rssi)
    {
        if (rssi == 0)
            return "—";
        if (rssi >= GoodSignalDbm)
            return "Señal buena";
        return rssi >= FairSignalDbm ? "Señal regular" : "Señal mala";
    }

    // Every pod in the mapping, connected or not, so a missing one still has a row.
    public static byte[] Encode(PodReceiver receiver, List<PodAvatar.PodBone> mapping)
    {
        var data = new byte[mapping.Count * BytesPerPod];
        for (int i = 0; i < mapping.Count; i++)
        {
            int id = mapping[i].podId;
            int flags = 0;
            if (receiver.IsAlive(id, ConnectedTimeoutSeconds))
                flags |= FlagConnected;
            if (receiver.HasOrientation(id, ConnectedTimeoutSeconds))
                flags |= FlagSensor;

            int millivolts = 0, rssi = 0;
            if (receiver.TryGetHealth(id, out PodReceiver.PodHealth health, HealthTimeoutSeconds))
            {
                flags |= FlagHealth;
                if (health.charging == 1)
                    flags |= FlagCharging;
                else if (health.charging == 0)
                    flags |= FlagNotCharging;
                millivolts = Mathf.Clamp(health.batteryMillivolts, 0, ushort.MaxValue);
                rssi = Mathf.Clamp(health.rssi, sbyte.MinValue, sbyte.MaxValue);
            }

            int o = i * BytesPerPod;
            data[o] = (byte)id;
            data[o + 1] = (byte)flags;
            data[o + 2] = (byte)millivolts;
            data[o + 3] = (byte)(millivolts >> 8);
            data[o + 4] = (byte)(sbyte)rssi;
        }
        return data;
    }

    // Which pods are connected, packed, to notice a change without comparing batteries.
    public static int ConnectedMask(byte[] data)
    {
        int mask = 0;
        for (int o = 0; data != null && o + BytesPerPod <= data.Length; o += BytesPerPod)
        {
            if ((data[o + 1] & FlagConnected) != 0)
                mask |= 1 << data[o];
        }
        return mask;
    }

    public static bool Decode(byte[] data, List<PodAvatar.PodBone> mapping, List<PodStatusEntry> result)
    {
        result.Clear();
        if (data == null || data.Length < BytesPerPod)
            return false;

        for (int o = 0; o + BytesPerPod <= data.Length; o += BytesPerPod)
        {
            int flags = data[o + 1];
            var entry = new PodStatusEntry
            {
                podId = data[o],
                bone = HumanBodyBones.LastBone,
                connected = (flags & FlagConnected) != 0,
                hasSensor = (flags & FlagSensor) != 0,
                hasHealth = (flags & FlagHealth) != 0,
                health = new PodReceiver.PodHealth
                {
                    batteryMillivolts = data[o + 2] | (data[o + 3] << 8),
                    charging = (flags & FlagCharging) != 0 ? 1 : (flags & FlagNotCharging) != 0 ? 0 : -1,
                    rssi = (sbyte)data[o + 4],
                },
            };

            foreach (PodAvatar.PodBone pod in mapping)
            {
                if (pod.podId == entry.podId)
                    entry.bone = pod.bone;
            }
            result.Add(entry);
        }
        return true;
    }
}
