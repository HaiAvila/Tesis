using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// End-to-end haptic latency (CLAUDE.md, "Haptics work" item 3): Unity sends a cue to one pod
// and the PC's microphone hears its motor start. The whole real path is measured - UDP to
// bridge.py, serial to the receiver, BLE to the pod, the DRV2605L, the LRA spinning up, the
// sound in the air - plus the microphone's own input delay, so the result slightly
// OVERESTIMATES the true latency (the safe direction).
//
// The buzz is found by its frequency: the LRA resonates at ~235 Hz, so a Goertzel filter at that
// frequency runs over short windows of the recording. Before each cue the background level is
// measured; the onset is the first moment after the send where the 235 Hz energy stays well
// above it. Trials are spaced at random intervals so nothing locks to a rhythm.
//
// Scene LatencyTest, played on its own, with the bridge running (python bridge.py). Put one pod
// on a hard table right next to the PC's microphone, quiet room. Results go to Latency/ in the
// project folder (outside Assets): a CSV per run, its summary, and a WAV of the whole recording.
public class LatencyTest : MonoBehaviour
{
    public PodReceiver receiver;
    [Tooltip("The pod next to the microphone.")]
    public int podId = 1;
    public int trials = 30;
    public float minInterval = 1.5f;
    public float maxInterval = 2.5f;
    [Tooltip("LONG for this many ms: long enough to be clearly heard.")]
    public int buzzMs = 200;

    [Header("Detection")]
    public float targetHz = 235f;
    public float windowMs = 10f;
    public float hopMs = 1f;
    public float baselineMs = 300f;
    public float searchMs = 1000f;
    [Tooltip("Onset = energy above baseline mean + this many standard deviations...")]
    public float sigmas = 8f;
    [Tooltip("...and at least this many times the baseline mean...")]
    public float minRatio = 6f;
    [Tooltip("...for this many hops in a row.")]
    public int holdHops = 3;

    const int SampleRate = 48000;
    const int MicSeconds = 10;

    string device;
    AudioClip mic;
    int micRead;
    readonly List<float> recording = new List<float>();
    float[] chunk = new float[0];

    struct Trial { public int SendSample; public float LatencyMs; public float Ratio; }
    readonly List<Trial> results = new List<Trial>();
    readonly List<int> pending = new List<int>();
    bool running;
    int sent;
    float nextSendTime;
    int probeSample = -1;
    string status = "Listo. Coloca un pod junto al micrófono.";
    string summary = "";
    float liveLevel, liveBaseline;
    float liveDb = -120f, livePeakDb = -120f;

    void Start()
    {
        if (receiver == null)
            receiver = FindFirstObjectByType<PodReceiver>();
        if (Microphone.devices.Length == 0)
        {
            status = "No hay micrófono.";
            return;
        }
        UseMicrophone(0);
    }

    int deviceIndex;

    void UseMicrophone(int index)
    {
        if (device != null)
            Microphone.End(device);
        deviceIndex = index % Microphone.devices.Length;
        device = Microphone.devices[deviceIndex];
        mic = Microphone.Start(device, true, MicSeconds, SampleRate);
        micRead = 0;
        recording.Clear();
        liveBaseline = 0f;
        Debug.Log($"[Latency] microphone '{device}', {mic.frequency} Hz");
    }

    void OnDestroy()
    {
        if (device != null)
            Microphone.End(device);
    }

    void Update()
    {
        if (mic == null)
            return;

        ReadMicrophone();
        UpdateLiveLevel();

        // Idle: keep only the last couple of seconds (enough for a probe's baseline).
        if (!running && pending.Count == 0 && probeSample < 0 && recording.Count > SampleRate * 4)
            recording.RemoveRange(0, recording.Count - SampleRate * 2);

        if (running && sent < trials && Time.time >= nextSendTime)
            SendOne();

        AnalyzeReady();

        if (running && sent >= trials && pending.Count == 0)
            Finish();

        if (probeSample >= 0 && recording.Count >= probeSample + Samples(searchMs))
        {
            Trial t = Analyze(probeSample);
            status = float.IsNaN(t.LatencyMs)
                ? $"Prueba: no se oyó el pod (relación {t.Ratio:F1}). Acércalo al micrófono."
                : $"Prueba: {t.LatencyMs:F0} ms (relación {t.Ratio:F1}). Listo para la corrida.";
            probeSample = -1;
        }
    }

    // Everything the microphone wrote since the last frame, appended to the recording: its
    // index in the recording is the absolute time base for sends and onsets.
    void ReadMicrophone()
    {
        int position = Microphone.GetPosition(device);
        if (position < 0 || position == micRead)
            return;

        int count = position > micRead ? position - micRead : mic.samples - micRead + position;
        if (chunk.Length != count)
            chunk = new float[count];
        if (position > micRead)
        {
            mic.GetData(chunk, micRead);
        }
        else
        {
            var tail = new float[mic.samples - micRead];
            var head = new float[position];
            mic.GetData(tail, micRead);
            if (position > 0)
                mic.GetData(head, 0);
            tail.CopyTo(chunk, 0);
            head.CopyTo(chunk, tail.Length);
        }
        recording.AddRange(chunk);
        micRead = position;
    }

    void SendOne()
    {
        // Sent right after reading the microphone: what's in the recording is what was heard up
        // to now, so the send sits at its end.
        int at = recording.Count;
        receiver.SendCue(podId, PodReceiver.Cue.Long, Mathf.Clamp(buzzMs / 10, 1, 255));
        pending.Add(at);
        sent++;
        nextSendTime = Time.time + UnityEngine.Random.Range(minInterval, maxInterval);
        status = $"Corriendo: {sent}/{trials}";
    }

    void AnalyzeReady()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (recording.Count < pending[i] + Samples(searchMs))
                continue;
            Trial t = Analyze(pending[i]);
            results.Add(t);
            Debug.Log($"[Latency] trial {results.Count}: " + (float.IsNaN(t.LatencyMs) ? $"not detected (ratio {t.Ratio:F1})" : $"{t.LatencyMs:F1} ms (ratio {t.Ratio:F1})"));
            pending.RemoveAt(i);
        }
    }

    Trial Analyze(int sendSample)
    {
        int window = Samples(windowMs);
        int hop = Mathf.Max(1, Samples(hopMs));

        // Background: the windows that end before the send.
        var baseline = new List<float>();
        for (int s = sendSample - Samples(baselineMs); s + window <= sendSample; s += hop)
            if (s >= 0)
                baseline.Add(Goertzel(s, window));
        float mean = 0f, sd = 0f;
        foreach (float b in baseline) mean += b;
        mean /= Mathf.Max(1, baseline.Count);
        foreach (float b in baseline) sd += (b - mean) * (b - mean);
        sd = Mathf.Sqrt(sd / Mathf.Max(1, baseline.Count));
        float threshold = Mathf.Max(mean + sigmas * sd, mean * minRatio, 1e-9f);

        float peak = 0f;
        int run = 0, first = -1;
        int end = sendSample + Samples(searchMs) - window;
        for (int s = sendSample; s <= end; s += hop)
        {
            float p = Goertzel(s, window);
            peak = Mathf.Max(peak, p);
            if (p > threshold)
            {
                if (run == 0) first = s;
                if (++run >= holdHops)
                    return new Trial { SendSample = sendSample, LatencyMs = MsBetween(sendSample, first + window / 2), Ratio = peak / Mathf.Max(mean, 1e-9f) };
            }
            else
            {
                run = 0;
            }
        }
        return new Trial { SendSample = sendSample, LatencyMs = float.NaN, Ratio = peak / Mathf.Max(mean, 1e-9f) };
    }

    // Energy at targetHz over recording[start, start + length).
    float Goertzel(int start, int length)
    {
        float k = 2f * Mathf.Cos(2f * Mathf.PI * targetHz / SampleRate);
        float s1 = 0f, s2 = 0f;
        for (int i = 0; i < length; i++)
        {
            float s0 = recording[start + i] + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return (s1 * s1 + s2 * s2 - k * s1 * s2) / (length * length);
    }

    void UpdateLiveLevel()
    {
        int window = Samples(windowMs * 5);
        if (recording.Count < window)
            return;
        liveLevel = Goertzel(recording.Count - window, window);
        float sum = 0f, peak = 0f;
        for (int i = recording.Count - window; i < recording.Count; i++)
        {
            sum += recording[i] * recording[i];
            peak = Mathf.Max(peak, Mathf.Abs(recording[i]));
        }
        liveDb = 20f * Mathf.Log10(Mathf.Max(Mathf.Sqrt(sum / window), 1e-6f));
        livePeakDb = Mathf.Max(livePeakDb - 20f * Time.deltaTime, 20f * Mathf.Log10(Mathf.Max(peak, 1e-6f)));
        liveBaseline = Mathf.Lerp(liveBaseline <= 0f ? liveLevel : liveBaseline, liveLevel, 0.01f);
    }

    void StartRun()
    {
        if (receiver == null)
        {
            status = "Falta el PodReceiver.";
            return;
        }
        results.Clear();
        pending.Clear();
        // The run's WAV starts here; send times in the CSV are seconds into it.
        if (recording.Count > SampleRate)
            recording.RemoveRange(0, recording.Count - SampleRate);
        sent = 0;
        running = true;
        summary = "";
        nextSendTime = Time.time + 1f;
        status = "Corriendo...";
    }

    void Finish()
    {
        running = false;
        var detected = new List<float>();
        foreach (Trial t in results)
            if (!float.IsNaN(t.LatencyMs))
                detected.Add(t.LatencyMs);
        detected.Sort();

        if (detected.Count == 0)
        {
            summary = $"Ninguna de {results.Count} se detectó. Acerca el pod al micrófono o sube la duración.";
        }
        else
        {
            float mean = 0f;
            foreach (float d in detected) mean += d;
            mean /= detected.Count;
            summary = string.Format(CultureInfo.InvariantCulture,
                "Detectadas {0}/{1} · mediana {2:F0} ms · media {3:F0} ms · p90 {4:F0} ms · mín {5:F0} · máx {6:F0} ms",
                detected.Count, results.Count, Percentile(detected, 0.5f), mean, Percentile(detected, 0.9f), detected[0], detected[detected.Count - 1]);
        }
        Save();
        status = "Terminado.";
        Debug.Log($"[Latency] {summary}");
    }

    static float Percentile(List<float> sorted, float p)
    {
        float index = p * (sorted.Count - 1);
        int lo = Mathf.FloorToInt(index), hi = Mathf.CeilToInt(index);
        return Mathf.Lerp(sorted[lo], sorted[hi], index - lo);
    }

    void Save()
    {
        string folder = Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Latency"))
            : Path.Combine(Application.persistentDataPath, "Latency");
        Directory.CreateDirectory(folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var csv = new StringBuilder("intento,enviado_s,latencia_ms,relacion\n");
        for (int i = 0; i < results.Count; i++)
        {
            Trial t = results[i];
            csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F4},{2},{3:F2}",
                i + 1, t.SendSample / (float)SampleRate, float.IsNaN(t.LatencyMs) ? "" : t.LatencyMs.ToString("F1", CultureInfo.InvariantCulture), t.Ratio));
        }
        File.WriteAllText(Path.Combine(folder, $"latencia_{stamp}.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(folder, $"latencia_{stamp}_resumen.txt"),
            $"{summary}\npod {podId}, LONG {buzzMs} ms, {targetHz} Hz, micrófono '{device}' a {SampleRate} Hz\n" +
            "Incluye el retraso de entrada del micrófono: sobreestima un poco la latencia real.\n");
        WavFile.Save(Path.Combine(folder, $"latencia_{stamp}.wav"), recording.ToArray(), SampleRate);
        status = $"Guardado en {folder}";
        Debug.Log($"[Latency] saved to {folder} (latencia_{stamp}.*)");
    }

    int Samples(float ms) => Mathf.RoundToInt(ms * SampleRate / 1000f);
    static float MsBetween(int from, int to) => (to - from) * 1000f / SampleRate;

    void OnGUI()
    {
        GUI.skin.label.fontSize = 18;
        GUI.skin.button.fontSize = 18;
        GUILayout.BeginArea(new Rect(20, 20, 900, 600));
        GUILayout.Label($"Prueba de latencia: Unity → pod {podId} → sonido del motor ({targetHz} Hz)");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Micrófono: {(device ?? "ninguno")}");
        GUI.enabled = !running && Microphone.devices.Length > 1;
        if (GUILayout.Button("Cambiar", GUILayout.Width(120)))
            UseMicrophone(deviceIndex + 1);
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label($"Pods conectados: {(receiver != null ? receiver.connectedPods : 0)} · pod {podId} {(receiver != null && receiver.IsAlive(podId) ? "vivo" : "SIN SEÑAL")}");

        // A working mic in a quiet room sits around -60 to -45 dBFS and jumps when you talk;
        // stuck near -90 or lower = muted or blocked by Windows.
        GUILayout.Label($"Micrófono: {liveDb:F0} dBFS (pico {livePeakDb:F0})" + (livePeakDb < -80f ? "  ← NO OYE NADA: revisa que no esté silenciado ni bloqueado en Windows" : ""));
        float ratio = liveBaseline > 0f ? liveLevel / liveBaseline : 0f;
        GUILayout.Label($"Nivel a {targetHz:F0} Hz: {new string('|', Mathf.Clamp(Mathf.RoundToInt(Mathf.Log10(1f + ratio) * 20f), 0, 60))}  ×{ratio:F1}");

        GUILayout.BeginHorizontal();
        GUI.enabled = !running && probeSample < 0 && mic != null;
        if (GUILayout.Button("Probar un pulso", GUILayout.Width(220), GUILayout.Height(40)))
        {
            ReadMicrophone();
            probeSample = recording.Count;
            receiver.SendCue(podId, PodReceiver.Cue.Long, Mathf.Clamp(buzzMs / 10, 1, 255));
            status = "Probando...";
        }
        if (GUILayout.Button($"Correr {trials} intentos", GUILayout.Width(260), GUILayout.Height(40)))
            StartRun();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(status);
        if (summary.Length > 0)
            GUILayout.Label(summary);
        GUILayout.EndArea();
    }
}
