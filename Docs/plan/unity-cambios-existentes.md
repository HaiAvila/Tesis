# Unity — changes to what already exists (by screen/component)
Base: Tesis_Pipeline_Completo.md (Oct 14). Order = do top to bottom. Each change says what exists → what changes.

## 1. Normcore / Bootstrap  (do first: everything else depends on it)
- RehearsalSessionSync (shot in transform position.x) → replace with **SessionModel** (RealtimeModel): scene, shot, mode (remote/solo/rodaje), sessionState, actorJoined, recording, sessionId, command + counter (calibrate, grabar, siguienteToma, accion, corte, guardar, descartar, aprobar).
- Session active/ended + "actor joined" flags (in progress) → live inside SessionModel.
- Normcore "only matters in Ensayo Remoto" → **Ensayar solo also joins a room** (the PC server needs the actor's headset). Update IsInspecting / joined logic for that entry and its exit.
- Persistent shot listener → only acts when mode = remote and actorJoined.
- ActorPositionSync → keep (it becomes the avatar's root).
- Add: **AvatarModel** (root + 9 quats), **PodStatusModel** (9 pods: connected, battery), **ReportModel** (last take report), **CueTrackModel** (cues auto/edited for the current shot).
- Builds: add **SERVER_BUILD** (Windows Standalone .exe) beside DIRECTOR_BUILD; Bootstrap loads a `Server` scene for it.

## 2. Database (ShotListDatabase)
- Shots: keep `notes`, add **`cues`** column (cue IDs per shot, e.g. "1;4"), read from the CSV.
- Sesiones: add **scene_number**, **tipo** (escena / toma) and **session_id** (GUID shared with the server; today the id is local autoincrement → two devices can't match it).
- es_preferida → now means **approved reference**. Setting it also sends `aprobar` through SessionModel.
- New tables (ALTER/CREATE with your PRAGMA rule): **PoseTrack** (session, t, pod, quat), **ShotBoundaries** (session, shot, t_in, t_out), **CueTrack** (scene, shot, cue_id, track, side, t_in, duration, intensity, source auto/manual).
- Metricas every 0.1 s → keep as is on the director (server records its own copy at 30 Hz).

## 3. SceneSelect
- Today: scenes grouped with their shots → add a **"Escena completa"** entry at the top of each scene group (scene rehearsal), shots stay below (shot rehearsal).
- Mark shots that already have an approved reference (small icon). Ensayar solo only allows those.

## 4. ShotStartPositioner
- Today: moves rig to Shot{N}_Start → use the **real start from the approved reference** when it exists; Shot{N}_Start only until then.

## 5. Director · Ensayo Remoto
- Recording start/stop → becomes **Grabar → Acción** (start + camera-start cue) and **Corte** (stop + cut cue). Both go through SessionModel so the server records at the same time.
- Add **Siguiente toma** button (only in scene mode) → writes ShotBoundaries.
- Add **Calibrar** button (in director pause menu or a corner of the HUD).
- Actor representation (headset only) → **full avatar** from AvatarModel.
- Add **pod status** indicator (9/9, low battery) near the REC indicator.
- Stop popup: Ver replay / Guardar / Descartar / Preferida / Favorita → keep, plus:
  - Guardar/Descartar also tell the server to keep/delete its copy.
  - New button **Editar cues** (visible after Preferida) → opens the cue editor on the quick replay.
- After Corte: small **report panel** (from ReportModel).
- Quick replay → show the avatar; it hosts the cue editor when opened from the popup.

## 6. Director · Inspeccionar escenas  (main home of the cue editor)
- Takes list per shot → also list **scene recordings**, showing shot boundaries.
- Ghost replay (headset) → **avatar replay** (PoseTrack).
- Playback panel scrub slider → expose current time as an event; it is the **editor playhead**.
- Add **Editor háptico** panel under the replay: tracks Todos / Cintura / Muñecas / Pantorrillas, cue library to drag from, fixed vs stretchable clips, auto vs manual color, tap clip → side L/R + intensity + delete, 0.5 s snap. Guardar → CueTrackModel → server.
- Build the editor as **one prefab** reused by the quick replay (section 5).
- Favoritos → no change.
- The pending "haptic vocabulary editor" item = this editor (one thing, not two).

## 7. Actor · Home and Ensayo Remoto
- Ensayo Remoto waiting screen → add **pod status** (9/9) and the **T-pose instruction + confirmation** when the director calibrates.
- Show **start mark** marker on the floor when a shot loads.

## 8. Actor · Ensayar solo  (today: navigation only)
- SceneSelect local → only shots with a reference, then: join room (mode = solo) → go to mark (pods guide) → Iniciar / Parar buttons → the server plays cues and records.

## 9. Pause menu
- Director: add **Calibrar** and **Estado de pods**.
- Actor: add **Estado de pods**.

## 10. New: Server scene (SERVER_BUILD)
- Window UI: pods 9/9, battery, COM port, Normcore status; rodaje controls: escena/toma, Cargar timeline, Acción, Corte.
- Modules: SerialBridge (+ PodSimulator until firmware streams), PoseSolver, CueGenerator, CueEngine, Logger, server DB.

## Open questions from the Oct 14 file — now answered
1. Approved take reaches the actor side → the PC server keeps its own copy; Ensayar solo uses it through Normcore.
2. Approved take = es_preferida.
3. Data path → Quests ↔ Normcore ↔ server ↔ USB ↔ receiver ↔ BLE ↔ pods.
4. Calibration → director button + actor T-pose, offsets saved on the server.
5. Cues in notes → new `cues` column + automatic cues from the reference.
6. Body pose recorded → PoseTrack, on both DBs.
7. Live take → server window + timeline mode, operated by previz.
8. Shot sync via transform → SessionModel.
