using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

// Persistent (lives under Bootstrap, DontDestroyOnLoad). Samples ActorPositionSync's
// synced transform into an in-memory buffer every 0.1s while recording, then flushes the
// whole buffer to the Sesiones/Metricas tables in one batch when stopped. Started and
// stopped directly with the right controller's Secondary Button ("B") - confirmed unused
// anywhere else in this project's input actions, unlike the Primary Button ("A"), which
// is already bound to Jump. DIRECTOR_BUILD only, and only while in a content scene.
public class RehearsalRecorder : MonoBehaviour
{
    const float SampleInterval = 0.1f;

    public static RehearsalRecorder Instance { get; private set; }

    public bool IsRecording { get; private set; }

    // Fired from the exact same start/stop calls that drive the recording itself, so a
    // UI indicator built from these can never drift out of sync with the real state.
    public event Action RecordingStarted;
    public event Action RecordingStopped;

    readonly List<Metric> buffer = new List<Metric>();

    Coroutine samplingCoroutine;
    float recordingStartTime;
    DateTime recordingStartedAt;
    int shotNumberAtStart;

#if DIRECTOR_BUILD
    InputAction recordingShortcutAction;
#endif

    void Awake()
    {
        Instance = this;

#if DIRECTOR_BUILD
        // Same standard OpenXR path convention as PauseMenuController's Menu binding,
        // plus the XR Device Simulator's equivalent control for in-Editor testing.
        recordingShortcutAction = new InputAction("RecordingShortcut", InputActionType.Button);
        recordingShortcutAction.AddBinding("<XRController>{RightHand}/{secondaryButton}");
        recordingShortcutAction.AddBinding("<XRSimulatedController>{RightHand}/secondaryButton");
        recordingShortcutAction.performed += OnRecordingShortcutPressed;
        recordingShortcutAction.Enable();
#endif
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;

#if DIRECTOR_BUILD
        recordingShortcutAction.performed -= OnRecordingShortcutPressed;
        recordingShortcutAction.Disable();
        recordingShortcutAction.Dispose();
#endif
    }

#if DIRECTOR_BUILD
    void OnRecordingShortcutPressed(InputAction.CallbackContext context)
    {
        if (!Bootstrap.IsContentScene(SceneManager.GetActiveScene().name))
            return;

        if (IsRecording)
            StopRecording();
        else
            StartRecording();

        SendHaptic();
    }

    void SendHaptic()
    {
        GameObject rightController = GameObject.Find("Right Controller");
        if (rightController == null)
            return;

        HapticImpulsePlayer hapticPlayer = rightController.GetComponent<HapticImpulsePlayer>();
        if (hapticPlayer != null)
            hapticPlayer.SendHapticImpulse(0.5f, 0.1f);
    }
#endif

    public void StartRecording()
    {
        if (IsRecording)
            return;

        buffer.Clear();
        shotNumberAtStart = Bootstrap.CurrentShotNumber;
        recordingStartedAt = DateTime.Now;
        recordingStartTime = Time.time;
        IsRecording = true;
        samplingCoroutine = StartCoroutine(SampleLoop());

        RecordingStarted?.Invoke();
    }

    public void StopRecording()
    {
        if (!IsRecording)
            return;

        IsRecording = false;
        if (samplingCoroutine != null)
            StopCoroutine(samplingCoroutine);

        RecordingStopped?.Invoke();

        float duration = Time.time - recordingStartTime;

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("RehearsalRecorder: no ShotListDatabase instance found, recording discarded.");
            buffer.Clear();
            return;
        }

        database.SaveRecordingSession(shotNumberAtStart, recordingStartedAt, duration, buffer);
        buffer.Clear();
    }

    IEnumerator SampleLoop()
    {
        var wait = new WaitForSeconds(SampleInterval);
        while (true)
        {
            SampleOnce();
            yield return wait;
        }
    }

    void SampleOnce()
    {
        if (ActorPositionSync.Instance == null)
            return;

        Transform actorTransform = ActorPositionSync.Instance.transform;
        Vector3 position = actorTransform.position;
        Quaternion rotation = actorTransform.rotation;

        buffer.Add(new Metric
        {
            TiempoRelativo = Time.time - recordingStartTime,
            PosX = position.x,
            PosY = position.y,
            PosZ = position.z,
            RotX = rotation.x,
            RotY = rotation.y,
            RotZ = rotation.z,
            RotW = rotation.w
        });
    }
}
