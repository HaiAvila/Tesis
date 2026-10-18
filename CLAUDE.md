# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

This is a thesis project (Tesis) building a **haptic feedback system for actors in virtual production**: wearable bands with IMUs and LRA motors that cue/guide an actor during rehearsal and live takes. The Unity project is the software side of this hardware+software system — it hosts the XR views, the networked sync between actor and director, and the logic that will drive haptic cues from scene/timing events.

### Hardware decisions (already fixed — don't second-guess or suggest alternatives)

- **LRA haptic motor**: Vybronics VG0832013D, 8mm, 235Hz resonance, 1.8VAC rated, 1G (weak output) — one per band, across **9 pods**: waist, biceps x2, wrists x2, quads x2, calves x2.
- **IMU**: Adafruit BNO085 primary (9DOF, SH-2 sensor fusion), ICM-20948 as fallback/backup.
- **Microcontroller**: Seeed XIAO nRF52840 (Sense variant, mbed-enabled core), per pod, presumably BLE-connected to the Unity host — confirm before assuming a specific transport.
- **Haptic driver**: DRV2605L (SparkFun/DevLab Qwiic module), drives the LRA from the microcontroller.
- **Battery**: LiPo 3.7V 150mAh per band.
- **Pod layout**: LRA and BNO085 are placed at opposite ends of the pod (magnetic separation, to avoid the IMU picking up motor vibration/interference).

### Haptic cue vocabulary

18 cues across 3 tiers. Bands marked "pendiente" have not been assigned to a specific pod yet.

**MVP (5) — actively implemented**
1. Proximity — pulse frequency ∝ 1/distance (PS5-style) — Band: waist
2. Orientation — directional pulse, indicates which side to turn toward — Band: 1 wrist (L or R)
3. Timing — silent metronome, alternating — Band: calves, alternating L-R
4. Camera start — 3 short spaced pulses — Band: all 9
5. Cut — 1 long, low-intensity pulse — Band: all 9

**Backup (8) — implemented, not demoed at defense**
6. Initial mark confirmation — single "ready" pulse — Band: pending
7. Checkpoint confirmation — "coin collected"-style, resets accumulated error — Band: pending
8. Contact with virtual object — sustained pulse, distinct from proximity — Band: pending
9. Anticipatory alert — ascending/stepped pulse — Band: waist only
10. Segment speed deviation — too fast/slow vs. expected pace for the segment — Band: pending
11. Hold position — sustain posture for the duration of an event — Band: pending
12. Exclusion zone / physical hazard — warns of the set's physical boundary — Band: pending
13. Proximity to another actor — excluded from the demo (needs a 2nd actor with pods + a 2nd Vision Pro) — Band: pending

Cues 6-13 are meant to be used across the 3 demo scenes (Scene1_mountain/giant, Scene2_spaceship/spore, Scene3_apartment/robot); band assignment is pending, per how each cue fits its scene.

**Experimental (5) — documented as future work only, NOT in the app menu**
14. Laban effort/jerk — vibration modulated by movement quality (Laban Movement Analysis) — pending: which Laban parameters are perceptible as vibration is unvalidated
15. Breathing rhythm / emotional pacing — pulse suggests a breathing rhythm to modulate emotional intensity — crosses into acting/interpretive territory, hard to validate objectively
16. Multi-actor synchrony — coordinates simultaneous group movement across several actors — needs a shared network time architecture that doesn't exist yet
17. Real-time director's note — director marks a performance note → abstract haptic pattern — jump from objective to subjective feedback, hard to defend to a jury
18. Virtual surface texture — pattern suggests what the actor is "touching" (rough/liquid/hot) — would need its own pattern-to-perception correspondence study

### XR / networking architecture

Dual-view VR setup, synchronized over the network:
- **Actor's view** and **director's view**: currently **both build for Meta Quest 3/3S only**, through OpenXR + XR Interaction Toolkit (the stock XRI Starter Assets rig, `XR Origin (XR Rig)`). Apple Vision Pro / PolySpatial is paused for now — a possible future migration if there's time, but do not install or scaffold PolySpatial/Vision Pro support unless asked.
- **Network sync between the two**: **Normcore**. The shot the director picks is synced as a `RealtimeTransform` position (`RehearsalSessionSync`); the rehearsal's active/ended state is a `[RealtimeModel(createMetaModel: true)]` (`RehearsalSessionStateModel` / `RehearsalSessionState`) — the meta model is required for ownership, which the "director disconnected" detection relies on. The actor's head pose is synced by `ActorPositionSync`.
- Day-to-day testing so far: actor as a build on a Quest 3, director in the Editor's Play mode with the XR Device Simulator.

### Roles and build profiles

One codebase, role chosen at compile time: `DIRECTOR_BUILD` = director, no define = actor, `SERVER_BUILD` = future PC server (not checked by any code yet, so it behaves like actor). The role is set with **Tesis → Role → Director / Actor / Server** ([`RoleSwitcher.cs`](Assets/Editor/RoleSwitcher.cs)), which writes it into every build profile's own Scripting Defines — never into Player Settings. There are 4 build profiles under `Assets/Settings/Build Profiles/` (PC/Quest × Actor/Director; the Quest ones carry their own Player Settings with the Quest-specific values). The role only applies while a build profile is active. Don't put role defines back into Player Settings, and don't use internal Unity APIs to read/write a profile's Player Settings — `PlayerSettings.GetScriptingDefineSymbols_Internal` crashed the Editor.

## Project state

This is a Unity **6000.3.24f1** project (see `ProjectSettings/ProjectVersion.txt`). There is still no README, no CI workflow, and no build/test tooling configured — all iteration happens inside the Unity Editor. Controls (every button, per role and context) are documented in Spanish in [`Docs/Controles.md`](Docs/Controles.md) — keep it updated when controls change.

The repo is pushed to `origin` at `https://github.com/HaiAvila/Tesis` (private).

### Scenes and flow

- **`Bootstrap.unity`** — entry point. [`Bootstrap.cs`](Assets/Scripts/Bootstrap.cs) is `DontDestroyOnLoad`, holds the `Realtime` connection and the persistent controllers (database, recorder, playback, viewfinder, session sync…), and on `didConnectToRoom` loads `MenuBackdrop` plus `Director_HomeMenu` or `Actor_HomeMenu` additively, depending on the role.
- **`MenuBackdrop.unity`** — the XR rig and backdrop that the menu scenes (home menus, `SceneSelect`) load on top of. `SceneSelect` has no rig of its own, so it's always loaded via `Bootstrap.LoadSceneSelect()`.
- **Director home**: "Ensayo Remoto" (live rehearsal with the actor) and "Inspección de Escenas" (review recorded sessions). **Actor home**: "Ensayo Remoto" (wait for the director's pick, then follow into the take) and "Ensayar Solo".
- **`SceneSelect.unity`** — shot list from the database, grouped by scene ([`SceneSelectController.cs`](Assets/Scripts/SceneSelectController.cs)); World Space, placed in front of the head.
- **`Scene1_mountain` / `Scene2_spaceship` / `Scene3_apartment`** — the 3 content scenes, each with its `.glb` models under `Assets/Models/Scene1|2|3/` and a skybox under `Assets/Skyboxes/`. Each contains an **XR Device Simulator** tagged `EditorOnly` so it's stripped from builds — left in a build, it replaces the real headset with a still, simulated one (scene locked to the head, view at floor level).
- **`NormcoreDemo.unity`** — the original Normcore proof-of-concept, not part of the app flow.

All UI is World Space and built at runtime in code (no prefabs). Panels get controller-ray support through [`XRUISetup.cs`](Assets/Scripts/XRUISetup.cs) (`XRUIInputModule` + `TrackedDeviceGraphicRaycaster`). Panels that can be on screen while a virtual camera renders go on the `UI` layer, which those cameras exclude. Project layers: `DirectorViewfinderPanel` (8), `ControllerRayVisual` (9), `PlaybackMonitor` (10). Icons that the legacy font renders badly (star, heart, play/pause) are generated sprites in [`UIIcons.cs`](Assets/Scripts/UIIcons.cs).

A per-mode controls tutorial ([`TutorialController.cs`](Assets/Scripts/TutorialController.cs)) is offered the first time each mode's content scene opens (remembered in `PlayerPrefs`) and can be replayed from the pause menu. When a control changes, update its tutorial step too.

All on-screen text and `Docs/Controles.md` are in **Mexican Spanish** (tú: "presiona", "puedes", "¿Quieres…?") — never Argentine voseo ("apretá", "podés", "¿Querés…?") or Argentine vocabulary ("tildar").

### Shot list database

[`ShotListDatabase.cs`](Assets/Scripts/ShotListDatabase.cs) opens/creates SQLite at `Application.persistentDataPath/shotlist.db` and imports `Assets/StreamingAssets/ShotListDB.csv` into the `Shots` table on start (on Android the CSV is read with `UnityWebRequest`, since StreamingAssets is inside the APK). Recorded rehearsals go to `Sesiones` (with `es_preferida` / `es_favorita`), the actor's samples to `Metricas`, the director's viewfinder camera to `CamaraTrack`. `CameraFramings` still exists in the schema but nothing reads or writes it anymore. New columns are added with a `PRAGMA table_info` check + `ALTER TABLE`, so older databases keep working.

## Working with this project

**Split of responsibility**: Claude may add GameObjects, attach components, and wire serialized-field references directly — this doesn't require the user to do it by hand. What stays the user's job is visual/spatial judgment: positioning objects, choosing materials or skyboxes, aesthetic decisions. If a task crosses from "wire this reference" to "decide how it should look or exactly where it should sit," say so instead of guessing.

**Live Editor control works** through the `unity` CLI (`unity status`, `unity command eval_file --file …`, `recompile` / `recompile_status`, `menu --path …`, `editor_play` / `editor_stop`, `open_scene`, `save_scene`…). Prefer it over hand-editing scene YAML. Before opening or saving scenes, check which scenes are open and whether they have unsaved changes. To compile-check both roles, use the Tesis → Role menu and switch back to the user's role afterwards.

Use the `unity-package-management` skill for adding/removing UPM packages, rather than editing `Packages/manifest.json` by hand.

## Packages

`Packages/manifest.json` includes, beyond Unity's default/standard packages:
- **Normcore** (`com.normalvr.normcore`, 3.6.1, via the `Normal` scoped registry at `https://normcore-registry.normcore.io`).
- **XR**: `com.unity.xr.openxr` (1.18.0), `com.unity.xr.interaction.toolkit` (3.6.1, with its Starter Assets and XR Device Simulator samples under `Assets/Samples/`), `com.unity.xr.management` (4.7.0).
- **`com.unity.cloud.gltfast`** (6.14.1) — the `.glb` models in the content scenes.

PolySpatial is **not** installed (paused indefinitely).

Outside UPM, `Assets/Plugins/SQLite4Unity3d/` (`SQLite.cs`) is the SQLite binding, with two native libraries: `Assets/Plugins/x64/sqlite3.dll` (Editor/PC) and `Assets/Plugins/Android/libs/arm64-v8a/libsqlite3.so` (Quest; compiled from the official SQLite amalgamation with Unity's bundled Android NDK).

Normcore-related assets: `Assets/Normal/`, `Assets/Settings/NormcoreAppSettings.asset`, `Assets/NormcoreProjectSettingsBuildProcessor/`, `Assets/EasySyncBuildProcessor/`.

## Git / gitignore

The `.gitignore` is the standard Unity template: `Library/`, `Temp/`, `Obj/`, `Build*/`, `Logs/`, `UserSettings/` are all ignored, so only source-controlled project files (Assets, Packages, ProjectSettings, .vscode/.claude config) are tracked. Never commit the `Library/` folder — it's large, regeneratable, and already excluded.
