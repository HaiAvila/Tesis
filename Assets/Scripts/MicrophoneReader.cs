using System.Collections.Generic;
using Normal.Realtime;
using UnityEngine;

// Reads this device's microphone as 16 kHz mono, for the director's voice notes and the take's
// conversation track, both at once if need be.
//
// The voice chat's microphone (Normcore's MicrophoneDevice.sharedMicrophone) if it's open, read
// directly: Normcore zeroes muted audio before handing it on, and a note is recorded with the
// voice chat muted. If it isn't open (no connection), one of our own, shared by every reader and
// closed with the last one. A ring buffer about a second long, so Read once a frame.
public class MicrophoneReader
{
    public const int SampleRate = 16000;

    static MicrophoneDevice ownMicrophone;
    static int ownUsers;

    MicrophoneDevice readingFrom;
    bool usingOwn;
    int readHead;
    float[] buffer = new float[0];
    float sum;
    int summed;
    int phase;

    public bool IsOpen => readingFrom != null;

    // The voice chat's microphone, if it's really open: after a disconnect Normcore has closed it,
    // but sharedMicrophone still points at it. It always opens the default device, "".
    static MicrophoneDevice SharedMicrophone => Microphone.IsRecording("") ? MicrophoneDevice.sharedMicrophone : null;

    public bool Begin()
    {
        MicrophoneDevice microphone = SharedMicrophone;
        if (microphone != null && microphone == ownMicrophone)
            microphone = null; // that's ours: counted below
        if (microphone == null)
        {
            if (ownMicrophone == null)
                ownMicrophone = MicrophoneDevice.Start("");
            if (ownMicrophone == null)
                return false;
            ownUsers++;
            usingOwn = true;
            microphone = ownMicrophone;
        }

        StartFrom(microphone);
        return true;
    }

    void StartFrom(MicrophoneDevice microphone)
    {
        readingFrom = microphone;
        readHead = microphone.deviceWriteHeadPosition;
        sum = 0f;
        summed = 0;
        phase = 0;
    }

    public void End()
    {
        readingFrom = null;
        if (!usingOwn)
            return;

        usingOwn = false;
        ownUsers--;
        if (ownUsers <= 0 && ownMicrophone != null)
        {
            // Unless the voice chat has taken the device over since: closing it would close theirs.
            if (MicrophoneDevice.sharedMicrophone == ownMicrophone)
                ownMicrophone.Dispose();
            ownMicrophone = null;
            ownUsers = 0;
        }
    }

    // Appends what arrived since the last call. silence: keep the time, drop the sound.
    public void Read(List<float> into, bool silence = false)
    {
        // The voice chat opened it again, a reconnect: that one is what's live now.
        MicrophoneDevice shared = SharedMicrophone;
        if (shared != null && shared != readingFrom && readingFrom != null)
            StartFrom(shared);
        if (readingFrom == null)
            return;

        int total = readingFrom.deviceBufferSampleCount;
        int head = readingFrom.deviceWriteHeadPosition;
        if (total <= 0 || head == readHead)
            return;

        if (head > readHead)
        {
            ReadRange(readHead, head - readHead, into, silence);
        }
        else
        {
            ReadRange(readHead, total - readHead, into, silence);
            ReadRange(0, head, into, silence);
        }
        readHead = head;
    }

    // Down to SampleRate by averaging: one output sample each time SampleRate more input samples'
    // worth of the device's rate has gone by, whatever that rate is.
    void ReadRange(int offset, int count, List<float> into, bool silence)
    {
        if (count <= 0)
            return;

        int channels = Mathf.Max(1, readingFrom.numberOfChannels);
        int rate = Mathf.Max(SampleRate, readingFrom.sampleRate);
        int length = count * channels;
        if (buffer.Length != length)
            buffer = new float[length];
        if (!readingFrom.GetBufferData(buffer, offset))
            return;

        for (int i = 0; i < length; i += channels)
        {
            float mono = 0f;
            for (int c = 0; c < channels; c++)
                mono += buffer[i + c];
            sum += mono / channels;
            summed++;

            phase += SampleRate;
            if (phase < rate)
                continue;

            phase -= rate;
            into.Add(silence ? 0f : sum / summed);
            sum = 0f;
            summed = 0;
        }
    }
}
