using System;
using System.IO;
using System.Text;
using UnityEngine;

// 16-bit PCM mono WAV, for the director's voice notes: plays anywhere, also outside the app.
public static class WavFile
{
    const int HeaderSize = 44;

    public static void Save(string path, float[] samples, int sampleRate)
    {
        var data = new byte[HeaderSize + samples.Length * 2];
        using (var stream = new MemoryStream(data))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(data.Length - 8);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);                 // format chunk size
            writer.Write((short)1);           // PCM
            writer.Write((short)1);           // mono
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);     // bytes per second
            writer.Write((short)2);           // bytes per sample
            writer.Write((short)16);          // bits per sample
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length * 2);
            foreach (float sample in samples)
                writer.Write((short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        File.WriteAllBytes(path, data);
    }

    // Null if it isn't a file Save wrote.
    public static AudioClip Load(string path)
    {
        if (!File.Exists(path))
            return null;

        byte[] data = File.ReadAllBytes(path);
        if (data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, 4) != "RIFF")
            return null;

        int sampleRate = BitConverter.ToInt32(data, 24);
        int count = (data.Length - HeaderSize) / 2;
        if (count == 0 || sampleRate <= 0)
            return null;

        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(data, HeaderSize + i * 2) / (float)short.MaxValue;

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), count, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
