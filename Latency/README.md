# Latency test results

Cue from Unity to the motor's buzz, heard by the PC's microphone (`LatencyTest` scene,
`Assets/Scripts/Haptics/LatencyTest.cs`). Each run: a `.csv` with every trial, a `_resumen.txt`
summary, and the `.wav` of the microphone during the run. The microphone's own input delay is
included, so the numbers slightly overestimate the real latency.

| Run | Result | Notes |
|---|---|---|
| `latencia_20261109_112429` | 0 of 30 detected | Not valid: AirPods were connected, so the PC microphone recorded nothing useful. Kept for the record. |
| `latencia_20261109_113404` | 30/30 · median 55 ms · mean 61 · p90 93 · min 31 · max 103 ms | **Preliminary.** Pod 1, LONG 200 ms, 235 Hz. Someone was talking during the run. To repeat in silence, with all 9 pods connected. |

Target: under ~100 ms (CLAUDE.md, "Haptics work in particular").
