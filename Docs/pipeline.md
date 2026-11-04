# Pipeline

The whole system: who uses it, on which devices, and what runs where. Keep in sync with
`CLAUDE.md` (its "Pipeline" summary points here).

## Roles and devices

- **Previz team** — builds the Unity scenes, uploads the shot list, and makes all builds:
  actor APK, director APK, director desktop build, server `.exe`. On shoot day it runs the
  system from its own PC. **Only the previz team ever opens Unity.**
- **Actor** — Quest 3 with the actor build + the 9 pods + a PC with the receiver XIAO on USB
  running the server `.exe` (`SERVER_BUILD`). The actor only plugs in the receiver,
  double-clicks the `.exe`, turns on the pods and puts on the Quest. **No configuration.**
- **Director** — Quest 3 with the director build. Optional: a PC with the desktop director
  build, mainly to edit the haptic cue timeline with a mouse. Can be somewhere else than the
  actor: remote rehearsal over the internet through Normcore.

## The server `.exe` (runs where the pods are)

```
pods ──BLE──> receiver ──USB──> bridge ──> pose solver ──> AvatarModel ──Normcore──> director sees the avatar
                                              (calibration offsets + body measurements)
```

It also:
- receives the actor's headset pose from Normcore (the avatar's root);
- runs the calibration when the director presses **Calibrar**;
- records the takes with pose data;
- generates automatic cues from the approved take;
- plays live cues and the cue timeline on the pods;
- makes the take report.

**The director's app never talks to the pods directly.** Commands — Calibrar, Grabar,
Acción, Corte, Aprobar, edited cues — go through Normcore to the server.

## Setups

1. **Remote rehearsal** — actor + pods + server PC in one place, director in another. The
   director picks scene/take, calibrates, records, approves.
2. **Ensayar solo** — actor + pods + server PC, no director. The actor practices an approved
   take with cues.
3. **Inspeccionar escenas** — director only (Quest or desktop). Replays takes, marks the
   preferred one, edits the haptic timeline. No pods needed.
4. **Shoot day** — the actor wears only the pods, no Quest. The previz team's PC runs the
   server with the receiver: picks scene and take, loads the timeline, triggers **Acción**
   (3 pulses) and **Corte** (1 long pulse). Cues come from the approved rehearsal's timeline.
5. **Testing now (one Quest)** — the Editor on the user's PC plays the server role, with the
   pods on USB. Both roles get tested by swapping builds.

**Whichever machine has the pods sends the avatar** — the server `.exe` in setups 1, 2 and 4,
the Editor in setup 5 — so the avatar code doesn't depend on the build role. Built as
`AvatarPoseSync` (Bootstrap → `ActorAvatar`): the machine that hears pods and has calibrated
owns `AvatarPoseModel` and sends the pose; the director's app shows it, rooted at the actor's
headset. Calibrar (`SessionCommand.Calibrate`) runs on the machine with the pods.

## Body measurements

Entered by hand for now (`PodAvatar.body`: height and joint-to-joint lengths). Measuring them
from an iPhone video (MediaPipe) is future work.

## Cloud (planned, not now)

An API + database on DigitalOcean for persistence: shot list, takes, references, cue tracks,
reports, body measurements. Normcore stays for live sync; the local databases become caches.
**Don't build this yet.**
