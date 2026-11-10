#!/usr/bin/env python3
"""
sim_pods.py - nine pretend pods, for testing without the hardware.

Sends what bridge.py would forward from the receiver (P, S and H lines, UDP 5005) and prints
every cue command the app sends back (UDP 5006). Nothing here touches the real pods; don't run
it together with bridge.py (both listen on 5006).

    python sim_pods.py              stand in a T-pose, then follow a calibration when told
    python sim_pods.py --calibrate  start the calibration poses right away (press Calibrar in
                                    the app at the same moment)

The poses follow PodAvatar's calibration by the clock: T-pose for the first countdown and
capture (4 s + 1 s), arms down with a slight squat for the second, arms to the front for the
third, then it keeps moving (arms swinging, a slow squat) so the avatar is seen to move. With
no --calibrate the sequence starts when a calibration cue arrives (the first CLICK to all pods).

Each pod's reading is the rotation of its bone from the T-pose in the avatar's own frame (it
faces +Z, left is -X), as if every pod were taped on squarely and switched on facing the same
way - the simplest case for the solver. Sent in the BNO085's order (i, j, k, real), through the
inverse of PodReceiver's conversion 1.
"""

import math, socket, sys, threading, time

UNITY = ("127.0.0.1", 5005)
COMMAND_PORT = 5006
RATE_HZ = 11.0

# pod id -> bone (Docs/pod-map.md)
BONES = {1: "l_forearm", 2: "r_forearm", 3: "l_upper_arm", 4: "r_upper_arm",
         5: "l_thigh", 6: "r_thigh", 7: "l_shin", 8: "r_shin", 9: "chest"}

COUNTDOWN, CAPTURE = 4.0, 1.0


def axis_angle(degrees, axis):
    half = math.radians(degrees) / 2.0
    s = math.sin(half)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half))


RIGHT, UP, FORWARD = (1, 0, 0), (0, 1, 0), (0, 0, 1)
IDENTITY = (0.0, 0.0, 0.0, 1.0)


def second_pose(bone, amount=1.0):
    """PodAvatar.SecondPoseDelta, scaled."""
    if bone in ("l_upper_arm", "l_forearm"):
        return axis_angle(90 * amount, FORWARD)
    if bone in ("r_upper_arm", "r_forearm"):
        return axis_angle(-90 * amount, FORWARD)
    if bone.endswith("thigh"):
        return axis_angle(-45 * amount, RIGHT)
    if bone.endswith("shin"):
        return axis_angle(30 * amount, RIGHT)
    return axis_angle(20 * amount, RIGHT)


def third_pose(bone):
    """Standing straight, arms to the front."""
    if bone in ("l_upper_arm", "l_forearm"):
        return axis_angle(90, UP)
    if bone in ("r_upper_arm", "r_forearm"):
        return axis_angle(-90, UP)
    return IDENTITY


def moving(bone, t):
    """After the calibration: arms swing down and up, a slow shallow squat."""
    swing = 0.5 + 0.5 * math.sin(t * 1.6)          # 0 = T-pose, 1 = arms down
    squat = 0.5 + 0.5 * math.sin(t * 0.8)
    if "arm" in bone:
        return second_pose(bone, swing)
    return second_pose(bone, squat * 0.6)


def pose(bone, t):
    """t = seconds since the calibration started (None = not started: T-pose)."""
    if t is None or t < COUNTDOWN + CAPTURE + 0.5:
        return IDENTITY
    if t < 2 * (COUNTDOWN + CAPTURE) + 0.5:
        return second_pose(bone)
    if t < 3 * (COUNTDOWN + CAPTURE) + 0.5:
        return third_pose(bone)
    return moving(bone, t)


def to_bno(q):
    """Unity (x, y, z, w) -> the pod's (i, j, k, real). Conversion 1 is (-i, -k, -j, r)."""
    x, y, z, w = q
    return (-x, -z, -y, w)


started = None
CUE_NAMES = {0: "STOP", 1: "CLICK", 2: "PULSE", 3: "TRIPLE", 4: "LONG", 5: "DOUBLE",
             6: "DONE", 7: "FAIL", 8: "STEP", 9: "RISE"}


def listen_for_cues():
    global started
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", COMMAND_PORT))
    t0 = time.time()
    while True:
        data, _ = sock.recvfrom(256)
        line = data.decode("utf-8", "ignore").strip()
        parts = line.split(",")
        name = ""
        if len(parts) >= 3 and parts[2].isdigit():
            name = CUE_NAMES.get(int(parts[2]), "?")
        print(f"[cue {time.time() - t0:7.2f}s] {line}  {name}", flush=True)
        # The calibration's first countdown CLICK goes to every pod, 3 s before the capture.
        if started is None and line.startswith("C,0,1"):
            started = time.time() - (COUNTDOWN - 3.0)
            print("[sim] calibration seen: doing the poses", flush=True)


def main():
    global started
    if "--calibrate" in sys.argv:
        started = time.time()
        print("[sim] calibration poses from now")

    threading.Thread(target=listen_for_cues, daemon=True).start()
    out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"[sim] 9 pretend pods -> {UNITY[0]}:{UNITY[1]}, cues printed from UDP {COMMAND_PORT}. ctrl-C to stop", flush=True)

    last_status = 0.0
    step = 1.0 / (RATE_HZ * len(BONES))
    while True:
        for pod, bone in BONES.items():
            now = time.time()
            t = None if started is None else now - started
            i, j, k, r = to_bno(pose(bone, t))
            out.sendto(f"P,{pod},{i:.4f},{j:.4f},{k:.4f},{r:.4f}".encode(), UNITY)
            if now - last_status >= 1.0:
                last_status = now
                out.sendto(("S,9,9," + ",".join(str(p) for p in BONES)).encode(), UNITY)
                for p in BONES:
                    out.sendto(f"H,{p},{3700 + p * 40},0,{-55 - p}".encode(), UNITY)
            time.sleep(step)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
