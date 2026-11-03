# STATUS

Audit of the actual code against `Docs/plan/unity-cambios-existentes.md` and `Docs/plan/unity-priorities.md`.
Updated after every task (see CLAUDE.md). Last update: Oct 18, 2026 (colliders + fall guard).

**Legend:** ✅ Done · 🟡 Partial (what exists, and how) · ⬜ Not started

## Build and role setup

- **Unity version:** **6000.3.24f1 (Unity 6.3)**, per `ProjectSettings/ProjectVersion.txt`. `unity-priorities.md` still says "Unity 2022.3 LTS", and the request that created this file said 6.2. Both are outdated.
- **Roles:** compile-time defines. `DIRECTOR_BUILD` = director, none = actor, `SERVER_BUILD` = server (defined, but no code checks it yet).
- **Role switch:** set with **Tesis → Role → Director / Actor / Server** (`Assets/Editor/RoleSwitcher.cs`). It writes the define into every build profile's own Scripting Defines, never into Player Settings, and only applies while a build profile is active.
- **4 build profiles** (`Assets/Settings/Build Profiles/`): PC Actor, PC Director, Quest Actor, Quest Director. All four get the same role define, so "Actor" and "Director" in their names no longer mean anything. The Quest ones carry their own Player Settings (OpenXR, Android).
- **Quest-specific setup:**
  - SQLite has an Android arm64 `libsqlite3.so`.
  - The CSV is read with `UnityWebRequest` on Android.
  - The XR Device Simulator is `EditorOnly`.

## Networking today (Normcore)

- **Room:** the `Realtime` prefab in `Bootstrap.unity` joins room **`Test Room`** on start (its own "Join Room On Start" setting), on both roles. The `TesisEnsayo01` value in the scene is on a *disabled* `NormcoreDemoConnector` and isn't used. On the first `didConnectToRoom`, `Bootstrap` loads `MenuBackdrop` plus the role's home menu.
- **Disconnects:** Normcore's `AutoReconnect` is on the `Realtime` object (exponential backoff after a network error or the device sleeping). `Bootstrap` follows connect and disconnect for the whole session:
  - **Disconnect during Ensayo Remoto** → the user goes home with "Se perdió la conexión. Reconectando...". That means `Bootstrap.Mode == Remote` (from the Ensayo Remoto button through SceneSelect or the waiting screen and every take).
  - **Anywhere else** (menus, Inspección, Ensayar Solo) → nothing is interrupted, since those work offline.
  - **Reconnect** → only logged; no scene reload.
  - The director's home menu now shows notices too (`DirectorHomeMenuController.PendingNotice`).
  - An initial connection that never succeeds still leaves the app on the Bootstrap scene.
- **Take sync:** `sceneNumber` (property 5) + `shotNumber` (property 4) of `RehearsalSessionStateModel` (the transform hack is gone; the `RehearsalSession` object no longer has a `RealtimeTransform`, and its `RealtimeView` keeps component id 1 as deprecated).
  - The director writes both through `RehearsalSessionSync.SelectTake` → `RehearsalSessionState.SetSelection` (`RequestOwnership()`, then the scene, then the shot). Shot −1 = no shot; shot **0 = `FullSceneShot`, the whole scene** ("Escena completa"); scene −1 (or 0, in a room whose model predates the property) = unset.
  - The actor reacts to either change event, only in `Remote` mode, and reads the pair **one frame later**: sceneNumber can arrive after shotNumber in the same update, and going from one full scene to another changes only the scene.
  - Whatever the room already holds when joining doesn't fire the events, so a stale take never loads by itself. A late joiner reads both explicitly (see "Late join").
  - The actor takes the scene from the model; it only derives it from the shot in its own database when the model has no scene.
  - Locally the take is `Bootstrap.SelectTake(scene, shot)` → `CurrentSceneNumber`, `CurrentShotNumber`, `IsFullScene` and `StartShotNumber`: the shot whose `Shot{N}_Start` the take starts on, which for the whole scene is its lowest `shot_number` (1, 6, 11). `ShotStartPositioner`, `StartMarkAligner`, `StartMarkTape` and `FallGuard` use `StartShotNumber`. `[Escena]` logs.
- **Session state:** `RehearsalSessionStateModel` (`[RealtimeModel(createMetaModel: true)]`), with `sessionState`: 0 none, 1 active, 2 ended.
  - Written only by the director (`RequestOwnership()` and then set).
  - `BeginRemoteRehearsal` sets 1 and resets the take to scene −1 / shot −1. `EndRemoteRehearsal` sets 2 and is a no-op during Inspección.
- **Director leaves the rehearsal:** `sessionState = 2` → `RehearsalEnded` → the actor goes home with "El director terminó el ensayo".
- **Director disconnects:** Normcore clears the ownership the director held on the state model → `OwnerLeft` → the actor goes home with "Se perdió la conexión con el director". This only works once the director has written the state at least once.
- **Late join:** pressing Ensayo Remoto calls `JoinRemoteRehearsal()`, which loads the current take (a shot, or the whole scene with shot 0) only if the state is active **and** the model has an owner. A stale model left by a client that's gone is ignored.
- **Mode:** one local value, `Bootstrap.Mode` (`None` / `Remote` / `Solo` / `Inspect`), not synced. It replaces the old `IsInspecting` and `ActorJoinedRemoteRehearsal` flags; Ensayar Solo used to reuse the director's "inspecting" flag. It's set at every entry (home menu buttons, Favoritos → Ver) and reset to `None` at every exit (first connect, Menú Principal, the actor sent home, a disconnect during Remote). Pause → Selección de Escenas keeps the mode. Every director tool and every director-driven event checks for its own mode, so Solo shows no director tools and ignores the director entirely.
- **Solo session (actor):** `SoloSessionModel` (`[RealtimeModel(createMetaModel: true)]`, all properties reliable with change events) on its own `SoloSession` object (`RealtimeView` + `SoloSession` + `SoloRehearsalController`). Fields: `mode`, `sceneNumber`, `shotNumber`, `commandId`, `commandCounter`. The actor owns it only while in solo: when a take loads it writes mode=Solo + scene + shot (and writes again if the model arrives late or after a reconnect); on leaving solo it writes mode=None and clears ownership. Command channel with the same counter + baseline contract as the director's. It never touches the director's model. `[Solo]` logs. **Iniciar / Parar** on the right controller's B (A is the rig's stock Jump): each press sends the command; a world-space "Iniciando..." / "Detenido" fades out in ~1.5 s; opening the pause menu or leaving the take while running sends Parar automatically. Nothing is recorded on the Quest.
- **Actor head pose:** `ActorPositionSync` (actor build only). It's a `RealtimeTransform` that the actor requests ownership of every frame it doesn't hold it. The recorder reads it. **The director has no live visual of it.**
- **Who owns what:**
  - The director owns the shot transform and the state model.
  - The actor owns `ActorPositionSync`.
  - There's no presence model.
- **Command channel:** `commandId` + `commandCounter` on `RehearsalSessionStateModel`, written by the director (`RehearsalSessionSync.SendCommand` → `RehearsalSessionState.SendCommand`: id first, then counter + 1). Every other client fires `OnCommand` only when the counter goes above its baseline. The baseline is taken when receiving the model on joining and reset when the actor enters Ensayo Remoto, so a late joiner never replays an old command. The sender doesn't fire its own, and Ensayar Solo ignores commands (the baseline still moves up). Enum `SessionCommand`: Calibrate, Grabar, SiguienteToma, Accion, Corte, Guardar, Descartar, Aprobar. **No command does anything yet beyond logging** (`[SessionSync]`). Temporary debug key on the director: **C** = Calibrate.

## Haptics today (pods)

- **Transport:** 9 pods → receiver XIAO → `Bridge/bridge.py` → UDP 5005 → `PodReceiver` (`Assets/Scripts/Haptics/`); cues go back as `C,<id>,<cue>,<param>` on UDP 5006. The BLE layer is verified on hardware; the firmware may be changed under the rules in CLAUDE.md.
- **`PodReceiver` receive thread — fixed.** `Parse` runs on the UDP thread and used to call `Time.realtimeSinceStartup`, which is main-thread only: the first `P` line threw and the thread died, so orientation never reached Unity (a real bench run showed "0 de 0"). It now uses a `Stopwatch`, parses numbers with `InvariantCulture`, and catches anything a bad line throws so the thread keeps going. Raw quaternions are stored and converted on read, so changing `conversion` applies at once, even to a still pod. New `HasOrientation(id)`: false for a pod sending the all-zero "no IMU" quaternion.
- **Pods sending no IMU reading — firmware updated, needs flashing, cause unknown.** The bridge logs of 2026-11-02 (pods 1 and 2, ~6,900 samples) are all exactly `0,0,0,1`: the pod's starting value, sent until the BNO085 delivers a reading. So on both pods the IMU didn't start or never reported. `pod_v2.ino` now sends `0,0,0,0` while there's no reading, retries a failed IMU every 2 s, re-enables the report after 1 s of silence and restarts the IMU after 5 s, blinks slowly only while readings arrive, and logs each step on the pod's USB serial (pod-reference.md §8). Compiles (`arduino-cli`, `Seeeduino:nrf52:xiaonRF52840`); not flashed or tested on a pod. The receiver and bridge are unchanged.
- **`PodDiagnostics` scene** (`Assets/Scenes/PodDiagnostics.unity`, `PodDiagnostics.cs`): standalone, played on its own (no Bootstrap, XR rig or Normcore), desktop camera + IMGUI. One cube per pod with IMU readings, in a row by ID 1–9 (other IDs append to the right), each following `GetRotation(id)`, with red/green/blue sticks on its +X/+Y/+Z faces. The label "Pod N" always stays: white = readings, orange "sin IMU" = connected without a reading, grey = no data. On screen: connected/target (the receiver's `S` line), the IDs with data now, the ones without an IMU reading, and the current `conversion` — **C** (or a button) cycles it 0–3 at runtime. Buttons fire `CueCameraStart()` and `CueCut()` (checked on a UDP listener: `C,0,3,0` and `C,0,4,60`). Tested in Play mode with a fake bridge: an identity pod and a rotating one show cubes, an all-zero pod shows orange "sin IMU", a garbage line doesn't stop the thread, and a conversion change turns the cubes at once.
- **Still open:** why the bench pods had no IMU reading (the new firmware's LED/serial will say), which `ToUnity()` conversion is right, the pod-to-limb mapping, and the end-to-end cue latency.

## `unity-cambios-existentes.md`

| # | Item | Status |
|---|---|---|
| 1 | SessionModel replaces the shot-in-transform hack | 🟡 The scene and the shot are now properties of `RehearsalSessionStateModel` (with session state and commands); mode, actorJoined, recording and sessionId aren't synced yet |
| 1 | Session active/ended + actor joined inside one model | 🟡 `sessionState` is in `RehearsalSessionStateModel`; "joined" is a local flag |
| 1 | Ensayar Solo also joins a room | 🟡 Every client joins the room at startup; Solo just ignores all sync. No mode is sent |
| 1 | Persistent shot listener acts only when remote + joined | 🟡 Gated by the local joined flag, not a synced mode |
| 1 | ActorPositionSync kept | ✅ |
| 1 | AvatarModel / PodStatusModel / ReportModel / CueTrackModel | ⬜ |
| 1 | SERVER_BUILD + `Server` scene | 🟡 The define exists in the Role menu; no code uses it, and there's no `Server` scene |
| 2 | Shots: `cues` column | ⬜ Only `notes` (free text) |
| 2 | Sesiones: scene_number, tipo, session_id GUID | ✅ Added with the PRAGMA + ALTER rule. `tipo` is "toma" (a shot) or "escena" (the whole scene, `shot_number` 0). `session_id` is a GUID made at record start for every take (`RecordingSession.SessionGuid` in C#, not to be confused with `id`, the local key that `Metricas`/`CamaraTrack.sesion_id` use). Older rows were migrated on start: tipo "toma", scene_number from Shots, a fresh GUID each. A scene take is never saved as preferida, and the stop panel hides "Marcar como preferida" for it |
| 2 | es_preferida = approved reference, sends `aprobar` | 🟡 Preferida exists (exclusive per shot); nothing is sent |
| 2 | PoseTrack / ShotBoundaries / CueTrack tables | 🟡 **ShotBoundaries** exists (`session_id` = the Sesiones GUID, `shot_number`, `t_in`, `t_out` in the recording's own seconds, `source` "manual"/"auto", indexed by `session_id`). Full-scene takes keep them in memory and write them with Guardar, in the same transaction as the session; Descartar drops them (and deletes any rows for that GUID). Automatic suggestions: `RehearsalRecorder.SuggestShotBoundariesAuto`, an empty hook for now (would return `source = "auto"` rows). PoseTrack and CueTrack ⬜ |
| 2 | Metricas every 0.1 s on the director | ✅ `RehearsalRecorder`, `SampleInterval = 0.1` |
| 3 | SceneSelect "Escena completa" entry | ✅ Director only, Ensayo Remoto and Inspección: an "Escena completa — shots X a Y" button heads each scene group (own color). In Remote it syncs scene + shot 0 and the take starts on the first shot's start mark; in Inspección it opens the scene's full-scene recordings. Not in Solo |
| 3 | Approved-reference icon; Solo only allows those | 🟡 The session list has a star per session; no icon at shot level. In Solo, SceneSelect lists only shots for which `ApprovedReferences.HasApprovedReference(scene, shot)` is true, single shots only. The hook returns true for every shot until it can ask the server |
| 4 | ShotStartPositioner uses the approved reference start | ⬜ Only `Shot{N}_Start` |
| 5 | Grabar → Acción / Corte through the network | 🟡 The command channel exists, but B still only starts/stops a local recording and sends nothing |
| 5 | Siguiente toma / Calibrar buttons | 🟡 **Siguiente toma** on the director's left **Y** (Simulator T + N), only while recording a full scene: closes the current shot's boundary, opens the next shot's, sends `SessionCommand.SiguienteToma` (the actor logs it), "Siguiente toma: shot N". Optional: without it the take has one boundary (the first shot, whole take). Ignored on the last shot ("Última toma de la escena"). Calibrar ⬜ (only the temporary C key) |
| 5 | Full avatar of the actor | ⬜ No live visual (headset pose is synced but not drawn) |
| 5 | Pod status indicator | ⬜ |
| 5 | Stop popup (Ver replay / Guardar / Descartar / Preferida / Favorita) | ✅ Exists. Server keep/delete and "Editar cues" ⬜ |
| 5 | Report panel after Corte | ⬜ |
| 5 | Quick replay with avatar / cue editor | 🟡 Replay shows a capsule ghost plus the recorded camera |
| 6 | Takes per shot listed | ✅ Session picker, star/heart, Favoritos |
| 6 | Scene recordings with shot boundaries | ✅ "Escena completa" in Inspección lists the scene's `tipo = "escena"` recordings (`GetSceneSessions`), apart from single-shot takes: 0 or 1 loads directly, more opens a list (date, duration, shots marked). Replay is the same ghost + recorded camera; the slider shows a tick at each boundary's `t_in`, labeled "T6", "T7"… (also on "Ver replay" of an unsaved scene take, from memory). Heart works; the star is hidden for scene recordings (list, replay panel, stop panel). Favoritos opens a scene recording from its own `scene_number` + `shot_number` |
| 6 | Avatar replay (PoseTrack) | ⬜ Capsule ghost |
| 6 | Scrub slider as editor playhead | 🟡 Slider exists; there's no time event |
| 6 | Editor háptico | ⬜ |
| 6 | Favoritos unchanged | ✅ |
| 7 | Actor waiting screen: pod status, T-pose | ⬜ ("Esperando selección del director..." only) |
| 7 | Start mark on the floor | 🟡 `StartMarkTape` (on the Bootstrap object): in **every take, every mode, both roles** (Ensayo Remoto, Solo, Inspección) a yellow gaffer-tape "T" (60 × 40 cm, 6 cm tape) is drawn on the floor below `Shot{N}_Start` (the markers float 1.6 m up in every scene; the first collider below, up to 50 m, skipping the XR rig, then lifted 2.5 cm because the visible floor sits ~1.2 cm above the collider in scenes 2 and 3) (bar = toes, stem back between the feet; unlit UI images on a flat canvas, on the `UI` layer so virtual cameras don't film it). Since `StartMarkAligner` puts the actor's head over it on load, it starts right under the feet, out of view when looking ahead; the actor tutorial says "mira hacia abajo". Not tested on the Quest yet |
| 8 | Ensayar Solo with reference, pods, Iniciar/Parar | 🟡 Own mode (only the set, no director tools). SceneSelect filtered by the approved-reference hook (always true for now). Start mark. Iniciar/Parar on B through `SoloSessionModel`. Pods: empty stubs only (`SoloPods.GuideToMark` is called when a take loads, `OnPodStatus` isn't called yet). Nothing is recorded or cued yet (needs the server) |
| 9 | Pause menu: Calibrar, Estado de pods | ⬜ Menu has Menú Principal, (director) Selección de Escenas, Favoritos, and Tutorial |
| 10 | Server scene and modules | ⬜ |

## `unity-priorities.md`

| # | Item | Status |
|---|---|---|
| P0-1 | Controller ray UI on a real Quest, both builds | 🟡 Actor build tested on a Quest 3. Director only tested in the Editor |
| P0-2 | SessionModel | 🟡 One model now holds the shot, session state and commands (✅, commands log only). Missing: scene, mode, actorJoined, recording, sessionId |
| P0-3 | SERVER_BUILD .exe | 🟡 Define only |
| P0-4 | IPodSource + PodSimulator | ⬜ |
| P0-5 | SerialBridge | ⬜ |
| P1-6 | AvatarModel + avatar in director view | ⬜ |
| P1-7 | PoseSolver | ⬜ |
| P1-8 | Calibration flow | ⬜ |
| P1-9 | Server-side recording + server DB | ⬜ |
| P1-10 | Director DB PoseTrack + avatar replay | ⬜ |
| P1-11 | Scene rehearsal + shot slicing | ⬜ |
| P2-12 | Haptic vocabulary enum + command contract | ⬜ |
| P2-13 | CueGenerator | ⬜ |
| P2-14 | CueTrack sync | ⬜ |
| P2-15 | CueEngine | ⬜ |
| P2-16 | Haptic timeline editor | ⬜ |
| P2-17 | Structured cues per shot | ⬜ |
| P3-18 | Server window UI | ⬜ |
| P3-19 | Take report | ⬜ |
| P3-20 | Ensayar Solo complete | 🟡 See cambios #8: mode, synced session, Iniciar/Parar, start mark, hooks. Missing: server, real reference check, pods guiding, cues, recording |
| P3-21 | Pod status in both apps | ⬜ |
| P3-22 | CSV export for Python | ⬜ |
| P4-23 | Bone lengths from MediaPipe | ⬜ |
| P4-24 | Free-fly in Ensayo Remoto | ⬜ Free-fly exists only in Inspección |
| P4-25 | Notes, voice notes, trajectory verification | ⬜ |
| P4-26 | Scripted creature motion | ⬜ |
| P4-27 | Continuity warning / desktop director polish | ⬜ |

## Done but not in either plan

- **Colliders and fall guard.** Scenes 2 and 3 have a static MeshCollider on every mesh of the set: 122 for the spaceship, which used to have only a 10 × 19 m invisible floor box, and 36 more for the apartment. Scene 1's terrain already had them; its bushes are left without. `FallGuard` (both roles, every content scene): if the rig drops more than 3 m below the floor under the shot's start (off the set's edge or the mountain's cliff), it's put back on the start mark with "Te regresamos al inicio", re-aligned under the actor. The director's free-fly is ignored. `[FallGuard]` logs. `SceneFloor.TryFindFloorBelow` is the shared floor search (also used for the tape mark). `FloatingMessage` boxes are one line and grow to fit the text (min 620 wide): "Te regresamos al inicio" used to wrap and get cut to "Te regresamos al".
- **Real play area on the floor** (`PlayAreaOutline`, actor only, Remote + Solo): the Quest's boundary, read through OpenXR `XRInputSubsystem.TryGetBoundaryPoints` (the largest rectangle inside a Roomscale boundary), drawn as a light-blue line at the rig's feet. It moves with the rig (alignment, joystick), rebuilds on every take and on `boundaryChanged`. A Stationary boundary or the Editor reports none, so nothing is drawn. `[PlayArea]` logs the size. Not tested on a Quest yet (the drawing was checked in the Editor with a fake 4 × 5 m rectangle).
- **Start mark under the actor** (`StartMarkAligner`, actor only, Ensayo Remoto + Solo). `ShotStartPositioner` puts the XR Origin, the center of the real play area, on `Shot{N}_Start`. Right after that, the rig is turned about the actor's head and shifted so the head is over the mark, facing the mark's direction. This happens automatically when a take loads, and again with **left X** ("Fijar inicio aquí": walk to one end of the real room, face the open space, press X). Only the rig moves, never the head or camera. The director isn't affected. `[StartAlign]` logs.
- **Panel focus** (`PanelFocusManager`, on the Bootstrap object, both builds). Panels register as **modal** (pause menu, Favoritos, decision panel), **persistent** (replay panel, recorded-camera monitor, viewfinder) or **guide** (tutorial). Only one modal is shown: a newer one hides the older until it closes. While any modal is open, persistent panels are hidden through a CanvasGroup (no ray hits) and come back exactly as they were. The replay pauses and resumes only if it was playing. Left X, free-fly and the viewfinder's Grip and zoom are ignored. The world dims inside a 2 m dark box around the head. The tutorial is never hidden and docks beside what the manager reports; this replaced its own canvas scan. `[PanelFocus]` logs.
- Scene 2 (spaceship) lighting: the scene asked for skybox ambient, but its ambient was never generated (measured as zero), and the floor is fully metallic with no reflection probe, so shadows were pure black. It now has a cool trilight ambient (sky 0.45/0.52/0.58, equator 0.30/0.35/0.38, ground 0.16/0.16/0.17), the directional light goes from 0.7 to 0.9, and a realtime `ReflectionProbe_Interior` refreshes once on load over the walkable area, at intensity 2.5. Placeholder values, tuned by eye. Scenes 1 and 3 also have no generated ambient, but they're lit directly.
- Director viewfinder: virtual camera on the right hand, named lenses + fine zoom, REC badge, "scene only" feed. Its camera track is recorded (`CamaraTrack`) and replayed on a monitor.
- Replay panel: play/pause, loop, restart, scrub, preferida/favorita. Favoritos screen.
- Per-mode controls tutorial (`TutorialController`), replayable from the pause menu. In a full-scene take the director's Ensayo Remoto tutorial adds a Siguiente toma step (left Y; also done once recording stops, since it's optional) and its Guardar step mentions only favorita.
- Controls reference: `Docs/Controles.md`.
