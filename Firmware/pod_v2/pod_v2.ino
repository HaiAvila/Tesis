// 16 - POD firmware v3. ArduinoBLE on the MBED core.
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
// Cue packet = 2 bytes: [cueId, param]
//   0 STOP   1 CLICK   2 PULSE(param=ms/10)   3 TRIPLE
//   4 LONG(param=ms/10, 0=500ms)              5 DOUBLE
//
// Quaternion notify = 4 floats (i,j,k,real) every 50 ms.
// All four zero = no IMU reading, so Unity can tell that apart from a pod
// lying still.
//
// LED: slow blink = IMU readings arriving, fast blink = BLE up, no IMU.

#define POD_ID  2

#include <ArduinoBLE.h>
#include <Wire.h>
#include <Adafruit_BNO08x.h>
#include <Adafruit_DRV2605.h>

#define BNO_RST D2

BLEService        podService("19b10000-e8f2-537e-4f6c-d104768a1214");
BLECharacteristic quatChar("19b10001-e8f2-537e-4f6c-d104768a1214",
                           BLERead | BLENotify, 16);
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

// --- cue engine -----------------------------------------------------------
enum { CUE_STOP = 0, CUE_CLICK, CUE_PULSE, CUE_TRIPLE, CUE_LONG, CUE_DOUBLE };

volatile uint8_t pendingCue = 255, pendingParam = 0;
uint8_t  curCue = CUE_STOP, curParam = 0, cueStep = 0;
unsigned long cueT = 0;

void fireEffect(uint8_t e) {
  if (!drvOK) return;
  drv.setWaveform(0, e);
  drv.setWaveform(1, 0);
  drv.go();
}

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

// --- setup ----------------------------------------------------------------
void setup() {
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

  if (millis() - lastNotify >= 50) {
    lastNotify = millis();
    quatChar.writeValue((uint8_t*)quat, 16);
  }
}
