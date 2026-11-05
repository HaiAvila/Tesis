# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

This is a thesis project (Tesis) building a **haptic feedback system for actors in virtual production**: wearable bands with IMUs and LRA motors that cue/guide an actor during rehearsal and live takes. The Unity project is the software side of this hardware+software system — it hosts the XR views, the networked sync between actor and director, and the logic that will drive haptic cues from scene/timing events.

The wider point of the system is **remote rehearsal** — actor and director can be in different places, so a set doesn't have to be rented to block a scene.

### Pipeline (full version: [`Docs/pipeline.md`](Docs/pipeline.md) — keep both in sync)

- **Previz team** makes every build and is the only one who opens Unity. **Actor**: Quest 3 + 9 pods + a PC with the receiver running the server `.exe` (`SERVER_BUILD`) — no configuration. **Director**: Quest 3 (optionally a desktop build for editing the cue timeline), possibly somewhere else.
- **The server runs where the pods are**: pods → receiver → bridge → pose solver (calibration + body measurements) → AvatarModel over Normcore. It also takes the headset pose (avatar root), runs Calibrar, records takes with pose data, generates and plays cues, makes the take report. **The director's app never talks to the pods**: its commands go through Normcore to the server.
- **Setups**: remote rehearsal · Ensayar solo (no director) · Inspeccionar escenas (director only, no pods) · shoot day (pods only, no Quest; the previz PC runs the server) · **testing now**: the Editor on the user's PC plays the server role with the pods on USB.
- **Whichever machine has the pods sends the avatar** — not tied to a build role.
- Body measurements are entered by hand for now; an iPhone scan (MediaPipe) is future work. A DigitalOcean API + database is planned, **not now**.

### Hardware decisions (already fixed — don't second-guess or suggest alternatives)

- **LRA haptic motor**: Vybronics VG0832013D, 8mm, 235Hz resonance, 1.8VAC rated, 1G (weak output) — one per band, across **9 pods**: sternum (pod 9, replaces the waist), biceps x2, wrists x2, quads x2, shins x2. **No hip pod**: the hips follow pod 9's yaw plus the headset's position. Which pod ID sits where, its humanoid bone and which way its USB end points: [`Docs/pod-map.md`](Docs/pod-map.md).
- **IMU**: BNO085, **UNIT Electronics module** (not the Adafruit breakout — the I²C address is **0x4B**, not 0x4A). 9DOF, SH-2 sensor fusion. ICM-20948 as fallback/backup.
- **Microcontroller**: Seeed XIAO nRF52840 (**non-Sense**), per pod, BLE. **Pods and receiver use different Arduino cores, on purpose:**
  - **Pods: the mbed-enabled core** ("Seeed nRF52 mbed-enabled Boards" → "Seeed XIAO nRF52840 Sense"), with **ArduinoBLE** as a peripheral (firmware v3, 2026-11-02; v4, 2026-11-04, adds the battery). The classic core's `Wire` doesn't work with the BNO085 — a plain IMU sketch with no BLE and no DRV prints nothing on it — which is why every pod sample used to be `0,0,0,1`.
  - **Receiver: the classic "Seeed nRF52 Boards" core** (Adafruit's Bluefruit stack), because ArduinoBLE holds only one central connection and can't take 9 pods.
  - The two stacks interoperate. Don't move either one to the other core.
- **Haptic driver**: DRV2605L, **UNIT Electronics DevLab module (UE0065)**, drives the LRA from the microcontroller.
- **Battery**: LiPo 3.7V **140 mAh** per band. Charged at the XIAO's 50 mA default (P0.13 never touched; 100 mA deliberately not used). Battery-reading pins verified against Seeed's schematic: see `Docs/pod-reference.md` §7 before touching P0.13, P0.14, P0.17 or P0.31.
- **Pod layout**: LRA and BNO085 at opposite ends of the pod (magnetic separation). Bench-tested: no measurable interference from the LRA at pod distance.
- **PCB**: custom 2-layer flex, 40 × 26.5 mm, JLCPCB. Pods are assembled, tested and mounted in TPU housings; attached to skin with Walker Tape, no straps.

Full hardware detail — wiring, assembly, RF measurements, power, gotchas — is in [`Docs/pod-reference.md`](Docs/pod-reference.md).

### Pod transport and protocol

**The BLE layer is verified on real devices:** nine pods have held simultaneous BLE connections for six minutes with zero drops, and the round trip from Unity to a vibration on skin is proven. **Claude may change the firmware, the bridge and `PodReceiver` when something needs fixing** (the user's call, 2026-11-02), as long as:
- all 9 pods still connect and send their data at the same time — never trade that away;
- the serial/UDP line formats and the cue IDs only change together across pod, receiver, `bridge.py` and `PodReceiver`, and this file and [`Docs/pod-reference.md`](Docs/pod-reference.md) are updated with them;
- every firmware change compiles before it's committed (see "Compiling the firmware" below), and the user is told it needs flashing — Claude can't flash or test pods. Each pod is flashed separately with its own `POD_ID`, which is tedious, so batch firmware changes.

```
9 pods ──BLE──> receiver XIAO ──USB serial──> bridge.py ──UDP──> Unity ──Normcore──> 2 Quests
```

- **Receiver**: a 10th XIAO on USB acting as BLE central for all 9 pods. Lives on a USB extension cable near the rehearsal area, at head height.
- **`Bridge/bridge.py`**: forwards serial lines to Unity verbatim and relays cue commands back. Deliberately dumb — no translation. Needs `pip install pyserial`. Logs raw samples to CSV.
- **[`Assets/Scripts/Haptics/PodReceiver.cs`](Assets/Scripts/Haptics/PodReceiver.cs)**: the Unity boundary. Read it before touching anything haptics-related. Exposes `GetRotation(podId)`, `IsAlive(podId)`, `HasOrientation(podId)`, `ActivePods()`, `TryGetRawRotation(podId, out raw)` (the BNO085's i, j, k, real before conversion), `ToUnityRotation(raw)`, `GetPacketCount(podId)`, `TryGetHealth(podId, out health)` (battery mV and estimated %, charging, RSSI), `SendCue(...)`, and helpers `CueProximity` / `CueOrientation` / `CueStep` / `CueCameraStart` / `CueCut`. Lines are parsed on a background thread: **nothing in `Parse` may touch the Unity API** (`Time.realtimeSinceStartup` there killed the thread on the first sample — it uses a `Stopwatch`), and numbers are parsed with `InvariantCulture`.

Orientation arrives at roughly **11 Hz per pod** (nine connections sharing one radio). That is intentional and sufficient — IMU rate is not the latency path. What has to be fast is a cue command going *out* to a pod.

**Receiver → PC (serial, 115200):**
```
P,<id>,<qi>,<qj>,<qk>,<qr>        one per sample
S,<connected>,<target>,<id>,...   once a second
R,connected,<id>,<cue|nocue>,<n>
R,lost,<id>,<reason>
H,<id>,<battery_mV>,<charging>,<rssi>   every 2 s per connected pod
```

`H`: `battery_mV` 0 = the pod sent none (firmware older than v4, or no reading yet); `charging` 1 / 0, −1 = unknown (older than v4); `rssi` in dBm as the receiver sees that pod, 0 = not available. Pod v4's notify is 19 bytes: the four floats, VBAT in mV (uint16 LE), flags (bit0 charging, bit1 reading valid); the receiver still accepts v3's 16.

**Pods → Unity (UDP 5005):** the serial lines above, forwarded unchanged. **`P,<id>,0,0,0,0` (all zeros) = the pod is connected but has no IMU reading** — the IMU didn't start or went quiet; `IsAlive` is true, `HasOrientation` false. The quaternion is the BNO085's **game rotation vector** (no magnetometer), so heading is relative to where the pod faced at power-up and drifts slowly.

**Unity → pods (UDP 5006):** `C,<id>,<cue>,<param>` — **id 0 broadcasts to every pod**.

| Cue | Name | Param | Used for |
|----|--------|---------|----------------------------------|
| 0 | STOP | — | ends a PULSE |
| 1 | CLICK | — | orientation, footstep metronome |
| 2 | PULSE | ms ÷ 10 | proximity (lower = faster) |
| 3 | TRIPLE | — | camera start |
| 4 | LONG | ms ÷ 10 | cut (0 = 500 ms) |
| 5 | DOUBLE | — | confirmations |
| 6 | DONE | — | a pod calibrated |
| 7 | FAIL | — | a pod failed to calibrate |

Patterns run **on the pod**, not on the server: one BLE write plays a whole pattern, so timing survives radio contention and costs one packet instead of three. The calibration countdown is not firmware: `PodAvatar` is the clock and sends CLICK at 3, 2, 1 s before each capture, DOUBLE when a pose is captured, then DONE or FAIL per pod.

### Haptic cue vocabulary

18 cues across 3 tiers. Bands marked "pendiente" have not been assigned to a specific pod yet.

**MVP (5) — actively implemented**
1. Proximity — pulse frequency ∝ 1/distance (PS5-style) — Band: sternum (pod 9; was waist)
2. Orientation — directional pulse, indicates which side to turn toward — Band: 1 wrist (L or R)
3. Timing — silent metronome, alternating — Band: shins (pods 7 and 8), alternating L-R
4. Camera start — 3 short spaced pulses — Band: all 9
5. Cut — 1 long, low-intensity pulse — Band: all 9

**Backup (8) — implemented, not demoed at defense**
6. Initial mark confirmation — single "ready" pulse — Band: pending
7. Checkpoint confirmation — "coin collected"-style, resets accumulated error — Band: pending
8. Contact with virtual object — sustained pulse, distinct from proximity — Band: pending
9. Anticipatory alert — ascending/stepped pulse — Band: sternum only (pod 9; was waist)
10. Segment speed deviation — too fast/slow vs. expected pace for the segment — Band: pending
11. Hold position — sustain posture for the duration of an event — Band: pending
12. Exclusion zone / physical hazard — warns of the set's physical boundary — Band: pending
13. Proximity to another actor — excluded from the demo (needs a 2nd actor with pods + a 2nd headset) — Band: pending

Cues 6-13 are meant to be used across the 3 demo scenes (Scene1_mountain/giant, Scene2_spaceship/spore, Scene3_apartment/robot); band assignment is pending, per how each cue fits its scene.

**Experimental (5) — documented as future work only, NOT in the app menu**
14. Laban effort/jerk — vibration modulated by movement quality (Laban Movement Analysis) — pending: which Laban parameters are perceptible as vibration is unvalidated
15. Breathing rhythm / emotional pacing — pulse suggests a breathing rhythm to modulate emotional intensity — crosses into acting/interpretive territory, hard to validate objectively
16. Multi-actor synchrony — coordinates simultaneous group movement across several actors — needs a shared network time architecture that doesn't exist yet
17. Real-time director's note — director marks a performance note → abstract haptic pattern — jump from objective to subjective feedback, hard to defend to a jury
18. Virtual surface texture — pattern suggests what the actor is "touching" (rough/liquid/hot) — would need its own pattern-to-perception correspondence study

**Note on sustained cues (8, 11, 12):** DRV effect 118 is named "long buzz for programmatic stopping" but **self-terminates after about 10 s** — verified on hardware. Anything that must vibrate continuously has to re-trigger, or use the DRV's real-time playback (RTP) mode. Don't build a sustained cue assuming 118 holds.

### XR / networking architecture

Dual-view VR setup, synchronized over the network:
- **Actor's view** and **director's view**: currently **both build for Meta Quest 3/3S only**, through OpenXR + XR Interaction Toolkit (the stock XRI Starter Assets rig, `XR Origin (XR Rig)`). Apple Vision Pro / PolySpatial is paused for now — a possible future migration if there's time, but do not install or scaffold PolySpatial/Vision Pro support unless asked.
- **Network sync between the two**: **Normcore**. The selected take (scene + shot, where shot 0 is the whole scene), the rehearsal's active/ended state and the director → everyone command channel all live in one `[RealtimeModel(createMetaModel: true)]` (`RehearsalSessionStateModel` / `RehearsalSessionState`, used through `RehearsalSessionSync`) — the meta model is required for ownership, which the "director disconnected" detection relies on. The actor's head pose is synced by `ActorPositionSync`, and how their rig is turned by `ActorRigSync` (X, a new take and the joystick turn the rig but not the actor, so the avatar's facing needs it). The actor's body is synced by `AvatarPoseModel` / [`AvatarPoseSync`](Assets/Scripts/Haptics/AvatarPoseSync.cs) on Bootstrap's `ActorAvatar` (with its own `PodReceiver`, `PodAvatar` and Y Bot): the machine that hears pods and has calibrated sends the final bone rotations, everyone else copies them onto their own Y Bot, rooted at the actor's headset; `SessionCommand.Calibrate` starts the calibration on the machine with the pods.
- Day-to-day testing so far: actor as a build on a Quest 3, director in the Editor's Play mode with the XR Device Simulator.

### Roles and build profiles

One codebase, role chosen at compile time: `DIRECTOR_BUILD` = director, no define = actor, `SERVER_BUILD` = future PC server (not checked by any code yet, so it behaves like actor). The role is set with **Tesis → Role → Director / Actor / Server** ([`RoleSwitcher.cs`](Assets/Editor/RoleSwitcher.cs)), which writes it into every build profile's own Scripting Defines — never into Player Settings. There are 4 build profiles under `Assets/Settings/Build Profiles/` (PC/Quest × Actor/Director; the Quest ones carry their own Player Settings with the Quest-specific values). The role only applies while a build profile is active. Don't put role defines back into Player Settings, and don't use internal Unity APIs to read/write a profile's Player Settings — `PlayerSettings.GetScriptingDefineSymbols_Internal` crashed the Editor.

## Project state

This is a Unity **6000.3.24f1** project (see `ProjectSettings/ProjectVersion.txt`). There is still no README, no CI workflow, and no build/test tooling configured — all iteration happens inside the Unity Editor. Controls (every button, per role and context) are documented in Spanish in [`Docs/Controles.md`](Docs/Controles.md) — keep it updated when controls change.

The repo is pushed to `origin` at `https://github.com/HaiAvila/Tesis` (private).

### Non-Unity folders

C# must live inside `Assets/`. Everything else must live outside it, or Unity imports it as junk.

```
Firmware/pod_v2/        Pod firmware (Arduino). Editable under the rules above.
Firmware/receiver_v2/   Receiver firmware (Arduino). Editable under the rules above.
Bridge/bridge.py        Serial <-> UDP bridge. Python + pyserial.
Bridge/cue.py           Send cues by hand: type "<pod> <cue> [param]" while bridge.py runs.
Docs/pod-reference.md   Full hardware reference.
```

Each `.ino` must sit in a folder matching its own name — Arduino requires it.

**Compiling the firmware** (checks it builds; it doesn't flash anything). The Arduino IDE's own CLI, both Seeed cores and the libraries are installed on this PC. Each firmware has its own core (see Hardware decisions):

```
"$LOCALAPPDATA/Programs/Arduino IDE/resources/app/lib/backend/resources/arduino-cli.exe" compile --fqbn Seeeduino:mbed:xiaonRF52840Sense --build-path <scratch dir> Firmware/pod_v2
"$LOCALAPPDATA/Programs/Arduino IDE/resources/app/lib/backend/resources/arduino-cli.exe" compile --fqbn Seeeduino:nrf52:xiaonRF52840 --build-path <scratch dir> Firmware/receiver_v2
```

`pod_v2.ino`'s `#define POD_ID` is per pod: the user edits it before flashing each one, so it shows up as a local change. Don't commit a different value by accident.

### Scenes and flow

- **`Bootstrap.unity`** — entry point. [`Bootstrap.cs`](Assets/Scripts/Bootstrap.cs) is `DontDestroyOnLoad`, holds the `Realtime` connection and the persistent controllers (database, recorder, playback, viewfinder, session sync…), and on `didConnectToRoom` loads `MenuBackdrop` plus `Director_HomeMenu` or `Actor_HomeMenu` additively, depending on the role.
- **`MenuBackdrop.unity`** — the XR rig and backdrop that the menu scenes (home menus, `SceneSelect`) load on top of. `SceneSelect` has no rig of its own, so it's always loaded via `Bootstrap.LoadSceneSelect()`.
- **Director home**: "Ensayo Remoto" (live rehearsal with the actor) and "Inspección de Escenas" (review recorded sessions). **Actor home**: "Ensayo Remoto" (wait for the director's pick, then follow into the take) and "Ensayar Solo".
- **`SceneSelect.unity`** — shot list from the database, grouped by scene ([`SceneSelectController.cs`](Assets/Scripts/SceneSelectController.cs)); World Space, placed in front of the head.
- **`Scene1_mountain` / `Scene2_spaceship` / `Scene3_apartment`** — the 3 content scenes, each with its `.glb` models under `Assets/Models/Scene1|2|3/` and a skybox under `Assets/Skyboxes/`. Each contains an **XR Device Simulator** tagged `EditorOnly` so it's stripped from builds — left in a build, it replaces the real headset with a still, simulated one (scene locked to the head, view at floor level).
- **`NormcoreDemo.unity`** — the original Normcore proof-of-concept, not part of the app flow.

All UI is World Space and built at runtime in code (no prefabs). Panels get controller-ray support through [`XRUISetup.cs`](Assets/Scripts/XRUISetup.cs) (`XRUIInputModule` + `TrackedDeviceGraphicRaycaster`). Panels that can be on screen while a virtual camera renders go on the `UI` layer, which those cameras exclude. Project layers: `DirectorViewfinderPanel` (8), `ControllerRayVisual` (9), `PlaybackMonitor` (10). Icons that the legacy font renders badly (star, heart, play/pause) are generated sprites in [`UIIcons.cs`](Assets/Scripts/UIIcons.cs). **Every new world-space panel must register with [`PanelFocusManager`](Assets/Scripts/PanelFocusManager.cs)**: `OpenModal`/`CloseModal` for a menu, `RegisterPersistent` for something that stays up while you work, `RegisterGuide` for the tutorial. That's what keeps menus from overlapping other panels.)

A per-mode controls tutorial ([`TutorialController.cs`](Assets/Scripts/TutorialController.cs)) is offered the first time each mode's content scene opens (remembered in `PlayerPrefs`) and can be replayed from the pause menu. When a control changes, update its tutorial step too.

All on-screen text and `Docs/Controles.md` are in **Mexican Spanish** (tú: "presiona", "puedes", "¿Quieres…?") — never Argentine voseo ("apretá", "podés", "¿Querés…?") or Argentine vocabulary ("tildar").

### Shot list database

[`ShotListDatabase.cs`](Assets/Scripts/ShotListDatabase.cs) opens/creates SQLite at `Application.persistentDataPath/shotlist.db` and imports `Assets/StreamingAssets/ShotListDB.csv` into the `Shots` table on start (on Android the CSV is read with `UnityWebRequest`, since StreamingAssets is inside the APK). Recorded rehearsals go to `Sesiones` (with `es_preferida` / `es_favorita`, `scene_number`, `tipo` = "toma" for a shot or "escena" for a whole-scene take with `shot_number` 0, and `session_id`, a GUID — not the local `id` that `sesion_id` elsewhere points to), the actor's samples to `Metricas`, the director's viewfinder camera to `CamaraTrack`. `CameraFramings` still exists in the schema but nothing reads or writes it anymore. New columns are added with a `PRAGMA table_info` check + `ALTER TABLE`, so older databases keep working.

## Working with this project

**After every task, update [`Docs/STATUS.md`](Docs/STATUS.md) and commit it with the change. If a prompt references code that doesn't exist, stop and report before doing anything.** `STATUS.md` audits the code against the plans in `Docs/plan/` (`unity-cambios-existentes.md`, `unity-priorities.md`) and describes the networking as it actually is.

**Split of responsibility**: Claude may add GameObjects, attach components, and wire serialized-field references directly — this doesn't require the user to do it by hand. What stays the user's job is visual/spatial judgment: positioning objects, choosing materials or skyboxes, aesthetic decisions. If a task crosses from "wire this reference" to "decide how it should look or exactly where it should sit," say so instead of guessing.

**Live Editor control works** through the `unity` CLI (`unity status`, `unity command eval_file --file …`, `recompile` / `recompile_status`, `menu --path …`, `editor_play` / `editor_stop`, `open_scene`, `save_scene`…). Prefer it over hand-editing scene YAML. Before opening or saving scenes, check which scenes are open and whether they have unsaved changes. To compile-check both roles, use the Tesis → Role menu and switch back to the user's role afterwards.

Use the `unity-package-management` skill for adding/removing UPM packages, rather than editing `Packages/manifest.json` by hand.

### Haptics work in particular

Three things were unresolved and must not be guessed at; the conversion is settled, the mapping/calibration is built:

1. **The quaternion coordinate conversion — settled on hardware, 2026-11-03: conversion 1, no flips.** It's the default in `PodReceiver` and in both test scenes; don't change it. It's the only one that maps the BNO085's right-handed, Z-up frame to Unity's left-handed, Y-up frame (the others are inverted or mirrored). How the chip sits inside the pod (upside down, turned 90°) isn't part of the conversion: it's a fixed body offset, the same for every pod, that the zero/calibration removes — and one pose can't separate it from the BNO's arbitrary heading. `PodReceiver.ToUnity()` keeps the four candidates selectable from the inspector (1 and 2 are the same rotation, `q` vs `−q`), plus **`flipX` / `flipY` / `flipZ`** to mirror a Unity axis after the conversion (mirroring X reverses turns about Y and Z; if only one kind of turn is backwards, the conversion is wrong, not the flips). They live on `PodReceiver`, so the whole app uses the same setting. Two scenes, each played on its own: **`PodDiagnostics`** (one cube per pod, **C** cycles the conversion) and **`PodAxisTest`** (one pod-shaped box for one chosen pod: a two-step zero, **Z** flat with USB right then **X** with USB up — one step can't separate the BNO's arbitrary heading from the tilts — **C** conversion, **1/2/3** flips, raw/converted quaternions, euler, packets per second) — use them to re-check if the firmware's report or the hardware ever changes. The two-step zero there is a bench check, awkward on a worn pod; the body calibration is a separate, pose-based design (T-pose plus a second pose).
2. **Pod-to-limb mapping and body calibration — built, tried on the user (legs and body very good, arms close).** [`PodAvatar`](Assets/Scripts/Haptics/PodAvatar.cs) drives a Humanoid (the Y Bot, `Assets/Avatar/Y Bot.fbx`) from the pods: its `mapping` list (pod ID → `HumanBodyBones`, defaults from [`Docs/pod-map.md`](Docs/pod-map.md)) is editable in the inspector. Each reading is `Q = R·B·M` (R = the pod's own heading, a yaw; B = the bone; M = how it's taped on), so calibration takes **two poses**: a **T-pose** (B = the avatar's rest pose) and **arms down + slight squat with a little forward lean** (each bone turns by a known delta `SecondPoseDelta` about a horizontal axis; the observed axis gives R — only its direction matters, not how deep the squat is). Then `M = (R·T)⁻¹·Q1` (for the arms: averaged with the exact arms-down pose — arms swung down sideways, palms to the thighs, against the torso — and then fitted to a **third pose**, arms straight out in front with the palms pressed together, so the hands meet when the actor's do) and every frame `B = R⁻¹·Q·M⁻¹`. The hips take the chest pod's yaw; the feet stay flat, the lowest sole on the floor and the weight-bearing foot planted (so a squat lowers the hips and sends them back); the shoulders take a share of their upper arm's swing; other unmapped bones keep their rest pose. Then [`AvatarCollision`](Assets/Scripts/Haptics/AvatarCollision.cs) keeps the limbs out of the body and each other (capsules sized from the mesh; an overlap is pushed out by turning the arm at the shoulder or the leg at the hip). `PodAvatar.body` resizes the avatar to the actor (height + joint-to-joint segment lengths; widths only as joint spans, not outer widths) — rotations alone can't make two hands meet if the lengths are wrong. Test scene **`PodAvatarTest`** (played on its own, camera behind the avatar so the actor's left is its left; **K**/button = Calibrar, with a countdown for each pose).
3. **End-to-end latency.** Unity cue → vibration on skin has never been measured. Target is under ~100 ms. Measure it before building all five cues on top of it.

Testing haptics needs the hardware running: pods powered, receiver plugged in, `python bridge.py` going from the `Bridge/` folder. Close the Arduino Serial Monitor first — it holds the COM port. Without the bridge, `PodReceiver` simply receives nothing; it won't error.

## Packages

`Packages/manifest.json` includes, beyond Unity's default/standard packages:
- **Normcore** (`com.normalvr.normcore`, 3.6.1, via the `Normal` scoped registry at `https://normcore-registry.normcore.io`).
- **XR**: `com.unity.xr.openxr` (1.18.0), `com.unity.xr.interaction.toolkit` (3.6.1, with its Starter Assets and XR Device Simulator samples under `Assets/Samples/`), `com.unity.xr.management` (4.7.0).
- **`com.unity.cloud.gltfast`** (6.14.1) — the `.glb` models in the content scenes.

PolySpatial is **not** installed (paused indefinitely).

Outside UPM, `Assets/Plugins/SQLite4Unity3d/` (`SQLite.cs`) is the SQLite binding, with two native libraries: `Assets/Plugins/x64/sqlite3.dll` (Editor/PC) and `Assets/Plugins/Android/libs/arm64-v8a/libsqlite3.so` (Quest; compiled from the official SQLite amalgamation with Unity's bundled Android NDK).

Normcore-related assets: `Assets/Normal/`, `Assets/Settings/NormcoreAppSettings.asset`, `Assets/NormcoreProjectSettingsBuildProcessor/`, `Assets/EasySyncBuildProcessor/`.

**Ask before adding any package or dependency.**

## Git / gitignore

The `.gitignore` is the standard Unity template: `Library/`, `Temp/`, `Obj/`, `Build*/`, `Logs/`, `UserSettings/` are all ignored, so only source-controlled project files (Assets, Packages, ProjectSettings, .vscode/.claude config) are tracked. Never commit the `Library/` folder — it's large, regeneratable, and already excluded.

Also ignored: `Bridge/*.csv` (bridge session logs — commit individual runs by hand if they're wanted as thesis evidence) and `.utmp/` (Unity's Android build cache; it was committed once by accident and untracked with `git rm -r --cached .utmp`).
