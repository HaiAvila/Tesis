# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

This is a thesis project (Tesis) building a **haptic feedback system for actors in virtual production**: wearable bands with IMUs and LRA motors that cue/guide an actor during rehearsal and live takes. The Unity project is the software side of this hardware+software system — it will host the XR views, the networked sync between actor and director, and the logic that drives haptic cues from scene/timing events.

### Hardware decisions (already fixed — don't second-guess or suggest alternatives)

- **LRA haptic motor**: Vybronics VG0832013D, one per band, across **9 bands**: waist/pelvis, biceps x2, wrists x2, quadriceps x2, calves x2.
- **IMU**: BNO085 primary, ICM-20948 as fallback/backup.
- **Microcontroller**: nRF52840 (per band, presumably BLE-connected to the Unity host — confirm before assuming a specific transport).
- **Haptic driver**: DRV2605L (drives the LRA from the microcontroller).

### XR / networking architecture

Dual-view VR setup, synchronized over the network:
- **Actor's view**: Apple Vision Pro, via **PolySpatial**.
- **Director's view**: Meta Quest.
- **Network sync between the two**: **Normcore**.

**Normcore is now installed** (see "Packages" below), with a minimal sync demo scene. PolySpatial and Meta XR/OpenXR are still not installed. When they're added, and as the demo grows into the real actor/director split, document the actual assembly/scene structure here (e.g. how actor vs. director builds are separated, where haptic-cue logic lives, how band telemetry reaches Unity).

## Project state

This is a Unity **2022.3.58f1 LTS** project (see `ProjectSettings/ProjectVersion.txt`). It is currently a fresh project scaffold: there are no custom C# scripts, assembly definitions, or tests yet — only the default `Assets/Scenes/SampleScene.unity` and standard project/package configuration. There is no README, no CI workflow, and no build/test tooling configured yet. As real code and assets are added, this file should be updated to describe the actual architecture (assembly boundaries, key scenes, gameplay/XR systems, etc.) rather than the empty-project state described below.

The repo is pushed to `origin` at `https://github.com/HaiAvila/Tesis` (private).

## Working with this project

There is no CLI build/test pipeline in the repo yet — all iteration happens inside the Unity Editor. Use the `unity-cli` skill to drive the Editor from the terminal (open the project, run play mode, execute C# in a live Editor, run builds/tests) instead of hand-editing scene/asset files. Use the `unity-package-management` skill for adding/removing UPM packages headlessly, rather than editing `Packages/manifest.json` by hand.

## Packages

`Packages/manifest.json` lists Unity's default/standard packages for a new project (TextMeshPro, Timeline, UGUI, Visual Scripting, collab-proxy, and the built-in engine modules) plus **Normcore** (`com.normalvr.normcore`, 3.6.0, via the `Normal` scoped registry at `https://normcore-registry.normcore.io`) for network sync. PolySpatial and Meta XR/OpenXR are not installed yet — check `Packages/manifest.json` before assuming either is present.

A minimal Normcore integration exists under `Assets/Normal/`, `Assets/Settings/NormcoreAppSettings.asset`, and the demo scene `Assets/Scenes/NormcoreDemo.unity` (connector script: `Assets/Scripts/NormcoreDemoConnector.cs`, editor setup: `Assets/Editor/NormcoreDemoSetup.cs`). This is a scaffold/proof-of-concept for the actor↔director sync, not the final architecture.

## Git / gitignore

The `.gitignore` is the standard Unity template: `Library/`, `Temp/`, `Obj/`, `Build*/`, `Logs/`, `UserSettings/` are all ignored, so only source-controlled project files (Assets, Packages, ProjectSettings, .vscode/.claude config) are tracked. Never commit the `Library/` folder — it's large, regeneratable, and already excluded.
