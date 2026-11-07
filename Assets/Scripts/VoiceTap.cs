using System.Collections.Generic;
using UnityEngine;

// Added by TakeAudioRecorder to the other person's VoiceClient copy, after Normcore's AudioOutput,
// so it sees the voice exactly as it's played here. Unity runs OnAudioFilterRead on the audio
// thread, in component order: being last, it gets the filled buffer. Only collects while a take
// is being recorded; the main thread takes the samples (Drain).
public class VoiceTap : MonoBehaviour
{
    readonly List<float> pending = new List<float>();
    readonly object gate = new object();
    volatile bool collecting;

    public bool Collecting
    {
        set
        {
            collecting = value;
            if (!value)
            {
                lock (gate)
                    pending.Clear();
            }
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!collecting)
            return;

        lock (gate)
        {
            for (int i = 0; i < data.Length; i += channels)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                    mono += data[i + c];
                pending.Add(mono / channels);
            }
        }
    }

    // At the output rate (AudioSettings.outputSampleRate), mono.
    public void Drain(List<float> into)
    {
        lock (gate)
        {
            into.AddRange(pending);
            pending.Clear();
        }
    }
}
