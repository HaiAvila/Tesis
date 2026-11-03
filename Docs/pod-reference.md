# Haptic Pod — Build & Connection Reference

VR rehearsal system, thesis project.
Last updated: 2026-11-02

---

## 1. Bill of Materials (per pod)

| Component | Part | Notes |
|---|---|---|
| MCU | Seeed XIAO nRF52840 (non-Sense) | BLE, USB-C retained |
| Haptic driver | DRV2605L — UNIT Electronics DevLab (UE0065) | 5 pads, power LED on VDD only |
| IMU | GY-BNO085 — UNIT Electronics | 10 pads |
| Actuator | Vybronics VG0832013D LRA | Same on all 9 bands |
| Battery | LiPo 3.7 V / 150 mAh | Bare +/− wires, no JST |
| Switch | **SS-12D00** SPDT slide, 30 V / 0.3 A | Off-board, through the TPU wall |
| PCB | Custom 2-layer flex, 40 × 26.5 mm | JLCPCB, 25 µm polyimide |

**Quantities:** 9 pods, plus a **10th XIAO as the BLE receiver** (USB-powered,
never needs a battery — a module with a damaged BAT+ pad is ideal for this).

---

## 2. Connection Table

### Routed in copper — nothing to wire

| Net | XIAO pad | DRV pad | IMU pad | Other |
|---|---|---|---|---|
| 3V3 | 12 | 1 (VIN) | 1 (VCC) | — |
| GND | 13 | 2 | 2 | J1 pad 2 |
| SCL | 6 (D5) | 3 | 3 | — |
| SDA | 5 (D4) | 4 | 4 | — |
| IMU_INT | 4 (D3/A3) | — | 7 (INT) | — |
| IMU_RST | 3 (D2/A2) | — | 8 (RST) | — |
| LIPO_P | — | — | — | J1 pad 1, J2 pad 1 |

**Careful:** on both modules SCL is pad 3 and SDA is pad 4 — the reverse order
of the XIAO, where SCL is 6 and SDA 5. The numbers do not correspond.

### Hand-wired — 7 wires

| # | From | To | Wire |
|---|---|---|---|
| 1 | XIAO rear `+` pad | J2 `BAT+` | 30 AWG enamelled |
| 2 | LiPo + | J1 `LIPO_P` | its own |
| 3 | LiPo − | J1 `GND` | its own |
| 4 | Switch pin 2 (centre) | J2 `LIPO_P` | 28–30 AWG silicone |
| 5 | Switch pin 1 or 3 | J2 `BAT+` | 28–30 AWG silicone |
| 6 | LRA | DRV module `OUT+` | its own |
| 7 | LRA | DRV module `OUT−` | its own |

- **J2 `BAT+` takes two wires** (#1 and #5). Solder #5 first.
- **The XIAO's rear `−` pad gets nothing.** BAT− is GND internally.
- #6 and #7 land on the **DRV module itself**; OUT+/OUT− were never routed.

### Power path

```
LiPo + → J1 LIPO_P → (trace) → J2 LIPO_P → SWITCH → J2 BAT+ → wire → XIAO rear +
LiPo − → J1 GND → (trace) → XIAO pad 13
```

### Switch wiring (SS-12D00)

Pin 2 (centre) is common. Pin 2 → J2 `LIPO_P`, either end pin → J2 `BAT+`,
third pin unused — cut it flush to save space.

### Not connected

IMU: ADO, CS, PS1, PS0 · DRV: INT · XIAO: 1, 2, 7–11, 14–18, 21, 22

---

## 3. Critical Gotchas

**BNO085 I2C address is 0x4B, not 0x4A.** The UNIT module defaults opposite to
the Adafruit breakout. Use `begin_I2C(0x4B)`.

**`useLRA()` is mandatory.** Without it the LRA does not move at all, correct
wiring or not. Also call `selectLibrary(6)`.

**Effect 118 does NOT run until stopped.** Despite being named "long buzz for
programmatic stopping," it self-terminates after roughly 10 s. Verified on
hardware. Sustained cues must either re-trigger or use real-time playback (RTP)
mode, where you write an amplitude register directly.

**Board selection: "Seeed nRF52 Boards" — the classic core.** This is Adafruit's
Bluefruit stack, ported by Seeed. **Not** "Seeed nRF52 mbed-enabled Boards",
which uses ArduinoBLE and holds only one central connection. Arduino IDE 2.x
remembers the board **per window**, and both cores list a board called
"Seeed XIAO nRF52840 Sense" — always select from the **Tools → Board** menu,
where the core group name is visible.

**J2 has no negative pad.** J1 is LiPo + / −. **Both J2 pads are positive** —
pad 1 from the switch, pad 2 back to the XIAO's BAT+. Only a "+" is in
silkscreen. Wiring the battery to J2 pad 2 bypasses the switch entirely.

**3.3 V only on the IMU.** No onboard regulator.

**Python is required on Windows** for the classic core to compile — it builds
the `.uf2` with a Python script. Install from python.org with "Add python.exe
to PATH" checked, and disable the Microsoft Store `python.exe` app execution
alias.

---

## 4. Assembly — what actually goes wrong

Hand-soldering 2.54 mm modules onto 0.11 mm flex is the hardest part of the
build. Every failure encountered was one of these:

**Unfused joints.** Solder sits on both the pin and the pad without the two
ever joining. Symptom: the module works *only while pressed down*, and all
continuity tests pass, because probe pressure does the same thing a finger
does. Fix: flux, press the module flat, heat the **pad and pin together from
the side** (not the top of the pin), feed fresh solder, and **keep pressing for
3 seconds after removing the iron**. Solder that moves while solidifying forms
a grainy joint that looks fine and isn't.

**Solder bridges at 2.54 mm.** SDA-to-SCL was bridged under the DRV on one
board. Always test adjacent pads for continuity — the pairs that must *not*
beep matter as much as the ones that must.

**Lifted pads.** The XIAO's rear BAT+ pad tore off one module. Strain-relieve
the wire with Kapton *before* soldering so the pad never carries mechanical
load, and never tug-test that joint. A module with a dead BAT+ pad is still a
perfect receiver, since the receiver runs on USB.

**Technique:** 320 °C leaded / 340 °C lead-free, fine tip, flux every joint,
**2–3 seconds per pad maximum**. Tape the flex flat to scrap FR-4 or a ceramic
tile with Kapton at the edges.

### Order

1. Solder the BAT+ wire to the XIAO's rear `+` pad **before** mounting it
2. Kapton over the XIAO's rear pads
3. XIAO on the top layer, then power it over USB and confirm it enumerates
4. Flip, solder IMU and DRV on the bottom layer, then run an I2C scan
5. BAT+ wire to J2, LiPo to J1, switch to J2
6. LRA to the DRV module's OUT pads

Before connecting the battery: `LIPO_P` to `GND` must **not** beep in either
switch position.

---

## 5. RF — Antenna Rules

The chip antenna is at the **end opposite the USB-C connector**.

**Keep-out: 4 mm clear of copper, pads and module bodies.** Validated by
measurement, not estimated — the 4 mm layout gave the best RSSI of the whole
test session. The board was widened from 36 to 40 mm to achieve it.

**The battery matters more than the traces.** A LiPo touching the antenna cost
**10 dB**. Keep it away from that end inside the housing.

Residual compromise: the IMU_INT via and trace could not be moved fully clear.
Measured cost on the real board: nothing detectable.

### Measured RSSI, assembled board

| Condition | RSSI |
|---|---|
| ~1 m | ≈ −55 dBm |
| 3.2 m, varied orientation | −55 to −70 dBm |
| 3.2 m, body blocking (earlier mock-up) | ≈ −85 dBm |
| nRF52840 receiver floor | ≈ −95 dBm |

Margin is comfortable at every distance the system needs.

---

## 6. Magnetic Interference — Resolved

`SH2_MAGNETIC_FIELD_UNCALIBRATED` at 100 Hz, LRA on vs off at real pod
distance: 109.3–109.9 µT, p2p ≈ 3 µT in every configuration. Any LRA effect
equalled the sensor's own drift.

**No measurable interference. Mu-metal shielding not needed.** The LRA
compartment sits at the opposite end from the IMU, which is sufficient.

---

## 7. Power

| State | Current |
|---|---|
| Idle (LRA off) | ~20 mA |
| Clicks, 1/sec | ~50 mA peak |
| Continuous buzz | ~90 mA |

On 120 mAh usable: ~6 h idle, ~2.4 h clicks, ~1.3 h continuous, **~4.3 h
mixed**.

**No decoupling capacitors — confirmed unnecessary.** The earlier reset during
the buzz phase was caused by a multimeter in series, not by the board. Retested
on battery with no meter: the rail held through continuous 90 mA with no reset
and no dropped BLE link.

If a problem ever reappears, the fallback needs no PCB change: bridge an 0805
10 µF directly across the DRV module's VIN and GND pins, which are 2.54 mm
apart.

---

## 8. Cold boot

Two pods failed to start on battery while working fine after a USB upload.
Cause: the XIAO boots faster than the BNO085 finishes its own power-on, so
`begin_I2C` lost a race — and the original firmware hung forever on that
failure, so BLE never started and the pod was invisible.

**Fixed in firmware.** The pod now waits 500 ms, pulses RST manually, retries
the IMU ten times, and **always starts BLE regardless**. An LED code reports
the outcome on battery with no serial cable:

| Blink | Meaning |
|---|---|
| Slow, 1 s | Healthy: IMU readings arriving |
| Fast, 150 ms | BLE up, no IMU reading |

**Update 2026-11-02 — IMU watchdog.** A bench run sent `0,0,0,1` from both
pods for every one of ~6,900 samples: the pod started its quaternion at
`0,0,0,1` and kept sending it whenever the IMU had nothing, so "no reading"
looked like a pod lying perfectly still, and an IMU that failed at boot stayed
dead until a power cycle. The firmware now:

- sends **`0,0,0,0`** while there's no reading (Unity: "sin IMU");
- retries the IMU every 2 s if it failed at boot (one attempt each, so the cue
  engine isn't blocked for long);
- re-enables the rotation report after 1 s without a reading, and restarts the
  IMU after 5 s (back to `0,0,0,0` meanwhile);
- blinks slowly only while readings actually arrive (before, slow meant "the
  IMU started", even if it never sent anything);
- prints each step on the pod's own USB serial (115200): `IMU OK`,
  `IMU FAILED (will retry every 2 s)`, `IMU retry failed`, `IMU OK (retry)`,
  `IMU quiet, re-enabling report`, `IMU silent 5 s, restarting it`,
  `IMU enableReport failed`.

Why the bench pods had no readings is still unknown — the LED and the USB
serial on the new firmware will tell which step fails.

---

## 9. PCB Specification

**File:** `Tesis_PCB` (EasyEDA Pro) · **Size:** 40 × 26.5 mm · 1 mm rounded corners

| Layer | Components |
|---|---|
| Top | XIAO nRF52840, J1 (battery), J2 (switch) |
| Bottom | DRV2605L, GY-BNO085 |

- Signal tracks 0.254 mm; power 0.4 mm
- Via 0.61 mm outer / 0.305 mm drill; tenting custom −25.4 mm
- Clearance 0.152 mm
- **No GND pour** — deliberate; at I2C speeds the benefit doesn't outweigh the
  antenna risk

### Custom footprints (`pod_tesis_lib`)

`XIAO-nRF52840-DIP` (SMD castellated, pads 1–14 plus rear 15–20) ·
`BNO085_UNIT` (10 pads, 2.54 mm, pad 1 = VCC) ·
`DRV2605L_UNIT` (5 pads, 2.54 mm, pad 1 = VIN) ·
`BAT_PADS` (2 × 2.5 mm pads, 4 mm apart — used for J1 and J2)

### JLCPCB order

Flex · 25 µm dielectric · 2 layers · 0.11 mm · ENIG 1 U" · 1/3 oz outer copper ·
coverlay PI 12.5 / AD 15 µm yellow · no stiffener · no EMI film · laser cutting ·
flying probe fully test · remove mark · **confirm production file = yes**

---

## 10. Pod Housing

- 52.43 × 29.3 × 12.5 mm; width set by the side LRA compartment
- TPU body; the LRA compartment's bottom cap is rigid PLA/PETG, printed
  separately and swappable
- **The LRA bonds face-down to the rigid cap.** It must contact a rigid surface
  that contacts skin — rigid transmits far better than a TPU float, which
  absorbs vibration
- LRA compartment at the opposite end from the IMU
- Switch mounted through the TPU wall, wired to J1/J2
- CAD: `Pod_Tesis` (Fusion 360)

### Mounting

**Walker Tape, direct to skin — no straps.** Chosen for IMU fidelity: adhesive
removes the strap slop and soft-tissue movement that degrade orientation data.
Tested on a wrist.

Open consideration: the adhesive sits in the vibration path. If transmission
feels weak, cut the tape with a hole so the rigid LRA cap touches skin directly
and the tape grips only the surrounding TPU.

---

## 11. BLE Architecture

```
9 pods ──BLE──> receiver XIAO ──USB serial──> bridge.py ──UDP──> Unity ──Normcore──> 2 Quests
```

**Stack: Adafruit Bluefruit** (via the classic Seeed nRF52 core).
`BLE_MAX_CONNECTION = 20`.

ArduinoBLE (mbed core) was used through bench testing but **holds only one
central connection**, so the receiver was ported to Bluefruit. The pod
peripheral role works on either; both now use Bluefruit.

### Verified

**9/9 pods connected simultaneously, zero disconnects, 6+ minutes.** Each pod
streams ~11 Hz (one at 20 Hz), which is nine connections sharing one radio.
That rate is sufficient — IMU rate is not the latency path.

Commercial alternatives considered: SlimeVR uses Wi-Fi/UDP; Xsens and Rokoko
use proprietary 2.4 GHz to a dongle. BLE connections are unusual for multi-node
body tracking, which is why this needed proving.

### Protocol

**Serial, 115200** — `P,<id>,<qi>,<qj>,<qk>,<qr>` per sample;
`S,<connected>,<target>,<ids...>` per second; `R,...` for events.
`P,<id>,0,0,0,0` = connected, no IMU reading (see §8). The quaternion is the
**game rotation vector** (no magnetometer): heading is relative to the pod's
power-up orientation.

**Commands** — `C,<id>,<cue>,<param>`, **id 0 = broadcast**.

| Cue | Name | Param | For |
|---|---|---|---|
| 0 | STOP | — | ends a PULSE |
| 1 | CLICK | — | orientation, metronome |
| 2 | PULSE | ms ÷ 10 | proximity |
| 3 | TRIPLE | — | camera start |
| 4 | LONG | ms ÷ 10 | cut |
| 5 | DOUBLE | — | confirmations |

**Patterns execute on the pod**, not the server: one BLE write plays a whole
pattern, so timing survives radio contention and costs one packet instead of
three.

### Receiver placement

On a USB extension cable, head height or above, within 2–3 m of the actors. A
body between pod and receiver costs ~10 dB; from above it obstructs far less.

---

## 12. Haptic Cue Vocabulary

### MVP-5 (the demo)

| # | Cue | Where | Pattern |
|---|---|---|---|
| 1 | Proximity | waist only | pulse frequency ∝ 1/distance |
| 2 | Orientation | one wrist | directional pulse, no vertical |
| 3 | Timing | calves | alternating L-R metronome |
| 4 | Camera start | all 9 | 3 short spaced pulses |
| 5 | Cut | all 9 | 1 long low pulse |

### Backup-8 (implemented, not demoed)

Initial mark confirmation · checkpoint confirmation · virtual object contact ·
anticipatory alert (waist) · segment speed deviation · hold position · danger
zone · multi-actor proximity (excluded)

### Experimental-5 (future work, NOT built)

Laban jerk · breathing pacing · multi-actor sync · live director notes ·
virtual surface texture

---

## 13. Open Items

- [ ] **Quaternion coordinate conversion** — BNO085 right-handed vs Unity
      left-handed Y-up. Must be determined empirically by watching an object
      follow a pod. Four candidates selectable in `PodReceiver`.
- [ ] **Pod-to-limb mapping** — serializable, inspector-editable
- [ ] **End-to-end latency** — Unity cue → vibration on skin, never measured,
      target under ~100 ms
- [ ] Walker Tape under sustained vibration + sweat, full session
- [ ] The five MVP cues driven by scene state
- [ ] Rehearsal workflow: scene selection, take approval
- [ ] Battery runtime on an assembled pod

---

## 14. Software

| File | Role |
|---|---|
| `Firmware/pod_v2/` | Pod firmware. Bluefruit. Quaternion notify + cue write. |
| `Firmware/receiver_v2/` | Receiver. Multi-central, serial protocol. |
| `Bridge/bridge.py` | Serial ↔ UDP. Needs `pip install pyserial`. Logs CSV. |
| `Assets/Scripts/Haptics/PodReceiver.cs` | Unity UDP listener + cue API. |

Run the bridge from `Bridge/` so logs land there. Close the Arduino Serial
Monitor first — it holds the COM port.
