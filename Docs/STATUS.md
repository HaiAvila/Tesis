# STATUS

Audit of the actual code against `Docs/plan/unity-cambios-existentes.md` and `Docs/plan/unity-priorities.md`.
Updated after every task (see CLAUDE.md). Last update: Oct 18, 2026 (disconnect handling).

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
  - **Disconnect during Ensayo Remoto** → the user goes home with "Se perdió la conexión. Reconectando...". For the director, Ensayo Remoto means SceneSelect or a take with `IsInspecting` false. For the actor, it's the joined flag (waiting or in a take).
  - **Anywhere else** (menus, Inspección, Ensayar Solo) → nothing is interrupted, since those work offline.
  - **Reconnect** → only logged; no scene reload.
  - The director's home menu now shows notices too (`DirectorHomeMenuController.PendingNotice`).
  - An initial connection that never succeeds still leaves the app on the Bootstrap scene.
- **Shot sync:** `RehearsalSessionSync` encodes the shot number in a `RealtimeTransform`'s `position.x`.
  - The director calls `RequestOwnership()` and then sets it.
  - The actor reacts to changes in `Update()`, but only while `Bootstrap.ActorJoinedRemoteRehearsal` is set.
  - A baseline taken on connect ignores a stale value left in the room.
  - The scene isn't synced: the actor looks it up from the shot in its own database.
- **Session state:** `RehearsalSessionStateModel` (`[RealtimeModel(createMetaModel: true)]`), with `sessionState`: 0 none, 1 active, 2 ended.
  - Written only by the director (`RequestOwnership()` and then set).
  - `BeginRemoteRehearsal` sets 1 and resets the shot to −1. `EndRemoteRehearsal` sets 2 and is a no-op during Inspección.
- **Director leaves the rehearsal:** `sessionState = 2` → `RehearsalEnded` → the actor goes home with "El director terminó el ensayo".
- **Director disconnects:** Normcore clears the ownership the director held on the state model → `OwnerLeft` → the actor goes home with "Se perdió la conexión con el director". This only works once the director has written the state at least once.
- **Late join:** pressing Ensayo Remoto calls `JoinRemoteRehearsal()`, which loads the current shot only if the state is active **and** the model has an owner. A stale model left by a client that's gone is ignored.
- **"Joined" and Ensayar Solo protection:** both are local flags (`Bootstrap.ActorJoinedRemoteRehearsal`, `Bootstrap.IsInspecting`), not synced. Ensayar Solo sets joined = false, so every shot or session event is ignored.
- **Actor head pose:** `ActorPositionSync` (actor build only). It's a `RealtimeTransform` that the actor requests ownership of every frame it doesn't hold it. The recorder reads it. **The director has no live visual of it.**
- **Who owns what:**
  - The director owns the shot transform and the state model.
  - The actor owns `ActorPositionSync`.
  - There's no presence model.
- **Command channel:** `commandId` + `commandCounter` on `RehearsalSessionStateModel`, written by the director (`RehearsalSessionSync.SendCommand` → `RehearsalSessionState.SendCommand`: id first, then counter + 1). Every other client fires `OnCommand` only when the counter goes above its baseline. The baseline is taken when receiving the model on joining and reset when the actor enters Ensayo Remoto, so a late joiner never replays an old command. The sender doesn't fire its own, and Ensayar Solo ignores commands (the baseline still moves up). Enum `SessionCommand`: Calibrate, Grabar, SiguienteToma, Accion, Corte, Guardar, Descartar, Aprobar. **No command does anything yet beyond logging** (`[SessionSync]`). Temporary debug key on the director: **C** = Calibrate.

## `unity-cambios-existentes.md`

| # | Item | Status |
|---|---|---|
| 1 | SessionModel replaces the shot-in-transform hack | ⬜ Still `RehearsalSessionSync` (position.x) |
| 1 | Session active/ended + actor joined inside one model | 🟡 `sessionState` is in `RehearsalSessionStateModel`; "joined" is a local flag |
| 1 | Ensayar Solo also joins a room | 🟡 Every client joins the room at startup; Solo just ignores all sync. No mode is sent |
| 1 | Persistent shot listener acts only when remote + joined | 🟡 Gated by the local joined flag, not a synced mode |
| 1 | ActorPositionSync kept | ✅ |
| 1 | AvatarModel / PodStatusModel / ReportModel / CueTrackModel | ⬜ |
| 1 | SERVER_BUILD + `Server` scene | 🟡 The define exists in the Role menu; no code uses it, and there's no `Server` scene |
| 2 | Shots: `cues` column | ⬜ Only `notes` (free text) |
| 2 | Sesiones: scene_number, tipo, session_id GUID | ⬜ Local autoincrement id |
| 2 | es_preferida = approved reference, sends `aprobar` | 🟡 Preferida exists (exclusive per shot); nothing is sent |
| 2 | PoseTrack / ShotBoundaries / CueTrack tables | ⬜ Tables are Shots, Sesiones, Metricas, CamaraTrack (+ unused CameraFramings) |
| 2 | Metricas every 0.1 s on the director | ✅ `RehearsalRecorder`, `SampleInterval = 0.1` |
| 3 | SceneSelect "Escena completa" entry | ⬜ |
| 3 | Approved-reference icon; Solo only allows those | 🟡 The session list has a star per session; no icon at shot level; Solo isn't restricted |
| 4 | ShotStartPositioner uses the approved reference start | ⬜ Only `Shot{N}_Start` |
| 5 | Grabar → Acción / Corte through the network | 🟡 The command channel exists, but B still only starts/stops a local recording and sends nothing |
| 5 | Siguiente toma / Calibrar buttons | ⬜ |
| 5 | Full avatar of the actor | ⬜ No live visual (headset pose is synced but not drawn) |
| 5 | Pod status indicator | ⬜ |
| 5 | Stop popup (Ver replay / Guardar / Descartar / Preferida / Favorita) | ✅ Exists. Server keep/delete and "Editar cues" ⬜ |
| 5 | Report panel after Corte | ⬜ |
| 5 | Quick replay with avatar / cue editor | 🟡 Replay shows a capsule ghost plus the recorded camera |
| 6 | Takes per shot listed | ✅ Session picker, star/heart, Favoritos |
| 6 | Scene recordings with shot boundaries | ⬜ |
| 6 | Avatar replay (PoseTrack) | ⬜ Capsule ghost |
| 6 | Scrub slider as editor playhead | 🟡 Slider exists; there's no time event |
| 6 | Editor háptico | ⬜ |
| 6 | Favoritos unchanged | ✅ |
| 7 | Actor waiting screen: pod status, T-pose | ⬜ ("Esperando selección del director..." only) |
| 7 | Start mark on the floor | ⬜ The rig is placed at `Shot{N}_Start`; nothing is drawn |
| 8 | Ensayar Solo with reference, pods, Iniciar/Parar | 🟡 Navigation only: SceneSelect → scene |
| 9 | Pause menu: Calibrar, Estado de pods | ⬜ Menu has Menú Principal, (director) Selección de Escenas, Favoritos, and Tutorial |
| 10 | Server scene and modules | ⬜ |

## `unity-priorities.md`

| # | Item | Status |
|---|---|---|
| P0-1 | Controller ray UI on a real Quest, both builds | 🟡 Actor build tested on a Quest 3. Director only tested in the Editor |
| P0-2 | SessionModel | 🟡 See networking above. Command channel ✅ (logging only, no sender besides the debug key) |
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
| P3-20 | Ensayar Solo complete | 🟡 Navigation only |
| P3-21 | Pod status in both apps | ⬜ |
| P3-22 | CSV export for Python | ⬜ |
| P4-23 | Bone lengths from MediaPipe | ⬜ |
| P4-24 | Free-fly in Ensayo Remoto | ⬜ Free-fly exists only in Inspección |
| P4-25 | Notes, voice notes, trajectory verification | ⬜ |
| P4-26 | Scripted creature motion | ⬜ |
| P4-27 | Continuity warning / desktop director polish | ⬜ |

## Done but not in either plan

- Director viewfinder: virtual camera on the right hand, named lenses + fine zoom, REC badge, "scene only" feed. Its camera track is recorded (`CamaraTrack`) and replayed on a monitor.
- Replay panel: play/pause, loop, restart, scrub, preferida/favorita. Favoritos screen.
- Per-mode controls tutorial (`TutorialController`), replayable from the pause menu.
- Controls reference: `Docs/Controles.md`.
