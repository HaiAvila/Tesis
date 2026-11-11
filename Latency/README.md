# Latency test results

Cue from Unity to the motor's buzz, heard by the PC's microphone (`LatencyTest` scene,
`Assets/Scripts/Haptics/LatencyTest.cs`, with `bridge.py`). Each run: a `.csv` with every trial,
a `_resumen.txt` summary, and the `.wav` of the microphone during the run. LONG 200 ms, 30
trials. The microphone's own input delay is included, so the numbers slightly overestimate
the real latency.

The microphone only hears the pod next to it, so one pod is cued per run. "Pods connected"
is how many pods were on and linked to the receiver at the time: the nine share one radio
link, so a cue can wait longer when all of them are sending.

## The measurement (Nov 10, 2026, quiet room)

| Run | Pods connected | Cued pod | Result |
|---|---|---|---|
| `latencia_20261110_173717` | 1 | 4 | 30/30 · median 53 ms · mean 52 · p90 73 · min 25 · max 85 ms |
| `latencia_20261110_183626` | 9 | 2 | 30/30 · median 75 ms · mean 73 · p90 116 · min 23 · max 131 ms |
| `latencia_20261110_183836` | 9 | 2 | 30/30 · median 60 ms · mean 63 · p90 94 · min 30 · max 122 ms |

**With all 9 pods connected (both runs, 60 cues): median 62.5 ms, mean 68 ms, p90 110 ms,
max 131 ms; 9 of 60 over 100 ms.** With one pod: median 52.5 ms, none over 100 ms. So the
typical cue arrives well under the ~100 ms target, and sharing the radio with eight other
pods adds about 10 ms to the median and makes roughly one cue in seven arrive after 100 ms.

## Not measurements

| Run | Why |
|---|---|
| `latencia_20261109_112429` | 0 of 30 detected: AirPods were connected, so the PC microphone recorded nothing useful. |
| `latencia_20261109_113404` | 30/30, median 55 ms, but someone was talking and only pod 1 was on. Superseded by the Nov 10 runs. |
| `latencia_20261110_173900`, `_174008`, `_174128` | The user moving pod 4 around to find where the buzz is felt best: 9-10 of 30 detected, the timings are false detections. |

Target: under ~100 ms (CLAUDE.md, "Haptics work in particular").
