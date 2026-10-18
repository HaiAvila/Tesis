# Unity work list — prioritized
Project "Tesis" (Unity 2022.3 LTS, Built-in RP, Normcore, SQLite). Deadline Nov 30.
Context: pod firmware does NOT stream yet → everything on the server must work first with a pod SIMULATOR behind the same interface as the real serial bridge.
Demo must show: remote rehearsal + IMU avatar, live haptic cues, shoot-day timeline mode, take report.
Each item: what · why · done when.

---

## P0 — Unblock and foundations (do first, ~week of Oct 17)

1. **Test controller ray UI on the real Quest (both builds)**
   - Why: everything else is untested on hardware until menus work.
   - Done when: every button in Actor/Director home menus, SceneSelect, pause menu, stop popup, playback panel and viewfinder works with the ray on a real Quest 3.

2. **SessionModel (RealtimeModel) — replace shot-in-transform hack + finish session state**
   - Fields: sceneNumber, shotNumber, mode (remote / solo / rodaje), sessionState (idle / active / ended), actorJoined, recording (bool), sessionId, commandId + commandCounter (calibrate, record, nextShot, accion, corte, save, discard, approve).
   - Remove the shot-encoded-in-position.x RehearsalSessionSync.
   - Done when: director selects shot / ends session and the actor follows; actor sent home on director leave; flags set at every entry and reset at every exit.

3. **Third build: SERVER_BUILD (Windows Standalone .exe)**
   - Same project, new scripting define. Boots into a `Server` scene: joins the Normcore room automatically, no XR rig, opens a desktop window UI.
   - Done when: double-clicking the .exe joins the same room as both Quests with no configuration.

4. **PodSource interface + PodSimulator**
   - `IPodSource` with events: OnPose(podId, quat, accuracy, steps, still, t), OnStatus(podId, battery, rssi, connected); method Send(string line).
   - `PodSimulator` implements it: 9 pods, quaternions from keyboard / sine motion / a recorded CSV, logs every command it receives.
   - Done when: the rest of the server (pose solver, logger, cues) runs end-to-end with no hardware.

5. **SerialBridge.cs (real IPodSource)**
   - System.IO.Ports, 115200, auto-detect the receiver COM port, background read thread → ConcurrentQueue → consumed in Update().
   - Parse `P,pod,w,x,y,z,acc,t` and `S,pod,bat,rssi,conn`; write `C,pod,cue,int,dur` · `T,pod,...` · `G` · `X` (pod 255 = all).
   - Player Settings → Api Compatibility Level → .NET Framework.
   - Done when: swapping PodSimulator for SerialBridge needs no other code change.

## P1 — Core loop: avatar, calibration, recording (~Nov 2–12)

6. **AvatarModel (RealtimeModel) + avatar in director view**
   - Root position/rotation + 9 bone quaternions in ONE model (not 9 RealtimeTransforms). Published by the server, rendered on a rigged humanoid in the director app.
   - Done when: director sees a full-body avatar moving from simulated pod data, root following the actor's headset.

7. **PoseSolver.cs (server)**
   - Axis conversion BNO085 → Unity (validate with one pod), pod→bone map (waist→hips, biceps→upper arms, wrists→forearms, quads→thighs, calves→shins), root from actor headset via Normcore, bone lengths from DB (manual or MediaPipe file).
   - Also outputs heading, steps, still for the cue engine.
   - Done when: avatar pose matches the simulator input with correct axes.

8. **Calibration flow**
   - Director presses Calibrar → SessionModel command → actor app shows "T-pose" instruction → server stores offset = Inverse(q_sensor) × q_bone_expected per pod in BD servidor → sends confirmation (message on actor Quest + short pulse to all pods).
   - Done when: pressing Calibrar once per session makes the avatar line up with the actor.

9. **Server-side recording (Logger) + server DB**
   - Server SQLite with: Shots (from CSV), Sesiones, PoseTrack (session, t, pod, quat), Metricas (headset), ShotBoundaries, ShotReference, CueTrack, Reportes, Offsets, Proporciones.
   - Record at 30 Hz on the director's Grabar, same sessionId and start time (realtime.room.time) as the director's copy. Guardar keeps it, Descartar deletes it, Preferida marks it as the reference.
   - Done when: one recording exists identically (same id, same clock) on director DB and server DB.

10. **Director DB: add PoseTrack + avatar replay**
    - Director recorder also stores the 9 quats from AvatarModel; ghost replay shows the full avatar, not just the headset.
    - Done when: Inspección replays the avatar pose.

11. **Scene rehearsal + shot slicing**
    - New mode "Ensayar escena" (default) beside "Ensayar toma". Button **Siguiente toma** stores timestamps in ShotBoundaries.
    - On approve: slice the scene into shots → ShotReference per shot; ShotStartPositioner uses the real start from the reference once it exists (Shot{N}_Start only until then).
    - Re-approving a single shot replaces only that slice; warn if its end is far from the next shot's start.
    - Done when: one continuous scene recording produces one reference per shot.

## P2 — Haptics (~Nov 9–20)

12. **Shared haptic vocabulary + command contract**
    - One C# enum of cue IDs matching firmware (MVP-5 + Backup-8). Document the line format in one file so firmware and Unity use the same IDs.
    - Done when: every cue in the vocabulary has an ID and a pattern name used by both sides.

13. **CueGenerator (server)**
    - From the approved reference: turns → orientation (L/R wrist), stops > ~1 s → mark + anticipatory alert before it, walking rhythm → calf metronome, start → camera-start (3 pulses), end → cut. Writes CueTrack rows (source = auto).
    - Done when: approving a take produces an automatic cue timeline.

14. **CueTrack sync**
    - Server → Normcore → director (auto cues); director edits → Normcore → server. Re-approval regenerates auto rows and keeps manual rows.
    - Done when: director edits survive a re-approval.

15. **CueEngine (server)**
    - Live mode (rehearsal): compare live pose vs reference at the same time → proximity (waist, rate on change only), orientation, timing. Timeline mode: play CueTrack from Acción.
    - Sends commands only on change; patterns live in firmware.
    - Done when: with the simulator, wrong position/heading produces the right C commands in the log.

16. **Haptic timeline editor (inside Inspeccionar escenas)**
    - Video-editor style: tracks per body zone (Todos, Cintura, Muñecas, Pantorrillas), drag cues from the library, fixed-length clips (Acción, Corte) vs stretchable clips, auto vs manual color, L/R and intensity on tap, playhead linked to the avatar ghost replay, 0.5 s snapping.
    - Works on desktop director build (mouse) and Quest (big clips + snap).
    - Done when: director adds "Objeto virtual" at second 7 for 2 s and it reaches the server.

17. **Structured cues per shot**
    - Replace free-text `notes` with a cue-ID column in the shot list (e.g. "1;4"); previz can tag virtual objects / danger zones in the scene for contact cues.
    - Done when: CueGenerator reads which cue types are active per shot.

## P3 — Shoot day, report, solo (~Nov 16–25)

18. **Server window UI**
    - Status: pods 9/9, battery per pod, COM port, Normcore connected. Rodaje controls: choose scene/shot, Cargar timeline (sends T to each pod), Acción (G broadcast), Corte (X).
    - Done when: a full shoot-day take runs from the PC with no Quest.

19. **Take report**
    - After Corte: error at each mark, early/late per shot, average orientation error vs reference → Reportes table → Normcore → panel in director app (and server window on shoot day).
    - Done when: director sees the report right after Corte.

20. **Ensayar solo (complete)**
    - Actor picks a shot with an approved reference → joins Normcore room without director → pods guide to start mark → practice with live + timeline cues; recorded like any take.
    - Done when: actor practices a shot alone end-to-end.

21. **Pod status in both Quest apps**
    - "Pods 9/9" + low battery warning from AvatarModel / status model.

22. **CSV export for Python**
    - Export each take + its reference from BD servidor (PoseTrack, Metricas, ShotBoundaries) for DTW and the iPhone/MediaPipe validation.

## P4 — Nice to have (only if time remains)

23. Bone lengths import from MediaPipe result file.
24. Free-fly camera in Ensayo remoto.
25. Shared and personal notes, voice notes, trajectory verification.
26. Simple scripted motion for creatures (golem, orb, drone).
27. Continuity warning UI polish, desktop director build polish.

---

## Suggested calendar
- Oct 17–24: P0 (1–5).
- Exam week (late Oct): only small items (2 or 4).
- Nov 2–12: P1 (6–11).
- Nov 9–20: P2 (12–17) — swap the simulator for real pods as soon as firmware streams.
- Nov 16–25: P3 (18–22).
- Nov 25–30: full demo run-throughs, bug fixing, nothing new.

## Rules for Claude Code (keep from CLAUDE.md)
- Diagnose with logs before fixing; one clean test at a time.
- Any state flag set at every entry point and reset at every exit.
- Discrete values over the network go through RealtimeModel properties, never transforms.
- Never move the user's head/camera by script.
- Keep Meta-specific code isolated in a few wrapper scripts.
