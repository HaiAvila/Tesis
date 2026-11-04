// 17 - POD firmware v4. ArduinoBLE on the MBED core.
//
// BOARD: Seeed nRF52 mbed-enabled Boards -> Seeed XIAO nRF52840 Sense
//        (NOT the classic "Seeed nRF52 Boards" core - its Wire
//         implementation does not work with the BNO085. Verified: a plain
//         IMU sketch with no BLE and no DRV prints nothing on that core.)
//
// The RECEIVER stays on the classic core with Bluefruit, because ArduinoBLE
// cannot hold more than one central connection. Pods are peripherals, which
// ArduinoBLE does fine. The two stacks interoperate.
//
// v4 = v3 + battery voltage + charge status in the quaternion notify,
//      + cues 6 DONE and 7 FAIL. Everything else unchanged.
//
// Cue packet = 2 bytes: [cueId, param]
//   0 STOP   1 CLICK   2 PULSE(param=ms/10)   3 TRIPLE
//   4 LONG(param=ms/10, 0=500ms)              5 DOUBLE
//   6 DONE  (calibration complete / all good: tick-click-buzz, rising)
//   7 FAIL  (this pod failed: two heavy pulsing buzzes)
//
// Quaternion notify = 19 bytes every 50 ms:
//   0-15  four floats i, j, k, real (all four zero = no IMU reading)
//   16-17 VBAT in mV, uint16 little-endian (last 5 s reading; 0 until the first)
//   18    flags: bit0 = charging (P0.17 LOW), bit1 = battery reading valid
// The receiver also still accepts the old 16-byte packet (v3 pods).
//
// LED: slow blink = IMU readings arriving, fast blink = BLE up, no IMU.
//
// ---- BATTERY PIN RULES (no spare pods - see pod-reference.md §7) ----------
// - P0.14 (READ_BAT): OUTPUT LOW, set first thing in setup(), NEVER HIGH,
//   never back to input. HIGH puts P0.31 at 3.60 V (abs. max) with a full battery.
// - P0.31 (AIN7): read only, by the SAADC. VBAT = V(P0.31) x 1510/510.
// - P0.17 (~CHG): INPUT with pull-up only, never an output.
// - P0.13 (charge current): NEVER touched. 50 mA default on purpose.
// The pins are driven through the nRF52840 registers directly (NRF_P0, NRF_SAADC),
// not through the core's pin numbers, so there is no Arduino-pin-number mapping
// that could point at the wrong pin.

#define POD_ID  2

#include <ArduinoBLE.h>
#include <Wire.h>
#include <Adafruit_BNO08x.h>
#include <Adafruit_DRV2605.h>
#include "nrf.h"

#define BNO_RST D2

#define PKT_LEN 19

BLEService        podService("19b10000-e8f2-537e-4f6c-d104768a1214");
BLECharacteristic quatChar("19b10001-e8f2-537e-4f6c-d104768a1214",
                           BLERead | BLENotify, PKT_LEN);
BLECharacteristic cueChar ("19b10002-e8f2-537e-4f6c-d104768a1214",
                           BLEWrite | BLEWriteWithoutResponse, 2);

Adafruit_BNO08x   bno(BNO_RST);
Adafruit_DRV2605  drv;
sh2_SensorValue_t v;

float quat[4] = {0, 0, 0, 0};          // all zero = no reading yet
unsigned long lastNotify = 0, lastBlink = 0;
bool ledState = false, imuOK = false, drvOK = false;
char podName[8];

#define IMU_RETRY_MS 2000
#define IMU_QUIET_MS 1000
#define IMU_DEAD_MS  5000
unsigned long lastImuEvent = 0, lastImuRetry = 0, lastReportEnable = 0;

// --- battery --------------------------------------------------------------
#define PIN_READ_BAT  14      // P0.14, divider low side. OUTPUT LOW forever.
#define PIN_CHG       17      // P0.17, BQ25101 ~CHG, open drain. INPUT_PULLUP only.
#define BAT_PERIOD_MS 5000
#define BAT_SAMPLES   8

uint16_t batMv = 0;
bool     batValid = false;
unsigned long lastBat = 0;

// P0.14 low. OUT first, then direction, so the pin is never driven high even for
// an instant.
void holdReadBatLow() {
  NRF_P0->OUTCLR = (1UL << PIN_READ_BAT);
  NRF_P0->DIRSET = (1UL << PIN_READ_BAT);
}

void setupChargePin() {
  NRF_P0->PIN_CNF[PIN_CHG] =
      (GPIO_PIN_CNF_DIR_Input      << GPIO_PIN_CNF_DIR_Pos)   |
      (GPIO_PIN_CNF_INPUT_Connect  << GPIO_PIN_CNF_INPUT_Pos) |
      (GPIO_PIN_CNF_PULL_Pullup    << GPIO_PIN_CNF_PULL_Pos)  |
      (GPIO_PIN_CNF_DRIVE_S0S1     << GPIO_PIN_CNF_DRIVE_Pos) |
      (GPIO_PIN_CNF_SENSE_Disabled << GPIO_PIN_CNF_SENSE_Pos);
}

bool isCharging() { return (NRF_P0->IN & (1UL << PIN_CHG)) == 0; }   // LOW = charging

// SAADC on AIN7 (= P0.31): internal 0.6 V reference, gain 1/6 -> 0-3.6 V range,
// 12 bit. P0.31 is at most 1.42 V (4.2 V battery). TACQ 40 us: the divider's
// source resistance is 1 M || 510 k = 338 k, under the 800 k that 40 us allows.
static volatile int16_t saadcResult;

void saadcConfigure() {
  NRF_SAADC->RESOLUTION = SAADC_RESOLUTION_VAL_12bit;
  NRF_SAADC->OVERSAMPLE = SAADC_OVERSAMPLE_OVERSAMPLE_Bypass;
  NRF_SAADC->CH[0].CONFIG =
      (SAADC_CH_CONFIG_RESP_Bypass   << SAADC_CH_CONFIG_RESP_Pos)   |
      (SAADC_CH_CONFIG_RESN_Bypass   << SAADC_CH_CONFIG_RESN_Pos)   |
      (SAADC_CH_CONFIG_GAIN_Gain1_6  << SAADC_CH_CONFIG_GAIN_Pos)   |
      (SAADC_CH_CONFIG_REFSEL_Internal << SAADC_CH_CONFIG_REFSEL_Pos) |
      (SAADC_CH_CONFIG_TACQ_40us     << SAADC_CH_CONFIG_TACQ_Pos)   |
      (SAADC_CH_CONFIG_MODE_SE       << SAADC_CH_CONFIG_MODE_Pos)   |
      (SAADC_CH_CONFIG_BURST_Disabled << SAADC_CH_CONFIG_BURST_Pos);
  NRF_SAADC->CH[0].PSELN = SAADC_CH_PSELN_PSELN_NC;
  NRF_SAADC->CH[0].PSELP = SAADC_CH_PSELP_PSELP_AnalogInput7;
}

void saadcRelease() {
  NRF_SAADC->CH[0].PSELP = SAADC_CH_PSELP_PSELP_NC;
  NRF_SAADC->ENABLE = SAADC_ENABLE_ENABLE_Disabled;
}

// Waits on an event with a timeout, so a misbehaving SAADC can never hang the pod.
bool waitEvent(volatile uint32_t& ev) {
  unsigned long t0 = micros();
  while (!ev) if (micros() - t0 > 2000) return false;
  ev = 0;
  return true;
}

void saadcCalibrate() {
  saadcConfigure();
  NRF_SAADC->ENABLE = SAADC_ENABLE_ENABLE_Enabled;
  NRF_SAADC->EVENTS_CALIBRATEDONE = 0;
  NRF_SAADC->TASKS_CALIBRATEOFFSET = 1;
  unsigned long t0 = millis();
  while (!NRF_SAADC->EVENTS_CALIBRATEDONE && millis() - t0 < 20) {}
  NRF_SAADC->EVENTS_CALIBRATEDONE = 0;
  saadcRelease();
}

// One conversion, raw 12-bit count, or -1 on a timeout.
int saadcSample() {
  NRF_SAADC->RESULT.PTR    = (uint32_t)&saadcResult;
  NRF_SAADC->RESULT.MAXCNT = 1;
  NRF_SAADC->EVENTS_STARTED = 0;
  NRF_SAADC->EVENTS_END     = 0;
  NRF_SAADC->EVENTS_STOPPED = 0;
  NRF_SAADC->TASKS_START = 1;
  if (!waitEvent(NRF_SAADC->EVENTS_STARTED)) return -1;
  NRF_SAADC->TASKS_SAMPLE = 1;
  bool ok = waitEvent(NRF_SAADC->EVENTS_END);
  NRF_SAADC->TASKS_STOP = 1;
  waitEvent(NRF_SAADC->EVENTS_STOPPED);
  if (!ok) return -1;
  int r = saadcResult;
  return r < 0 ? 0 : r;            // single-ended can read slightly negative near 0
}

// Called from loop() only. ~8 x 60 us: never delays the 50 ms notify.
void serviceBattery() {
  unsigned long now = millis();
  if (lastBat != 0 && now - lastBat < BAT_PERIOD_MS) return;
  lastBat = now;

  holdReadBatLow();                // belt and braces: P0.14 stays LOW
  saadcConfigure();
  NRF_SAADC->ENABLE = SAADC_ENABLE_ENABLE_Enabled;

  long sum = 0; int n = 0;
  for (int k = 0; k < BAT_SAMPLES; k++) {
    int r = saadcSample();
    if (r >= 0) { sum += r; n++; }
  }
  saadcRelease();
  if (n == 0) return;              // keep the last good reading

  // pin mV = count * 3600 / 4096 ; VBAT = pin * 1510 / 510
  float pinMv = (sum / (float)n) * 3600.0f / 4096.0f;
  float vbat  = pinMv * 1510.0f / 510.0f;
  if (vbat < 0) vbat = 0;
  if (vbat > 65535) vbat = 65535;
  batMv = (uint16_t)(vbat + 0.5f);
  batValid = true;
}

// --- cue engine -----------------------------------------------------------
enum { CUE_STOP = 0, CUE_CLICK, CUE_PULSE, CUE_TRIPLE, CUE_LONG, CUE_DOUBLE,
       CUE_DONE, CUE_FAIL };

volatile uint8_t pendingCue = 255, pendingParam = 0;
uint8_t  curCue = CUE_STOP, curParam = 0, cueStep = 0;
unsigned long cueT = 0;

void fireEffect(uint8_t e) {
  if (!drvOK) return;
  drv.setWaveform(0, e);
  drv.setWaveform(1, 0);
  drv.go();
}

// Plays a whole sequence inside the DRV2605L (its 8-slot sequencer), so it costs
// one I2C burst and nothing in loop(). A slot with bit 7 set is a wait of
// (value & 0x7F) x 10 ms.
void fireSequence(const uint8_t* seq, uint8_t n) {
  if (!drvOK) return;
  uint8_t i = 0;
  for (; i < n && i < 7; i++) drv.setWaveform(i, seq[i]);
  drv.setWaveform(i, 0);           // end of sequence
  drv.go();
}

#define WAIT_MS(ms) (uint8_t)(0x80 | ((ms) / 10))

// DONE: sharp tick, strong click, strong buzz - rising, ~0.5-0.7 s.
const uint8_t SEQ_DONE[] = { 24, WAIT_MS(80), 1, WAIT_MS(80), 14 };
// FAIL: two "pulsing strong" buzzes - heavier and stuttering, ~0.8 s,
// unlike TRIPLE / DOUBLE (short clicks) and LONG (one smooth buzz).
const uint8_t SEQ_FAIL[] = { 52, WAIT_MS(100), 52 };

void stopEffect() { if (drvOK) drv.stop(); }

void startCue(uint8_t id, uint8_t param) {
  curCue = id; curParam = param; cueStep = 0; cueT = millis();
  switch (id) {
    case CUE_STOP:   stopEffect(); break;
    case CUE_CLICK:  fireEffect(1); curCue = CUE_STOP; break;
    case CUE_PULSE:  fireEffect(1); break;
    case CUE_TRIPLE: fireEffect(1); cueStep = 1; break;
    case CUE_LONG:   fireEffect(118); break;
    case CUE_DOUBLE: fireEffect(1); cueStep = 1; break;
    case CUE_DONE:   fireSequence(SEQ_DONE, sizeof(SEQ_DONE)); curCue = CUE_STOP; break;
    case CUE_FAIL:   fireSequence(SEQ_FAIL, sizeof(SEQ_FAIL)); curCue = CUE_STOP; break;
    default:         curCue = CUE_STOP; break;     // unknown cue: ignore
  }
}

void serviceCue() {
  unsigned long now = millis();
  switch (curCue) {
    case CUE_PULSE: {
      unsigned long gap = curParam ? curParam * 10UL : 500;
      if (now - cueT >= gap) { cueT = now; fireEffect(1); }
      break;
    }
    case CUE_TRIPLE:
      if (now - cueT >= 150) {
        cueT = now;
        if (cueStep < 3) { fireEffect(1); cueStep++; } else curCue = CUE_STOP;
      }
      break;
    case CUE_LONG: {
      unsigned long dur = curParam ? curParam * 10UL : 500;
      if (now - cueT >= dur) { stopEffect(); curCue = CUE_STOP; }
      break;
    }
    case CUE_DOUBLE:
      if (now - cueT >= 100) {
        cueT = now;
        if (cueStep < 2) { fireEffect(1); cueStep++; } else curCue = CUE_STOP;
      }
      break;
  }
}

void onCueWritten(BLEDevice central, BLECharacteristic chr) {
  (void)central;
  if (chr.valueLength() < 1) return;
  const uint8_t* d = chr.value();
  pendingCue   = d[0];
  pendingParam = (chr.valueLength() > 1) ? d[1] : 0;
}

// --- IMU ------------------------------------------------------------------
void setReports() {
  lastReportEnable = millis();
  if (!bno.enableReport(SH2_GAME_ROTATION_VECTOR, 20000))
    Serial.println("IMU enableReport failed");
}

bool startIMU(int attempts) {
  pinMode(BNO_RST, OUTPUT);
  digitalWrite(BNO_RST, LOW);  delay(10);
  digitalWrite(BNO_RST, HIGH); delay(300);
  for (int t = 0; t < attempts; t++) {
    if (bno.begin_I2C(0x4B)) {
      setReports();
      lastImuEvent = millis();
      return true;
    }
    if (t + 1 < attempts) delay(200);
  }
  return false;
}

void serviceIMU() {
  unsigned long now = millis();

  if (!imuOK) {
    if (now - lastImuRetry >= IMU_RETRY_MS) {
      lastImuRetry = now;
      imuOK = startIMU(1);
      Serial.println(imuOK ? "IMU OK (retry)" : "IMU retry failed");
    }
    return;
  }

  if (bno.wasReset()) { Serial.println("IMU reset"); setReports(); }

  if (bno.getSensorEvent(&v) && v.sensorId == SH2_GAME_ROTATION_VECTOR) {
    quat[0] = v.un.gameRotationVector.i;
    quat[1] = v.un.gameRotationVector.j;
    quat[2] = v.un.gameRotationVector.k;
    quat[3] = v.un.gameRotationVector.real;
    lastImuEvent = now;
    return;
  }

  if (now - lastImuEvent >= IMU_DEAD_MS) {
    Serial.println("IMU silent 5 s, restarting");
    quat[0] = quat[1] = quat[2] = quat[3] = 0;
    imuOK = false;
    lastImuRetry = 0;
  } else if (now - lastImuEvent >= IMU_QUIET_MS &&
             now - lastReportEnable >= IMU_QUIET_MS) {
    Serial.println("IMU quiet, re-enabling report");
    setReports();
  }
}

bool imuHealthy() { return imuOK && millis() - lastImuEvent < IMU_QUIET_MS; }

// --- notify packet ----------------------------------------------------------
void sendPacket() {
  uint8_t pkt[PKT_LEN];
  memcpy(pkt, quat, 16);
  uint16_t mv = batValid ? batMv : 0;
  pkt[16] = (uint8_t)(mv & 0xFF);
  pkt[17] = (uint8_t)(mv >> 8);
  pkt[18] = (isCharging() ? 0x01 : 0x00) | (batValid ? 0x02 : 0x00);
  quatChar.writeValue(pkt, PKT_LEN);
}

// --- setup ----------------------------------------------------------------
void setup() {
  holdReadBatLow();                // FIRST: P0.14 OUTPUT LOW, never HIGH
  setupChargePin();                // P0.17 input + pull-up, never an output

  Serial.begin(115200);
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, HIGH);

  delay(500);
  Wire.begin();
  sprintf(podName, "POD_%02d", POD_ID);

  imuOK = startIMU(10);
  lastImuRetry = millis();
  Serial.println(imuOK ? "IMU OK" : "IMU FAILED (retrying every 2 s)");

  drvOK = drv.begin();
  if (drvOK) {
    drv.selectLibrary(6);
    drv.useLRA();
    drv.setMode(DRV2605_MODE_INTTRIG);
  } else {
    Serial.println("DRV FAILED");
  }

  saadcCalibrate();
  serviceBattery();                // first reading before advertising
  Serial.print("VBAT mV "); Serial.print(batMv);
  Serial.print(isCharging() ? "  charging" : "  not charging");
  Serial.println();

  if (!BLE.begin()) { Serial.println("BLE FAILED"); while (1) delay(10); }

  BLE.setLocalName(podName);
  BLE.setDeviceName(podName);          // the receiver reads the name over GATT
  BLE.setAdvertisedService(podService);

  cueChar.setEventHandler(BLEWritten, onCueWritten);

  podService.addCharacteristic(quatChar);
  podService.addCharacteristic(cueChar);
  BLE.addService(podService);
  BLE.advertise();

  Serial.print(podName); Serial.println(" advertising");
}

void loop() {
  BLE.poll();

  unsigned long period = imuHealthy() ? 1000 : 150;
  if (millis() - lastBlink >= period) {
    lastBlink = millis();
    ledState = !ledState;
    digitalWrite(LED_BUILTIN, ledState ? LOW : HIGH);
  }

  if (pendingCue != 255) {
    uint8_t c = pendingCue, p = pendingParam;
    pendingCue = 255;
    startCue(c, p);
  }
  serviceCue();
  serviceIMU();
  serviceBattery();

  if (millis() - lastNotify >= 50) {
    lastNotify = millis();
    sendPacket();
  }
}
