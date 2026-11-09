# Haptic Pod — Build & Connection Reference

VR rehearsal system, thesis project.
Last updated: 2026-11-04

---

## 1. Bill of Materials (per pod)

| Component | Part | Notes |
|---|---|---|
| MCU | Seeed XIAO nRF52840 (non-Sense) | BLE, USB-C retained |
| Haptic driver | DRV2605L — UNIT Electronics DevLab (UE0065) | 5 pads, power LED on VDD only |
| IMU | GY-BNO085 — UNIT Electronics | 10 pads |
| Actuator | Vybronics VG0832013D LRA | Same on all 9 bands |
| Battery | LiPo 3.7 V / **140 mAh** | Bare +/− wires, no JST |
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

**The DRV was never tuned to this motor until v5.** `useLRA()` only sets the motor-type
bit; the rest stayed at the chip's defaults (rated ~1.6 V rms, drive time for ~208 Hz,
uncalibrated back-EMF), so steps felt weak on a moving shin (Nov 9). **v5** (`calibrateDRV`)
writes, before the chip's auto-calibration at every power-up (~1.2 s of buzzing):
RATED_VOLTAGE 70 (= 1.8 V rms closed loop at 235 Hz, datasheet Eq. 5), DRIVE_TIME 16
(2.1 ms, half of 235 Hz; Table 24), OD_CLAMP left at the default 0x8C (~3 V peak), FEEDBACK
LRA with default brake/loop/back-EMF gains, AUTO_CAL_TIME 3. The serial port prints
"DRV calibrated: comp=… bemf=… feedback=…" or "DRV calibration FAILED". First flashed on
pod 7 only, to compare with pod 8 (v4) using the same cue (`7 4 20` vs `8 4 20`).

**Effect 118 does NOT run until stopped.** Despite being named "long buzz for
programmatic stopping," it self-terminates after roughly 10 s. Verified on
hardware. Sustained cues must either re-trigger or use real-time playback (RTP)
mode, where you write an amplitude register directly.

**Board selection: pods and receiver use different cores** (since 2026-11-02):

| Firmware | Core (Tools → Board group) | Board | BLE stack |
|---|---|---|---|
| `pod_v2` (v4) | **Seeed nRF52 mbed-enabled Boards** | Seeed XIAO nRF52840 Sense | ArduinoBLE, peripheral |
| `receiver_v2` | **Seeed nRF52 Boards** (classic) | Seeed XIAO nRF52840 | Adafruit Bluefruit, multi-central |

- **Pods on mbed:** the classic core's `Wire` does not work with the BNO085.
  Verified: a plain IMU sketch, no BLE and no DRV, prints nothing on the
  classic core. Every pod sample used to be `0,0,0,1` because of this.
- **Receiver on classic:** ArduinoBLE holds only one central connection, so it
  can't take 9 pods; Bluefruit can.
- The two stacks interoperate.

Arduino IDE 2.x remembers the board **per window**, and both cores list a board
called "Seeed XIAO nRF52840 Sense" — always select from the **Tools → Board**
menu, where the core group name is visible, and check it before every upload.

**J2 has no negative pad.** J1 is LiPo + / −. **Both J2 pads are positive** —
pad 1 from the switch, pad 2 back to the XIAO's BAT+. Only a "+" is in
silkscreen. Wiring the battery to J2 pad 2 bypasses the switch entirely.

**3.3 V only on the IMU.** No onboard regulator.

**Python is required on Windows** for the classic core (the receiver) to compile — it builds
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

On ~120 mAh usable (of 140 mAh): ~6 h idle, ~2.4 h clicks, ~1.3 h continuous, **~4.3 h
mixed**.

**No decoupling capacitors — confirmed unnecessary.** The earlier reset during
the buzz phase was caused by a multimeter in series, not by the board. Retested
on battery with no meter: the rail held through continuous 90 mA with no reset
and no dropped BLE link.

If a problem ever reappears, the fallback needs no PCB change: bridge an 0805
10 µF directly across the DRV module's VIN and GND pins, which are 2.54 mm
apart.

### Battery reading and charging pins — verified, 2026-11-04

Checked against Seeed's own KiCad schematic for the plain XIAO nRF52840
(`260828_Seeed_Studio_XIAO_nRF52840_v1.2`, sheet "02 XIAO Header & nRF52840")
and Nordic's nRF52840 Product Specification v1.1. **No spare pods: follow
these rules exactly.**

**Chip limits (Nordic, Table 173 and §6.23):** no pin above **VDD + 0.3 V =
3.6 V** (VDD is 3.3 V on the XIAO); analog inputs AIN0–AIN7 never above
**VDD = 3.3 V**. A LiPo is 4.2 V full or charging. After reset every pin is a
disconnected input, no pull (`PIN_CNF` reset `0x00000002`).

**The divider (schematic):**

```
VBAT ──[R16 1 MΩ 1%]──┬──[R17 510 kΩ 1%]── P0.14 (READ_BAT, active low, straight to the pin)
                      └── P0.31 (AIN7_BAT)     no capacitor, no transistor
```

P0.31 = VBAT × 510/1510 = **0.338 × VBAT**. (C30, 2.2 µF, is VBAT's filter
capacitor to GND, not on P0.31.)

| P0.14 | P0.31 at VBAT = 4.2 V | |
|---|---|---|
| OUTPUT LOW | **1.42 V** (P0.14 at 0 V); divider draws 2.8 µA | ✅ safe |
| OUTPUT HIGH | 3.3 + 0.9 × 0.338 = **3.60 V** | ❌ at the absolute maximum when full/charging — Seeed's own warning ("risk of burning out the P0.31 pin") |
| floating (input — how firmware v3 ran; **v4 holds it LOW**) | nothing pulls the divider down: P0.31 and P0.14 drift toward VBAT until the pins' protection diodes clamp them a little above 3.3 V, < 1 µA through 1.5 MΩ | ⚠️ slightly outside spec; Seeed doesn't warn about it, but holding P0.14 LOW fixes it |

**Built in pod firmware v4 (2026-11-04), flashed on all 9.** The pins are driven through the nRF52840's registers (`NRF_P0`, `NRF_SAADC`), not the core's pin numbers, so no Arduino pin mapping can point at the wrong pin. Read every 5 s, 8 samples averaged, sent in every notify.

**Rules for reading the battery:**
- At the very start of `setup()`: P0.14 as **OUTPUT, LOW**. Never set it HIGH
  anywhere, never switch it back to an input.
- Read P0.31 as an analog input. With the nRF52840's internal 0.6 V reference
  and gain 1/6 the ADC range is 0–3.6 V; 1.42 V max is well inside.
  VBAT = V(P0.31) × 1510/510 = V(P0.31) × 2.961.
- **Never touch P0.13** (`P0.13_HICHG`, the charger's current setting). Left
  alone (high impedance) the charger runs at the 50 mA default; Seeed's pages
  contradict each other on which level means what, so don't drive it at all.

**Charging status, P0.17 (`P0.17_~{CHG}`)** — safe to read: it's the charger's
(BQ25101) open-drain CHG output, joined to the red charge LED, which runs from
**3V3** through R5 (2.2 kΩ). So P0.17 never sees more than 3.3 V. LOW =
charging (the red LED is on), HIGH = full or not charging. Read it as
**INPUT** (or INPUT_PULLUP); **never as an output** — driving it LOW would light
the LED, driving it HIGH would fight the charger while it pulls low.

Charge time at the 50 mA default (140 mAh): ~2–2½ h to ~90 %, ~3 h to full;
the pod's switch must be ON (it sits between the battery and the XIAO).
**100 mA charging deliberately not used** (no spare pods; 50 mA ≈ 0.35C is gentler).
Re-flash procedure for the battery firmware: flash and soak-test **one pod**
(~30 min, charge/unplug, readings ~4.2 V full, never > 4.3 V) before the other 8;
keep `pod_v2.ino` to roll back; double-tap reset enters the bootloader.
Charge on a non-flammable surface; never charge a swollen or hot pod.

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

**Cause found the same day:** the classic core's `Wire` doesn't work with the
BNO085. The pods moved to the mbed core with ArduinoBLE (firmware v3, see §3
and §11), keeping the watchdog above; with it, both bench pods' orientation
reached Unity.

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

### Body placement (final, 2026-11-03)

| ID | Placement | Bone | USB faces |
|---|---|---|---|
| 1 | Left wrist | LeftLowerArm | hand |
| 2 | Right wrist | RightLowerArm | hand |
| 3 | Left bicep | LeftUpperArm | hand |
| 4 | Right bicep | RightUpperArm | hand |
| 5 | Left quad | LeftUpperLeg | knee |
| 6 | Right quad | RightUpperLeg | knee |
| 7 | Left shin | LeftLowerLeg | foot |
| 8 | Right shin | RightLowerLeg | foot |
| 9 | Sternum | Chest | neck |

The sternum pod replaces the waist pod; no hip pod (hips follow pod 9 yaw +
headset position). Source of truth: `Docs/pod-map.md`.

Open consideration: the adhesive sits in the vibration path. If transmission
feels weak, cut the tape with a hole so the rigid LRA cap touches skin directly
and the tape grips only the surrounding TPU.

---

## 11. BLE Architecture

```
9 pods ──BLE──> receiver XIAO ──USB serial──> bridge.py ──UDP──> Unity ──Normcore──> 2 Quests
```

**Receiver: Adafruit Bluefruit** (via the classic Seeed nRF52 core),
`BLE_MAX_CONNECTION = 20`. **Pods: ArduinoBLE** (via the mbed core), as
peripherals.

ArduinoBLE **holds only one central connection**, so the receiver was ported to
Bluefruit. The pods were on Bluefruit too until 2026-11-02, when they went back
to ArduinoBLE: on the classic core the BNO085 never answered (§3, §8). Same
service and characteristic UUIDs, same 16-byte quaternion notify and 2-byte
cue write, so the receiver didn't change.

### Verified

**9/9 pods connected simultaneously, zero disconnects, 6+ minutes.** Each pod
streams ~11 Hz (one at 20 Hz), which is nine connections sharing one radio.
That rate is sufficient — IMU rate is not the latency path.

**Measured with Bluefruit pods.** **Repeated with 9 v3 pods (ArduinoBLE),
2026-11-03:** ~10 minutes, every pod at **17–19 samples/s** (faster than the
~11 Hz with Bluefruit pods), zero `0,0,0,0` (no-IMU) samples. Pods 3 and 4 had
dropouts of 7–19 s (low battery, per the user). Twice in those 10 minutes
**every pod paused at the same instant** (1.4 s and ~2.2 s).

**Diagnosed 2026-11-04 — the receiver, while a pod reconnects.** In the log, all
8 working pods stopped within 0.1 s and resumed at the very same instant, and
the low-battery pod 3 came back 0.2 s after them; no backlog burst followed
(so it wasn't the PC holding buffered lines — the samples were never sent). In
Bluefruit, `connect_cb` runs on the shared callback queue (`ada_callback`,
`bluefruit.cpp`), and so did every pod's notify callback (the default of
`setNotifyCallback`). `connect_cb` blocks ~1–2 s in `discover()` /
`enableNotify()` for the reconnecting pod, and every other pod's samples
waited behind it. It only happens when a pod (re)connects mid-session — which
is why it showed up only with low batteries. **Fix (receiver_v2, needs
re-flashing the receiver only):** `setNotifyCallback(notify_cb, false)` runs the
notify callback directly from the BLE task; the 16-byte copy is guarded with
`taskENTER_CRITICAL` on both sides so the main loop never prints a half-updated
sample. `bridge.py` now also writes the receiver's `R`/`S`/`E` lines to a
`*_events.csv` next to the samples, on the same clock, to check it.

**Verified on hardware, 2026-11-04** (9 pods, ~5 min; pods 1 and 9 switched
off and back on, pod 2 ran flat): while pod 1 (221.6 s) and pod 9 (246.5 s)
reconnected, every other pod's longest gap was 0.11–0.23 s — normal jitter —
and no moment had 3+ pods pausing together. **But the fix exposed a race:**
notifications now arrive before `connect_cb` finished, while it still assigned
the slot's pod ID last, so a connecting pod's first sample went out with the
slot's previous ID — `P,0,…` at startup (1–2 per pod), and pod 9's first
sample as `P,2,…` when it reconnected into pod 2's freed slot. **Fixed:** the
ID is set before `enableNotify()`, and the main loop prints only slots whose
`slotUsed` is set (end of `connect_cb`); `disconnect_cb` clears `dirty`.
**Verified on hardware, 2026-11-04 (second run, 7½ min):** no `P,0` lines, no
sample under a pod's ID while it was off, and during three reconnects (pods
1, 9, 5) every other pod's longest gap was 0.09–0.19 s; no shared pauses;
18–19.5 samples/s per pod.

**Status line, for reading it by eye:** `S,8,9,1,3,4,5,6,7,8,9` = 8 connected,
target 9, then the IDs — so with pod 9 connected "9" appears twice; that's not
a duplicate.

Commercial alternatives considered: SlimeVR uses Wi-Fi/UDP; Xsens and Rokoko
use proprietary 2.4 GHz to a dongle. BLE connections are unusual for multi-node
body tracking, which is why this needed proving.

### Protocol

**Serial, 115200** — `P,<id>,<qi>,<qj>,<qk>,<qr>` per sample;
`S,<connected>,<target>,<ids...>` per second; `R,...` for events;
`H,<id>,<battery_mV>,<charging>,<rssi>` every 2 s per connected pod (`battery_mV` 0 = none
sent, `charging` −1 = unknown: pod older than v4; `rssi` dBm, 0 = not available).
`P,<id>,0,0,0,0` = connected, no IMU reading (see §8). The quaternion is the
**game rotation vector** (no magnetometer): heading is relative to the pod's
power-up orientation.

**Pod notify (v4)** — 19 bytes every 50 ms: four floats i, j, k, real; VBAT mV, uint16
little-endian (last 5 s reading); flags, bit0 = charging, bit1 = reading valid. The receiver
still accepts v3's 16 bytes.

**Commands** — `C,<id>,<cue>,<param>`, **id 0 = broadcast**.

| Cue | Name | Param | For |
|---|---|---|---|
| 0 | STOP | — | ends a PULSE |
| 1 | CLICK | — | orientation, metronome |
| 2 | PULSE | ms ÷ 10 | proximity |
| 3 | TRIPLE | — | camera start |
| 4 | LONG | ms ÷ 10 | cut |
| 5 | DOUBLE | — | confirmations |
| 6 | DONE | — | calibrated OK: tick, click, strong buzz, rising |
| 7 | FAIL | — | this pod failed: two heavy pulsing buzzes |

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
| 1 | Proximity | sternum only (pod 9; was waist) | pulse frequency ∝ 1/distance |
| 2 | Orientation | one wrist | directional pulse, no vertical |
| 3 | Timing | shins (pods 7, 8) | alternating L-R metronome |
| 4 | Camera start | all 9 | 3 short spaced pulses |
| 5 | Cut | all 9 | 1 long low pulse |

### Backup-8 (implemented, not demoed)

Initial mark confirmation · checkpoint confirmation · virtual object contact ·
anticipatory alert (sternum, pod 9; was waist) · segment speed deviation · hold position · danger
zone · multi-actor proximity (excluded)

### Experimental-5 (future work, NOT built)

Laban jerk · breathing pacing · multi-actor sync · live director notes ·
virtual surface texture

---

## 13. Open Items

- [x] **Quaternion coordinate conversion** — settled 2026-11-03: conversion 1,
      no flips (see STATUS.md)
- [x] **Pod-to-limb mapping** — final, §10 / `Docs/pod-map.md`
- [x] Calibration + avatar on the desktop test scene (PodAvatarTest)
- [x] **Firmware batch** — battery voltage + charge status on pods, RSSI on the receiver,
      cues DONE and FAIL: v4, flashed on all 9 (2026-11-04). The optional 30 Hz notify was left out.
- [x] Avatar over Normcore (AvatarModel) — built; receiving on a second machine still to try
- [x] **End-to-end latency** — measured 2026-11-09 (Unity `LatencyTest` scene, microphone):
      median 55 ms, p90 93 ms, preliminary; to repeat with all 9 pods connected
- [ ] **Firmware v5** (DRV tuned + auto-calibrated at power-up): on pod 7 for an A/B
      against pod 8, then all 9 if it's stronger
- [ ] Walker Tape under sustained vibration + sweat, full session
- [ ] The five MVP cues driven by scene state
- [ ] Rehearsal workflow: scene selection, take approval
- [ ] Battery runtime on an assembled pod
- Future work: body proportions from iPhone video (MediaPipe); tape-measured for now

---

## 14. Software

| File | Role |
|---|---|
| `Firmware/pod_v2/` | Pod firmware v4. mbed core, ArduinoBLE peripheral. Quaternion + battery notify, cue write, IMU watchdog. |
| `Firmware/receiver_v2/` | Receiver. Classic core, Bluefruit multi-central, serial protocol. |
| `Bridge/bridge.py` | Serial ↔ UDP. Needs `pip install pyserial`. Logs CSV (samples; R, S, E and H lines in `*_events.csv`), prints each pod's battery and RSSI every 5 s. |
| `Assets/Scripts/Haptics/PodReceiver.cs` | Unity UDP listener + cue API. |

Run the bridge from `Bridge/` so logs land there. Close the Arduino Serial
Monitor first — it holds the COM port.
