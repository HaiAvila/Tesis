# Pod map

Which pod (its firmware `POD_ID`) goes on which body part, the humanoid bone it drives, and
which way its USB end points when it's taped on.

| Pod | Body part | Humanoid bone | USB end points |
|---|---|---|---|
| 1 | Left wrist | `LeftLowerArm` | toward the hand |
| 2 | Right wrist | `RightLowerArm` | toward the hand |
| 3 | Left bicep | `LeftUpperArm` | toward the hand |
| 4 | Right bicep | `RightUpperArm` | toward the hand |
| 5 | Left quad | `LeftUpperLeg` | toward the knee |
| 6 | Right quad | `RightUpperLeg` | toward the knee |
| 7 | Left shin | `LeftLowerLeg` | toward the foot |
| 8 | Right shin | `RightLowerLeg` | toward the foot |
| 9 | Sternum | `Chest` | toward the neck |

**No hip pod.** The hips follow pod 9's yaw plus the headset's position.

**The proximity cue moves from the waist to pod 9** (and so does the anticipatory alert,
which was "waist only").

This is the default assignment. In Unity the pod-to-bone mapping still has to be a
serializable mapping editable in the Inspector (CLAUDE.md, "Haptics work"), so a pod can be
reassigned without recompiling.
