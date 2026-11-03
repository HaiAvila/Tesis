// 14 - POD firmware v2. Bluefruit.
// Board: Seeed nRF52 Boards (classic) -> Seeed XIAO nRF52840
//
// Adds the return path: a WRITE characteristic the receiver uses to fire
// haptic cues. Patterns run locally on the pod, so a 3-pulse cue keeps its
// timing even if the radio is busy, and one BLE write costs one packet
// instead of three.
//
// Cue packet = 2 bytes: [cueId, param]
//
//   0 STOP      -               stop whatever is playing
//   1 CLICK     -               one strong click
//   2 PULSE     param = ms/10   repeat a click forever until STOP
//                               (proximity: lower param = faster)
//   3 TRIPLE    -               3 short pulses  (camera start)
//   4 LONG      param = ms/10   one long buzz   (cut; 0 = 500 ms)
//   5 DOUBLE    -               2 quick pulses  (confirmation)
//
// Quaternion notify = 4 floats (i, j, k, real), every 50 ms. All zeros = no IMU
// reading (the IMU didn't start, or its readings stopped): Unity shows "sin IMU"
// instead of mistaking it for a real orientation. It used to start at 0,0,0,1
// and stay there, which looked like a pod lying perfectly still.
//
// The IMU heals itself: if it fails at boot it's retried every 2 s; if it starts
// but goes quiet, the report is re-enabled after 1 s and the IMU restarted after
// 5 s. Each step prints on the pod's own USB serial (115200).
//
// LED: slow blink = IMU readings arriving, fast blink = BLE up but no IMU reading.

#define POD_ID  2

#include <bluefruit.h>
#include <Wire.h>
#include <Adafruit_BNO08x.h>
#include <Adafruit_DRV2605.h>

#define BNO_RST D2

// 19b10000-... service
const uint8_t SVC_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x00,0x00,0xb1,0x19
};
// 19b10001-... quaternion, notify
const uint8_t QUAT_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x01,0x00,0xb1,0x19
};
// 19b10002-... cue, write
const uint8_t CUE_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x02,0x00,0xb1,0x19
};

BLEService        podService(SVC_UUID);
BLECharacteristic quatChar(QUAT_UUID);
BLECharacteristic cueChar(CUE_UUID);

Adafruit_BNO08x   bno(BNO_RST);
Adafruit_DRV2605  drv;
sh2_SensorValue_t v;

float quat[4] = {0, 0, 0, 0};   // all zeros = no IMU reading yet
unsigned long lastNotify = 0, lastBlink = 0;
bool ledState = false, imuOK = false, drvOK = false;

// IMU watchdog (see the header).
#define IMU_RETRY_MS     2000   // IMU not started: try again this often
#define IMU_QUIET_MS     1000   // started, no reading this long: re-enable the report
#define IMU_DEAD_MS      5000   // still nothing: restart the IMU
unsigned long lastImuEvent = 0, lastImuRetry = 0, lastReportEnable = 0;
char podName[8];

// --- cue engine -----------------------------------------------------------
enum { CUE_STOP = 0, CUE_CLICK, CUE_PULSE, CUE_TRIPLE, CUE_LONG, CUE_DOUBLE };

volatile uint8_t pendingCue = 255, pendingParam = 0;
uint8_t  curCue = CUE_STOP, curParam = 0;
uint8_t  cueStep = 0;
unsigned long cueT = 0;

void fireEffect(uint8_t e) {
  if (!drvOK) return;
  drv.setWaveform(0, e);
  drv.setWaveform(1, 0);
  drv.go();
}

void stopEffect() { if (drvOK) drv.stop(); }

void startCue(uint8_t id, uint8_t param) {
  curCue   = id;
  curParam = param;
  cueStep  = 0;
  cueT     = millis();

  switch (id) {
    case CUE_STOP:   stopEffect(); break;
    case CUE_CLICK:  fireEffect(1); curCue = CUE_STOP; break;
    case CUE_PULSE:  fireEffect(1); break;
    case CUE_TRIPLE: fireEffect(1); cueStep = 1; break;
    case CUE_LONG:   fireEffect(118); break;
    case CUE_DOUBLE: fireEffect(1); cueStep = 1; break;
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
        if (cueStep < 3) { fireEffect(1); cueStep++; }
        else curCue = CUE_STOP;
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
        if (cueStep < 2) { fireEffect(1); cueStep++; }
        else curCue = CUE_STOP;
      }
      break;
  }
}

void cue_write_cb(uint16_t conn_hdl, BLECharacteristic* chr,
                  uint8_t* data, uint16_t len) {
  (void)conn_hdl; (void)chr;
  if (len < 1) return;
  pendingCue   = data[0];
  pendingParam = (len > 1) ? data[1] : 0;
}

// --- setup ----------------------------------------------------------------
void setReports() {
  lastReportEnable = millis();
  if (!bno.enableReport(SH2_GAME_ROTATION_VECTOR, 20000))
    Serial.println("IMU enableReport failed");
}

// attempts = 10 at boot (the cold-boot race, see pod-reference.md section 8);
// 1 for the retries in loop(), which mustn't block the cue engine for long.
bool startIMU(int attempts) {
  pinMode(BNO_RST, OUTPUT);
  digitalWrite(BNO_RST, LOW);  delay(10);
  digitalWrite(BNO_RST, HIGH); delay(300);
  for (int t = 0; t < attempts; t++) {
    if (bno.begin_I2C(0x4B)) {
      setReports();
      lastImuEvent = millis();   // the quiet/dead clocks start now
      return true;
    }
    if (t + 1 < attempts) delay(200);
  }
  return false;
}

// Called every loop: reads the IMU and keeps it alive.
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

  if (bno.wasReset()) {
    Serial.println("IMU reset, re-enabling report");
    setReports();
  }

  if (bno.getSensorEvent(&v) && v.sensorId == SH2_GAME_ROTATION_VECTOR) {
    quat[0] = v.un.gameRotationVector.i;
    quat[1] = v.un.gameRotationVector.j;
    quat[2] = v.un.gameRotationVector.k;
    quat[3] = v.un.gameRotationVector.real;
    lastImuEvent = now;
    return;
  }

  if (now - lastImuEvent >= IMU_DEAD_MS) {
    Serial.println("IMU silent 5 s, restarting it");
    quat[0] = quat[1] = quat[2] = quat[3] = 0;   // "no IMU reading" until it's back
    imuOK = false;
    lastImuRetry = 0;                            // retry on the next loop
  } else if (now - lastImuEvent >= IMU_QUIET_MS && now - lastReportEnable >= IMU_QUIET_MS) {
    Serial.println("IMU quiet, re-enabling report");
    setReports();
  }
}

bool imuHealthy() { return imuOK && millis() - lastImuEvent < IMU_QUIET_MS; }

void setup() {
  Serial.begin(115200);
  pinMode(LED_BUILTIN, OUTPUT);
  digitalWrite(LED_BUILTIN, HIGH);

  delay(500);
  Wire.begin();
  sprintf(podName, "POD_%02d", POD_ID);

  imuOK = startIMU(10);
  lastImuRetry = millis();
  Serial.println(imuOK ? "IMU OK" : "IMU FAILED (will retry every 2 s)");

  drvOK = drv.begin();
  if (drvOK) {
    drv.selectLibrary(6);
    drv.useLRA();
    drv.setMode(DRV2605_MODE_INTTRIG);
  } else {
    Serial.println("DRV FAILED");
  }

  Bluefruit.begin();
  Bluefruit.setTxPower(4);
  Bluefruit.setName(podName);
  Bluefruit.Periph.setConnInterval(12, 24);

  podService.begin();

  quatChar.setProperties(CHR_PROPS_NOTIFY);
  quatChar.setPermission(SECMODE_OPEN, SECMODE_NO_ACCESS);
  quatChar.setFixedLen(16);
  quatChar.begin();

  cueChar.setProperties(CHR_PROPS_WRITE | CHR_PROPS_WRITE_WO_RESP);
  cueChar.setPermission(SECMODE_NO_ACCESS, SECMODE_OPEN);
  cueChar.setFixedLen(2);
  cueChar.setWriteCallback(cue_write_cb);
  cueChar.begin();

  Bluefruit.Advertising.addFlags(BLE_GAP_ADV_FLAGS_LE_ONLY_GENERAL_DISC_MODE);
  Bluefruit.Advertising.addTxPower();
  Bluefruit.Advertising.addService(podService);
  Bluefruit.ScanResponse.addName();
  Bluefruit.Advertising.restartOnDisconnect(true);
  Bluefruit.Advertising.setInterval(32, 244);
  Bluefruit.Advertising.setFastTimeout(30);
  Bluefruit.Advertising.start(0);

  Serial.print(podName); Serial.println(" advertising");
}

void loop() {
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

  if (millis() - lastNotify >= 50) {
    lastNotify = millis();
    if (Bluefruit.connected()) quatChar.notify((uint8_t*)quat, 16);
  }
}
