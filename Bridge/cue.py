#!/usr/bin/env python3
"""
Send haptic cues to pods through bridge.py. Run this in a SECOND terminal
while bridge.py is running in the first.

    python cue.py

Then type:

    9 2 30     pod 9, PULSE every 300 ms  (keeps going)
    9 0        pod 9, STOP
    9 1        pod 9, one click
    0 3        ALL pods, TRIPLE  (camera start)
    0 4 60     ALL pods, LONG 600 ms  (cut)
    q          quit

Cues: 0 STOP   1 CLICK   2 PULSE(param = ms/10)   3 TRIPLE
      4 LONG(param = ms/10, 0 = 500 ms)           5 DOUBLE

Pod id 0 means broadcast to every connected pod.
"""

import socket

HOST, PORT = "127.0.0.1", 5006
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

NAMES = {0: "STOP", 1: "CLICK", 2: "PULSE", 3: "TRIPLE", 4: "LONG", 5: "DOUBLE"}

print(__doc__)

while True:
    try:
        line = input("> ").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        break

    if not line or line in ("q", "quit", "exit"):
        break

    parts = line.split()
    if len(parts) < 2:
        print("  need at least:  <pod> <cue>")
        continue

    try:
        pod = int(parts[0])
        cue = int(parts[1])
        param = int(parts[2]) if len(parts) > 2 else 0
    except ValueError:
        print("  numbers only:  <pod> <cue> [param]")
        continue

    msg = f"C,{pod},{cue},{param}"
    sock.sendto(msg.encode(), (HOST, PORT))

    where = "all pods" if pod == 0 else f"pod {pod}"
    print(f"  {msg}   -> {NAMES.get(cue, '?')} on {where}")
