using System.Collections.Generic;
using UnityEngine;

// Plays a shot's haptic cue timeline (CueTrack, made by CueGenerator from the preferida take)
// on the pods, from Acción. Timeline mode only for now; the live cues (proximity, orientation
// and timing compared with the reference as the actor moves) come next.
//
// It runs on the machine with the pods - the one whose PodReceiver hears them - and does
// nothing anywhere else. What starts and stops a take there:
//   Ensayo Remoto  the director recording (RehearsalSessionState.isRecording, synced): Acción
//                  when it starts, Corte when it stops. An escena completa starts on the scene's
//                  first shot; each Siguiente toma loads the next shot's cues from that moment
//                  (Acción only at the start, Corte only at the end).
//   Ensayar Solo   the actor's Iniciar / Parar (SoloSession's commands from the Quest, or
//                  SoloRehearsalController's events when the pods are on the same machine).
// In Ensayo Remoto the director can turn the guide cues off (RehearsalSessionSync.GuideCuesOn,
// read at Acción, for the whole take): then only Acción and Corte play - the pilot's "sin
// pistas" condition. Solo always plays everything.
//
// Scene 3's robot (DronePlan): as it crosses in front of the actor, right biceps then left biceps.
// Scene 1's giant (GolemPlan): its rumble and footfalls are felt from the set's action trigger -
// the director's, synced as a room time with its shot, so a "Repetir" plays them again; in Solo
// from Acción. Tremor = both shins, footfalls = both thighs, the alert after the rumble = sternum.
// A shot with a heartbeat (Latido, EmotionalPacing: DOUBLE on the sternum at its beats per
// minute) leaves the sternum to it: no live proximity, no Marca.
//
// Next to the timeline, the live cues (LiveCues: proximity to the next mark on the sternum, and
// orientation corrections on the wrists), from where the actor's headset actually is; when they
// run, the timeline's Marca is left out (the live one comes on arrival). Not without the headset
// (a shoot day) and not with the guide cues off.
// Stopping early cancels whatever hadn't played and sends Corte to everyone, unless the
// timeline's own Corte already went out.
//
// Each cue kind becomes firmware patterns (PodReceiver.Cue) here, so the stored timeline only
// says what a cue means:
//   Acción  TRIPLE, every pod          Corte  LONG 500 ms, every pod
//   Giro    CLICK, that side's wrist    Marca  DOUBLE, sternum
//   Pasos   STEP on the shins (buzz, silence, buzz), alternating left/right, at the walk's cadence
//   Aviso   RISE on the sternum: four buzzes rising 40-100% (firmware v5)
//   Pasos from a body: one STEP per real step, on that leg's shin, as long as the gap allows
//   Proximidad (the hand)  CLICKs on that wrist closer and closer over the reach, DOUBLE there
//   Contacto  overlapping LONG pulses on that wrist for the whole hold (a sustained buzz)
//   Sostener  LONG 600 ms on that wrist at the start of the hold, DOUBLE when it ends
public class CueEngine : MonoBehaviour
{
    public static CueEngine Instance { get; private set; }

    const int CutParam = 50;                       // LONG 500 ms
    const float AfterAccion = 0.5f;                // s: Acción's three pulses are over
    const float ProximityFirstGap = 0.45f;         // s between clicks as the reach starts...
    const float ProximityLastGap = 0.1f;           // ...and as it arrives
    const float ContactRepeat = 1f;                // a new long pulse every second...
    const float ContactPulse = 1.2f;               // ...each 1.2 s, so they overlap
    const int HoldPulseParam = 60;                 // LONG 600 ms: "hold it there"
    // A step: the pods' STEP pattern (firmware v5): buzz, silence, buzz, 150:60:150, 360 ms at
    // most, and never more than 65% of the time to the next step so fast walking doesn't blur
    // them (200 ms at least). Chosen by feel on Nov 9 over CLICK, DOUBLE and single buzzes,
    // which are barely felt on a moving shin.
    const float StepMaxSeconds = 0.36f;
    const float StepMinSeconds = 0.2f;
    const float StepShareOfInterval = 0.65f;

    static int StepParam(float secondsToNextStep) =>
        Mathf.RoundToInt(Mathf.Clamp(secondsToNextStep * StepShareOfInterval, StepMinSeconds, StepMaxSeconds) * 100f);

    [Tooltip("Off only for testing without pods: plays (and logs) the timeline anyway.")]
    public bool requirePods = true;
    [Tooltip("Logs every cue without sending it to the pods.")]
    public bool dryRun;

    struct Scheduled
    {
        public float At;
        public int Pod;
        public PodReceiver.Cue Cue;
        public int Param;
        public string Label;
    }

    readonly List<Scheduled> queue = new List<Scheduled>();
    PodReceiver receiver;
    bool playing;
    bool cutPlayed;
    bool guideOff;      // this take: Acción and Corte only
    LiveCues live;
    double seenSetActionTime;
    bool wasRecording;
    int sceneNumber;
    readonly List<int> sceneShots = new List<int>();
    int shotIndex;
    RehearsalSessionState sessionState;
    SoloSession soloSession;

    public bool IsPlaying => playing;

    // Acción on the machine with the pods (whether or not the shot has cues).
    public static event System.Action TakeStarted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        if (Instance != null)
            return;
        var engine = new GameObject("CueEngine");
        DontDestroyOnLoad(engine);
        engine.AddComponent<CueEngine>();
    }

    void Awake()
    {
        Instance = this;
        SoloRehearsalController.RunStarted += OnLocalSoloStarted;
        SoloRehearsalController.RunStopped += OnLocalSoloStopped;
        RehearsalSessionState.CommandSentLocally += OnSessionCommand;
    }

    void OnDestroy()
    {
        SoloRehearsalController.RunStarted -= OnLocalSoloStarted;
        SoloRehearsalController.RunStopped -= OnLocalSoloStopped;
        RehearsalSessionState.CommandSentLocally -= OnSessionCommand;
        if (sessionState != null)
            sessionState.OnCommand -= OnSessionCommand;
        if (soloSession != null)
            soloSession.OnCommand -= OnSoloCommand;
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        HookSessions();

        // Ensayo Remoto: the director's recording, as every machine sees it.
        bool recording = sessionState != null && sessionState.IsRecording;
        if (recording && !wasRecording)
            StartTake(sessionState.SceneNumber, sessionState.ShotNumber, "the director started recording", sessionState.GuideCuesOff);
        else if (!recording && wasRecording)
            StopTake("the director stopped recording");
        wasRecording = recording;

        // The giant's moment, as the director triggered it (Acción, or Repetir).
        double setActionTime = sessionState != null ? sessionState.SetActionTime : 0;
        if (setActionTime != seenSetActionTime)
        {
            seenSetActionTime = setActionTime;
            if (setActionTime > 0 && recording)
                ScheduleSetAction(sessionState.SetActionShot, (float)(sessionState.RoomTime - setActionTime), "the director's trigger");
        }

        if (!playing)
            return;

        float now = Time.time;
        if (live != null)
        {
            ActorPositionSync head = ActorPositionSync.Instance;
            AvatarPoseSync avatar = AvatarPoseSync.Instance;
            if (head != null && head.IsLive)
            {
                Vector3 forward = head.transform.forward;
                live.Update(now, head.transform.position, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg,
                    avatar != null && avatar.HasBody, avatar != null && avatar.HasBody ? avatar.BodyYaw : 0f);
            }
        }
        for (int i = 0; i < queue.Count; i++)
        {
            if (queue[i].At > now)
                continue;
            Fire(queue[i]);
            queue.RemoveAt(i--);
        }
    }

    // The session objects live under Bootstrap and may come up after this.
    void HookSessions()
    {
        if (sessionState == null)
        {
            sessionState = FindFirstObjectByType<RehearsalSessionState>();
            if (sessionState != null)
            {
                sessionState.OnCommand += OnSessionCommand;
                wasRecording = sessionState.IsRecording;
            }
        }
        if (soloSession == null && SoloSession.Instance != null)
        {
            soloSession = SoloSession.Instance;
            soloSession.OnCommand += OnSoloCommand;
        }
    }

    void OnSessionCommand(SessionCommand command)
    {
        if (command == SessionCommand.SiguienteToma && playing && sceneShots.Count > 0)
            NextShot();
    }

    void OnSoloCommand(SoloCommand command)
    {
        if (command == SoloCommand.Iniciar && soloSession.InSolo)
        {
            StartTake(soloSession.SceneNumber, soloSession.ShotNumber, "the actor's solo Iniciar");
            ScheduleSetAction(soloSession.ShotNumber, 0f, "solo Iniciar");
        }
        else if (command == SoloCommand.Parar)
            StopTake("the actor's solo Parar");
    }

    void OnLocalSoloStarted(int shot)
    {
        StartTake(Bootstrap.CurrentSceneNumber, shot, "solo run on this machine");
        ScheduleSetAction(shot, 0f, "solo run on this machine");
    }

    static bool IsSetActionSend(Scheduled s) => s.Label != null && (s.Label.StartsWith("Gigante") || s.Label.StartsWith("Robot"));

    // What the set's action (the giant, the robot) makes the actor feel in that shot, from its
    // trigger (since = seconds ago).
    void ScheduleSetAction(int shot, float since, string reason)
    {
        if (!playing || guideOff)
            return;
        if (shot == DronePlan.Shot)
        {
            queue.RemoveAll(IsSetActionSend);
            float from = Time.time - Mathf.Max(0f, since);
            Add(from + DronePlan.PassRightAt, PodIds.RightBicep, PodReceiver.Cue.Long, DronePlan.PassParam, "Robot: pasa (derecha)");
            Add(from + DronePlan.PassLeftAt, PodIds.LeftBicep, PodReceiver.Cue.Long, DronePlan.PassParam, "Robot: pasa (izquierda)");
            queue.RemoveAll(s => IsSetActionSend(s) && s.At < Time.time - 0.25f);
            queue.Sort((a, b) => a.At.CompareTo(b.At));
            Debug.Log($"[CueEngine] the robot in shot {shot} ({reason}, {since:F1} s ago): its pass, right then left biceps");
            return;
        }
        if (!GolemPlan.HasAction(shot))
            return;

        // A repeat replaces what was still pending from the last one.
        queue.RemoveAll(IsSetActionSend);
        float start = Time.time - Mathf.Max(0f, since);
        GolemPlan.Action action = GolemPlan.For(shot);
        int count = 0;
        if (action.Kind == GolemPlan.Kind.Rumble)
        {
            float at = start + action.Delay + GolemPlan.RumbleTremorAt;
            Add(at, PodIds.LeftShin, PodReceiver.Cue.Pulse, GolemPlan.TremorGapParam, "Gigante: temblor");
            Add(at, PodIds.RightShin, PodReceiver.Cue.Pulse, GolemPlan.TremorGapParam, "Gigante: temblor");
            Add(at + GolemPlan.RumbleTremorSeconds, PodIds.LeftShin, PodReceiver.Cue.Stop, 0, "Gigante: fin del temblor");
            Add(at + GolemPlan.RumbleTremorSeconds, PodIds.RightShin, PodReceiver.Cue.Stop, 0, "Gigante: fin del temblor");
            Add(start + action.Delay + GolemPlan.RumbleAlertAt, PodIds.Sternum, PodReceiver.Cue.Rise, 0, "Gigante: aviso");
            count = 5;
        }
        foreach (float t in GolemPlan.Footfalls(shot))
        {
            Add(start + t, PodIds.LeftQuad, PodReceiver.Cue.Long, GolemPlan.ThudParam, "Gigante: pisada");
            Add(start + t, PodIds.RightQuad, PodReceiver.Cue.Long, GolemPlan.ThudParam, "Gigante: pisada");
            count += 2;
        }
        queue.RemoveAll(s => IsSetActionSend(s) && s.At < Time.time - 0.25f);
        queue.Sort((a, b) => a.At.CompareTo(b.At));
        Debug.Log($"[CueEngine] the giant in shot {shot} ({reason}, {since:F1} s ago): {count} sends");
    }
    void OnLocalSoloStopped() => StopTake("solo run stopped on this machine");

    // The receiver that hears pods (Bootstrap's ActorAvatar has one; test scenes their own).
    PodReceiver Receiver()
    {
        if (receiver != null && receiver.isActiveAndEnabled)
            return receiver;
        receiver = null;
        foreach (PodReceiver candidate in FindObjectsByType<PodReceiver>(FindObjectsSortMode.None))
        {
            if (receiver == null || candidate.ActivePods().Length > receiver.ActivePods().Length)
                receiver = candidate;
        }
        return receiver;
    }

    bool HasPods => Receiver() != null && Receiver().ActivePods().Length > 0;

    public void StartTake(int scene, int shot, string reason, bool guideCuesOff = false)
    {
        if (requirePods && !HasPods)
        {
            Debug.Log($"[CueEngine] {reason}: no pods on this machine, timeline not played");
            return;
        }

        TakeStarted?.Invoke();
        queue.Clear();
        sceneShots.Clear();
        cutPlayed = false;
        playing = true;
        sceneNumber = scene;
        guideOff = guideCuesOff;
        if (guideOff)
            Debug.Log("[CueEngine] the director turned the guide cues off: Acción and Corte only");

        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        if (database == null)
        {
            Debug.LogError("[CueEngine] no ShotListDatabase");
            playing = false;
            return;
        }

        if (shot == RehearsalSessionState.FullSceneShot)
        {
            sceneShots.AddRange(database.GetShotNumbersOfScene(scene));
            shotIndex = 0;
            if (sceneShots.Count == 0)
            {
                Debug.LogWarning($"[CueEngine] scene {scene} has no shots");
                playing = false;
                return;
            }
            Debug.Log($"[CueEngine] Acción ({reason}): escena completa {scene}, shots {string.Join(",", sceneShots)}");
            Schedule(database, sceneShots[0], first: true, last: false);
        }
        else
        {
            Debug.Log($"[CueEngine] Acción ({reason}): scene {scene}, shot {shot}");
            Schedule(database, shot, first: true, last: true);
        }
    }

    void NextShot()
    {
        if (shotIndex + 1 >= sceneShots.Count)
            return;

        // Whatever the last shot still had pending is dropped: the director moved on.
        queue.Clear();
        EndLive();
        shotIndex++;
        ShotListDatabase database = FindFirstObjectByType<ShotListDatabase>();
        Debug.Log($"[CueEngine] Siguiente toma: shot {sceneShots[shotIndex]}");
        if (database != null)
            Schedule(database, sceneShots[shotIndex], first: false, last: false);
    }

    public void StopTake(string reason)
    {
        if (!playing)
            return;

        playing = false;
        EndLive();
        int dropped = queue.Count;
        queue.Clear();
        PodReceiver pods = dryRun ? null : Receiver();
        if (pods != null)
        {
            pods.SendCue(PodIds.All, PodReceiver.Cue.Stop);
            if (!cutPlayed)
                pods.SendCue(PodIds.All, PodReceiver.Cue.Long, CutParam);
        }
        Debug.Log($"[CueEngine] Corte ({reason}): {dropped} cues not played" + (cutPlayed ? "" : ", Corte sent"));
    }

    // The shot's cues from now on, as firmware sends.
    void Schedule(ShotListDatabase database, int shot, bool first, bool last)
    {
        List<CueEvent> cues = database.GetCueTrack(shot);
        if (cues.Count == 0)
        {
            Debug.Log($"[CueEngine] shot {shot} has no cues (no preferida take yet)");
            FloatingMessage.Show($"Shot {shot}: sin pistas hápticas (marca una toma preferida)");
            if (first)
                Add(Time.time, PodIds.All, PodReceiver.Cue.Triple, 0, "Acción");
            return;
        }

        float start = Time.time;
        int count = 0;

        // The live cues, when the actor's headset is here to say where they are.
        live = null;
        if (!guideOff && ActorPositionSync.Instance != null && ActorPositionSync.Instance.IsLive)
        {
            live = LiveCues.Load(database, shot, cues, SendLive);
            live?.Begin(start);
        }
        bool heartbeat = cues.Exists(c => c.Kind == CueKind.EmotionalPacing);
        if (heartbeat && live != null)
            live.ProximityEnabled = false;
        bool liveMarks = live != null && live.MarkCount > 0 && !heartbeat;

        foreach (CueEvent cue in cues)
        {
            float at = start + cue.T;
            if (guideOff && cue.Kind != CueKind.CameraStart && cue.Kind != CueKind.Cut)
                continue;
            // Acción's TRIPLE goes to every pod and would cut anything sent with it: a cue in the
            // take's first moments (a turn right at the start) waits until it's over.
            if (first && cue.Kind != CueKind.CameraStart && cue.T < AfterAccion)
                at = start + AfterAccion;
            if ((liveMarks || heartbeat) && cue.Kind == CueKind.Checkpoint)
                continue;
            switch (cue.Kind)
            {
                case CueKind.CameraStart:
                    if (first) Add(at, PodIds.All, PodReceiver.Cue.Triple, 0, "Acción");
                    break;
                case CueKind.Cut:
                    if (last) Add(at, PodIds.All, PodReceiver.Cue.Long, CutParam, "Corte");
                    break;
                case CueKind.Orientation:
                    Add(at, cue.Pod, PodReceiver.Cue.Click, 0, $"Giro {cue.Param:+0;-0}°");
                    break;
                case CueKind.Checkpoint:
                case CueKind.MarkConfirmation:
                    Add(at, cue.Pod != PodIds.All ? cue.Pod : PodIds.Sternum, PodReceiver.Cue.Double, 0, HapticCues.Name(cue.Kind));
                    break;
                case CueKind.Anticipation:
                    Add(at, PodIds.Sternum, PodReceiver.Cue.Rise, 0, "Aviso");
                    break;
                case CueKind.Timing when cue.Dur <= 0f:
                    // A real step from the reference's body: that leg's shin, at that moment.
                    Add(at, cue.Pod, PodReceiver.Cue.Step, StepParam(NextStepAfter(cues, cue.T) - cue.T), "Paso");
                    break;
                case CueKind.Proximity:
                    // The hand nearing its point: clicks closer and closer, then a double there.
                    for (float s = 0f; s < cue.Dur;)
                    {
                        Add(at + s, cue.Pod, PodReceiver.Cue.Click, 0, "Proximidad");
                        s += Mathf.Lerp(ProximityFirstGap, ProximityLastGap, s / cue.Dur);
                    }
                    Add(at + cue.Dur, cue.Pod, PodReceiver.Cue.Double, 0, "Proximidad: llegaste");
                    break;
                case CueKind.Contact:
                    // A sustained buzz: long pulses that overlap (the motor stops on its own).
                    for (float s = 0f; s < cue.Dur; s += ContactRepeat)
                        Add(at + s, cue.Pod, PodReceiver.Cue.Long, Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(ContactPulse, cue.Dur - s + 0.1f) * 100f), 10, 255), "Contacto");
                    break;
                case CueKind.EmotionalPacing:
                    // A heartbeat: lub-dub (DOUBLE) at its beats per minute, over the span.
                    float beat = 60f / Mathf.Clamp(cue.Param > 0f ? cue.Param : 120f, 40f, 180f);
                    for (float s = 0f; s < cue.Dur; s += beat)
                        Add(at + s, cue.Pod != PodIds.All ? cue.Pod : PodIds.Sternum, PodReceiver.Cue.Double, 0, "Latido");
                    break;
                case CueKind.HoldPosition:
                    Add(at, cue.Pod, PodReceiver.Cue.Long, HoldPulseParam, "Sostener");
                    Add(at + cue.Dur, cue.Pod, PodReceiver.Cue.Double, 0, "Sostener: listo");
                    break;
                case CueKind.Timing:
                    float step = 1f / Mathf.Max(cue.Param, 0.5f);
                    int n = 0;
                    for (float s = 0f; s < cue.Dur; s += step, n++)
                        Add(at + s, n % 2 == 0 ? PodIds.LeftShin : PodIds.RightShin, PodReceiver.Cue.Step, StepParam(step), "Paso");
                    break;
                default:
                    Debug.Log($"[CueEngine] {cue.Kind} not playable in timeline mode yet");
                    continue;
            }
            count++;
        }
        queue.Sort((a, b) => a.At.CompareTo(b.At));
        Debug.Log($"[CueEngine] shot {shot}: {count} cues, {queue.Count} sends scheduled");
    }

    // When the next real step of the reference comes (either leg); far off if none.
    static float NextStepAfter(List<CueEvent> cues, float t)
    {
        float next = float.MaxValue;
        foreach (CueEvent c in cues)
            if (c.Kind == CueKind.Timing && c.Dur <= 0f && c.T > t + 0.01f && c.T < next)
                next = c.T;
        return next == float.MaxValue ? t + 10f : next;
    }

    void Add(float at, int pod, PodReceiver.Cue cue, int param, string label) =>
        queue.Add(new Scheduled { At = at, Pod = pod, Cue = cue, Param = param, Label = label });

    void EndLive()
    {
        live?.End();
        live = null;
    }

    void SendLive(int pod, PodReceiver.Cue cue, int param, string label) =>
        Fire(new Scheduled { At = Time.time, Pod = pod, Cue = cue, Param = param, Label = "[vivo] " + label });

    void Fire(Scheduled s)
    {
        if (s.Cue == PodReceiver.Cue.Long && s.Pod == PodIds.All)
        {
            cutPlayed = true;
            EndLive();
        }
        PodReceiver pods = dryRun ? null : Receiver();
        if (pods != null)
            pods.SendCue(s.Pod, s.Cue, s.Param);
        Debug.Log($"[CueEngine] {s.Label} → pod {(s.Pod == PodIds.All ? "todos" : s.Pod.ToString())} {s.Cue}{(s.Param != 0 ? " " + s.Param : "")}");
    }
}
