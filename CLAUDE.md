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
- **Actor's view** and **director's view**: currently **both build for Meta Quest 3/3S only**. Apple Vision Pro / PolySpatial is paused for now — a possible future migration if there's time, but do not install or scaffold PolySpatial/Vision Pro support unless asked.
- **Network sync between the two**: **Normcore**.

Normcore is installed with a minimal sync demo scene. Meta XR/OpenXR packages are not yet in `Packages/manifest.json` — check before assuming either is present.

## Project state

This is a Unity **2022.3.58f1 LTS** project (see `ProjectSettings/ProjectVersion.txt`). It has moved past the empty scaffold stage: there's a scene-flow (Bootstrap → SceneSelect → content scene), a shot-list SQLite database driving the scene-select UI, a Normcore sync demo, and 3 content scenes with imported 3D assets. There is still no README, no CI workflow, and no build/test tooling configured — all iteration happens inside the Unity Editor.

The repo is pushed to `origin` at `https://github.com/HaiAvila/Tesis` (private).

### Scenes and flow

- **`Assets/Scenes/Bootstrap.unity`** — entry point. [`Bootstrap.cs`](Assets/Scripts/Bootstrap.cs) marks itself `DontDestroyOnLoad`, holds the `Realtime` (Normcore) connection, and on `didConnectToRoom` loads `NormcoreDemo` by scene name.
- **`Assets/Scenes/NormcoreDemo.unity`** — Normcore proof-of-concept: a `Realtime` object plus a `NetworkedCube` (`RealtimeView` + `RealtimeTransform`) that syncs across clients in the same room. Connector script: [`NormcoreDemoConnector.cs`](Assets/Scripts/NormcoreDemoConnector.cs) (connects to a hardcoded room name, logs connect/disconnect). Editor setup script: [`NormcoreDemoSetup.cs`](Assets/Editor/NormcoreDemoSetup.cs), menu item `Tools/Normcore/Create Demo Scene`, regenerates this scene + `Assets/Settings/NormcoreAppSettings.asset` from scratch.
- **`Assets/Scenes/SceneSelect.unity`** — reads the shot list from [`ShotListDatabase.cs`](Assets/Scripts/ShotListDatabase.cs) (a persisted object, expected already in the scene from Bootstrap), groups shots by scene number via [`SceneSelectController.cs`](Assets/Scripts/SceneSelectController.cs), and builds a scrollable uGUI list (all UI built at runtime in code, no prefab) of shot buttons. Clicking a shot loads the matching content scene (`Scene1_mountain` / `Scene2_spaceship` / `Scene3_apartment`) via `SceneManager.LoadSceneAsync`. [`OpenSceneSelectButton.cs`](Assets/Scripts/OpenSceneSelectButton.cs) is a small helper that spawns a button to jump into `SceneSelect` from elsewhere (test/dev convenience).
- **`Assets/Scenes/Scene1_mountain.unity`**, **`Scene2_spaceship.unity`**, **`Scene3_apartment.unity`** — the 3 content scenes, one per "shot" location. Each has matching imported `.glb` models under `Assets/Models/Scene1|2|3/` and a matching skybox material under `Assets/Skyboxes/` (`Skybox_Scene1/2/3.mat`, built from `.exr` HDRIs).

### Shot list database

[`ShotListDatabase.cs`](Assets/Scripts/ShotListDatabase.cs) is a `MonoBehaviour` that, on `Start()`, opens/creates a SQLite DB at `Application.persistentDataPath/shotlist.db` (via the `SQLite4Unity3d` plugin, see Packages below), creates a `Shots` table, and imports `Assets/StreamingAssets/ShotListDB.csv` into it (custom CSV parser that finds the header row by the `scene_number` marker, handles quoted fields). `GetShotsGroupedByScene()` queries the DB and returns shots grouped/sorted by scene and shot number for `SceneSelectController` to render. There's also `Assets/StreamingAssets/shotlist_test.csv` for test data. The `Shot` POCO and its SQLite column mapping live in the same file.

## Working with this project

**Split of responsibility**: Claude may add GameObjects, attach components, and wire serialized-field references directly (scene YAML edits or live Editor control) — this no longer requires the user to do it by hand. What stays the user's job is visual/spatial judgment: positioning objects, choosing materials or skyboxes, aesthetic decisions. If a task crosses from "wire this reference" to "decide how it should look or exactly where it should sit," say so instead of guessing.

**No live Editor control here.** The `unity-cli` skill's live-Editor-control path requires the project's `com.unity.pipeline` package, which itself requires **Unity 6.0+**. This project is on **2022.3.58f1** and doesn't have that package (`unity status` returns `STATUS_NO_INSTANCES`) — confirmed, not just a caveat. So scene/GameObject/component edits in this project mean **hand-editing `.unity`/`.prefab` YAML directly**, carefully: fileIDs and GUIDs are assigned by hand, a live Editor won't pick up the change until it regains focus/reimports, and a mistake can produce valid-looking YAML that silently doesn't do what was intended. When editing scene YAML by hand, do one scene first and let the user confirm it opens correctly in the Editor before repeating the change across other scenes.

There is no CLI build/test pipeline in the repo yet — all iteration happens inside the Unity Editor. Use the `unity-package-management` skill for adding/removing UPM packages headlessly, rather than editing `Packages/manifest.json` by hand.

## Packages

`Packages/manifest.json` includes, beyond Unity's default/standard packages (TextMeshPro, Timeline, UGUI, Visual Scripting, collab-proxy, and the built-in engine modules):
- **Normcore** (`com.normalvr.normcore`, 3.6.0, via the `Normal` scoped registry at `https://normcore-registry.normcore.io`) — network sync between actor and director.
- **`com.unity.cloud.gltfast`** (6.14.1) — runtime/import support for the `.glb` models used in the 3 content scenes.

Meta XR/OpenXR and PolySpatial packages are **not** installed yet — check `Packages/manifest.json` before assuming either is present. (PolySpatial is paused indefinitely for now; see "XR / networking architecture" above.)

Outside UPM, `Assets/Plugins/SQLite4Unity3d/` (`SQLite.cs`) plus the native `Assets/Plugins/x64/sqlite3.dll` provide the SQLite binding used by `ShotListDatabase.cs`.

A minimal Normcore integration exists under `Assets/Normal/`, `Assets/Settings/NormcoreAppSettings.asset`, `Assets/NormcoreProjectSettingsBuildProcessor/`, and the demo scene `Assets/Scenes/NormcoreDemo.unity`. `Assets/EasySyncBuildProcessor/` is also present (Normcore-related build processor).

## Git / gitignore

The `.gitignore` is the standard Unity template: `Library/`, `Temp/`, `Obj/`, `Build*/`, `Logs/`, `UserSettings/` are all ignored, so only source-controlled project files (Assets, Packages, ProjectSettings, .vscode/.claude config) are tracked. Never commit the `Library/` folder — it's large, regeneratable, and already excluded.
