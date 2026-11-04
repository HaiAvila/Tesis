#!/usr/bin/env python3
"""
Pod bridge: receiver XIAO (USB serial) <-> Unity (UDP).

    python bridge.py                 auto-detect the port
    python bridge.py COM7            use a specific port
    python bridge.py COM7 --nolog    no CSV

Needs: pip install pyserial

Serial lines are forwarded to Unity verbatim, so what you see in the Arduino
Serial Monitor is exactly what Unity receives. Nothing is reformatted.

    pod -> Unity    UDP 127.0.0.1:5005    "P,3,0.0120,-0.9981,..."
                                          "S,9,9,1,2,3,..."
                                          "H,3,3920,0,-58"   (mV, charging, RSSI; every 2 s)
    Unity -> pod    UDP 127.0.0.1:5006    "C,3,2,20"
"""

import csv, socket, sys, threading, time
from datetime import datetime

import serial
from serial.tools import list_ports

UNITY_HOST  = "127.0.0.1"
UNITY_PORT  = 5005      # bridge sends here
COMMAND_PORT = 5006     # bridge listens here
BAUD = 115200

running = True
ser = None


def find_port():
    """Pick the first port that looks like a Seeed/Nordic board."""
    ports = list(list_ports.comports())
    for p in ports:
        desc = (p.description or "").lower()
        if any(k in desc for k in ("xiao", "seeed", "nrf", "usb serial")):
            return p.device
    if len(ports) == 1:
        return ports[0].device
    print("Could not pick a port. Available:")
    for p in ports:
        print(f"   {p.device}  {p.description}")
    return None


def open_serial(port):
    while running:
        try:
            s = serial.Serial(port, BAUD, timeout=1)
            print(f"[bridge] serial open on {port}")
            return s
        except serial.SerialException as e:
            print(f"[bridge] cannot open {port}: {e}  retrying in 2s")
            time.sleep(2)
    return None


def command_listener():
    """Unity -> bridge -> receiver."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", COMMAND_PORT))
    sock.settimeout(1.0)
    print(f"[bridge] listening for cues on UDP {COMMAND_PORT}")
    while running:
        try:
            data, _ = sock.recvfrom(256)
        except socket.timeout:
            continue
        except OSError:
            break
        line = data.decode("utf-8", "ignore").strip()
        if not line.startswith("C,"):
            continue
        if ser and ser.is_open:
            try:
                ser.write((line + "\n").encode())
                print(f"[cue] {line}")
            except serial.SerialException:
                pass
    sock.close()


def main():
    global running, ser

    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    port = args[0] if args else find_port()
    if not port:
        sys.exit(1)

    logging = "--nolog" not in sys.argv
    writer = logf = None
    events = eventf = None
    if logging:
        name = datetime.now().strftime("pods_%Y%m%d_%H%M%S.csv")
        logf = open(name, "w", newline="")
        writer = csv.writer(logf)
        writer.writerow(["t_ms", "pod", "qi", "qj", "qk", "qr"])
        # The receiver's R (connected / lost), S (status), E (error) and H (battery, charging,
        # RSSI) lines, on the same clock as the samples - so a gap in the samples can be matched
        # to what the receiver was doing (e.g. a pod reconnecting, or a battery running out).
        eventf = open(name.replace(".csv", "_events.csv"), "w", newline="")
        events = csv.writer(eventf)
        events.writerow(["t_ms", "line"])
        print(f"[bridge] logging to {name} (+ receiver events to {name.replace('.csv', '_events.csv')})")

    ser = open_serial(port)
    if not ser:
        sys.exit(1)

    out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    threading.Thread(target=command_listener, daemon=True).start()

    t0 = time.time()
    samples = 0
    last_report = t0
    health = {}     # pod id -> latest H line's fields, for the periodic report only

    print(f"[bridge] forwarding to {UNITY_HOST}:{UNITY_PORT}   ctrl-C to stop")
    try:
        while True:
            try:
                raw = ser.readline()
            except serial.SerialException:
                print("[bridge] serial dropped, reopening")
                if events:
                    events.writerow([int((time.time() - t0) * 1000), "bridge: serial dropped, reopening"])
                ser = open_serial(port)
                continue
            if not raw:
                continue

            line = raw.decode("utf-8", "ignore").strip()
            if not line:
                continue

            out.sendto(line.encode(), (UNITY_HOST, UNITY_PORT))

            if line.startswith("P,"):
                samples += 1
                if writer:
                    f = line.split(",")
                    if len(f) == 6:
                        writer.writerow([int((time.time() - t0) * 1000)] + f[1:])
            elif line.startswith("H,"):
                # Only for the report below: a garbled line is still forwarded and logged,
                # and must never stop the bridge.
                try:
                    _, pod, mv, charging, rssi = line.split(",")
                    health[int(pod)] = (int(mv), int(charging), int(rssi))
                except ValueError:
                    pass
                if events:
                    events.writerow([int((time.time() - t0) * 1000), line])
            elif line[0] in "RSE":
                print(f"[rx] {line}")
                if events:
                    events.writerow([int((time.time() - t0) * 1000), line])

            now = time.time()
            if now - last_report >= 5:
                print(f"[bridge] {samples / (now - last_report):.0f} samples/s")
                if health:
                    print("[pods] " + "  ".join(
                        f"{pod}:{mv / 1000:.2f}V{'+' if charging == 1 else ''}/{rssi}dBm"
                        for pod, (mv, charging, rssi) in sorted(health.items())))
                samples = 0
                last_report = now

    except KeyboardInterrupt:
        print("\n[bridge] stopping")
    finally:
        running = False
        if ser and ser.is_open:
            ser.close()
        if logf:
            logf.close()
        if eventf:
            eventf.close()


if __name__ == "__main__":
    main()
