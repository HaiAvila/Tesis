// 18 - RECEIVER v3. Bluefruit multi-central.
// Board: Seeed nRF52 Boards (classic) -> Seeed XIAO nRF52840
//
// v3 = v2 (with both 2026-11-04 hardware-verified fixes) + battery/charging from
// 19-byte pod packets + RSSI + a new H line. P, S and R lines are unchanged.
//
// Serial protocol, 115200 baud.
//
//  OUT (receiver -> PC), one line per pod sample:
//      P,<id>,<qi>,<qj>,<qk>,<qr>
//      P,3,0.0120,-0.9981,0.0450,0.0310
//
//  OUT, once a second:
//      S,<connected>,<target>,<id>,<id>,...        (who is connected)
//
//  OUT, every 2 s per connected pod:
//      H,<id>,<battery_mV>,<charging>,<rssi>
//      battery_mV  integer mV; 0 = the pod sent no battery (old 16-byte packet,
//                  or no reading yet)
//      charging    1 = charging, 0 = not, -1 = unknown (old 16-byte packet)
//      rssi        dBm as the receiver sees that pod (negative); 0 = not available
//
//  IN (PC -> receiver), one command per line:
//      C,<id>,<cue>,<param>      fire a cue on one pod
//      C,0,<cue>,<param>         id 0 = broadcast to every pod
//
//      cue: 0 STOP  1 CLICK  2 PULSE  3 TRIPLE  4 LONG  5 DOUBLE  6 DONE  7 FAIL
//
//  Try it by hand in the Serial Monitor (line ending = Newline):
//      C,0,3,0      -> every pod plays the camera-start triple
//      C,2,2,20     -> pod 2 pulses every 200 ms
//      C,2,0,0      -> pod 2 stops

#define MAX_SLOTS 12
#define MAX_PODS  9

#if MAX_PODS > MAX_SLOTS
  #error "MAX_PODS cannot exceed MAX_SLOTS"
#endif

#include <bluefruit.h>

const uint8_t SVC_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x00,0x00,0xb1,0x19
};
const uint8_t QUAT_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x01,0x00,0xb1,0x19
};
const uint8_t CUE_UUID[16] = {
  0x14,0x12,0x8a,0x76,0x04,0xd1,0x6c,0x4f,
  0x7e,0x53,0xf2,0xe8,0x02,0x00,0xb1,0x19
};

#define S BLEClientService(SVC_UUID)
#define Q BLEClientCharacteristic(QUAT_UUID)
#define C BLEClientCharacteristic(CUE_UUID)
BLEClientService        podSvc[MAX_SLOTS]  = { S,S,S,S,S,S,S,S,S,S,S,S };
BLEClientCharacteristic quatChr[MAX_SLOTS] = { Q,Q,Q,Q,Q,Q,Q,Q,Q,Q,Q,Q };
BLEClientCharacteristic cueChr[MAX_SLOTS]  = { C,C,C,C,C,C,C,C,C,C,C,C };
#undef S
#undef Q
#undef C

#define PKT_V3 16      // quaternion only
#define PKT_V4 19      // quaternion + VBAT mV (uint16 LE) + flags

bool     slotUsed[MAX_SLOTS];
bool     hasCue[MAX_SLOTS];
uint8_t  podIdOf[MAX_SLOTS];
uint16_t handles[MAX_SLOTS];
float    quat[MAX_SLOTS][4];
volatile bool dirty[MAX_SLOTS];

// Battery info from 19-byte packets, written by notify_cb (BLE task) inside the
// same critical section as the quaternion.
bool     hasBat[MAX_SLOTS];      // last packet from this slot was 19 bytes
uint16_t batMv[MAX_SLOTS];
uint8_t  batFlags[MAX_SLOTS];    // bit0 charging, bit1 reading valid

char    cmdBuf[48];
uint8_t cmdLen = 0;
unsigned long lastStatus = 0, lastHealth = 0;

#define HEALTH_PERIOD_MS 2000

int connectedCount() {
  int n = 0;
  for (int i = 0; i < MAX_PODS; i++) if (slotUsed[i]) n++;
  return n;
}

int slotOfPod(uint8_t id) {
  for (int i = 0; i < MAX_PODS; i++)
    if (slotUsed[i] && podIdOf[i] == id) return i;
  return -1;
}

void clearBat(int i) { hasBat[i] = false; batMv[i] = 0; batFlags[i] = 0; }

void setup() {
  Serial.begin(115200);
  while (!Serial) delay(10);

  for (int i = 0; i < MAX_SLOTS; i++) {
    slotUsed[i] = false; hasCue[i] = false; dirty[i] = false;
    podIdOf[i]  = 0;     handles[i] = BLE_CONN_HANDLE_INVALID;
    clearBat(i);
  }

  if (!Bluefruit.begin(0, MAX_PODS)) {
    Serial.println("E,bluefruit begin failed");
    while (1) delay(100);
  }
  Bluefruit.setName("RECEIVER");

  for (int i = 0; i < MAX_PODS; i++) {
    podSvc[i].begin();
    // false = run notify_cb straight from the BLE task, not through the shared callback
    // queue. connect_cb runs on that queue and blocks in discover() for ~1-2 s while a pod
    // (re)connects; with notifications on the same queue, every other pod's samples waited
    // behind it - all 9 froze together (seen twice in a 10-min run, each time a low-battery
    // pod reconnected). notify_cb only copies a few bytes, so it's safe to run directly.
    quatChr[i].setNotifyCallback(notify_cb, false);
    quatChr[i].begin();
    cueChr[i].begin();
  }

  Bluefruit.Central.setConnectCallback(connect_cb);
  Bluefruit.Central.setDisconnectCallback(disconnect_cb);

  Bluefruit.Scanner.setRxCallback(scan_cb);
  Bluefruit.Scanner.restartOnDisconnect(true);
  Bluefruit.Scanner.setInterval(160, 80);
  Bluefruit.Scanner.filterUuid(SVC_UUID);
  Bluefruit.Scanner.useActiveScan(true);
  Bluefruit.Scanner.start(0);

  Serial.println("R,receiver up");
}

void scan_cb(ble_gap_evt_adv_report_t* report) {
  Bluefruit.Central.connect(report);
}

void connect_cb(uint16_t conn_handle) {
  int id = -1;
  for (int i = 0; i < MAX_PODS; i++) if (!slotUsed[i]) { id = i; break; }
  if (id < 0) { Bluefruit.disconnect(conn_handle); return; }

  char name[16] = {0};
  BLEConnection* c = Bluefruit.Connection(conn_handle);
  c->getPeerName(name, sizeof(name));
  c->requestConnectionParameter(12);

  // "POD_07" -> 7
  uint8_t pid = 0;
  if (strlen(name) >= 6) pid = (name[4] - '0') * 10 + (name[5] - '0');
  if (pid == 0 || pid > 99) { Bluefruit.disconnect(conn_handle); Bluefruit.Scanner.start(0); return; }

  // Before enableNotify: notifications now reach notify_cb at once (not queued behind this
  // callback), so the slot must already carry this pod's ID. loop() still prints nothing
  // from it until slotUsed is set below. Setting it after used to label a reconnecting
  // pod's first sample with the slot's previous pod (pod 9's as P,2) or 0 at startup.
  podIdOf[id] = pid;
  dirty[id]   = false;
  taskENTER_CRITICAL();
  clearBat(id);                   // no battery info carried over from the slot's last pod
  taskEXIT_CRITICAL();

  if (!podSvc[id].discover(conn_handle) || !quatChr[id].discover()
      || !quatChr[id].enableNotify()) {
    Serial.print("E,discover failed for "); Serial.println(name);
    Bluefruit.disconnect(conn_handle);
    Bluefruit.Scanner.start(0);
    return;
  }

  hasCue[id] = cueChr[id].discover();     // optional: old pods lack it

  // RSSI: one non-blocking SoftDevice call; the stack then keeps the value cached
  // and loop() only reads it.
  c->monitorRssi();

  handles[id]  = conn_handle;
  slotUsed[id] = true;

  Serial.print("R,connected,"); Serial.print(pid);
  Serial.print(","); Serial.print(hasCue[id] ? "cue" : "nocue");
  Serial.print(","); Serial.println(connectedCount());

  if (connectedCount() < MAX_PODS) Bluefruit.Scanner.start(0);
}

void disconnect_cb(uint16_t conn_handle, uint8_t reason) {
  for (int i = 0; i < MAX_PODS; i++) {
    if (slotUsed[i] && handles[i] == conn_handle) {
      Serial.print("R,lost,"); Serial.print(podIdOf[i]);
      Serial.print(",0x"); Serial.println(reason, HEX);
      slotUsed[i] = false;
      hasCue[i]   = false;
      dirty[i]    = false;
      handles[i]  = BLE_CONN_HANDLE_INVALID;
      taskENTER_CRITICAL();
      clearBat(i);
      taskEXIT_CRITICAL();
      break;
    }
  }
  Bluefruit.Scanner.start(0);
}

// Accepts the old 16-byte packet (v3 pods) and the new 19-byte one (v4 pods), so one
// pod can be flashed and soak-tested while the other 8 still run v3.
void notify_cb(BLEClientCharacteristic* chr, uint8_t* data, uint16_t len) {
  if (len != PKT_V3 && len != PKT_V4) return;
  for (int i = 0; i < MAX_PODS; i++) {
    if (&quatChr[i] == chr) {
      // Runs in the BLE task while loop() may be reading: copy as one piece.
      taskENTER_CRITICAL();
      memcpy(quat[i], data, 16);
      if (len == PKT_V4) {
        hasBat[i]   = true;
        batMv[i]    = (uint16_t)data[16] | ((uint16_t)data[17] << 8);
        batFlags[i] = data[18];
      } else {
        hasBat[i]   = false;
      }
      dirty[i] = true;
      taskEXIT_CRITICAL();
      return;
    }
  }
}

void sendCue(int slot, uint8_t cue, uint8_t param) {
  if (slot < 0 || !slotUsed[slot] || !hasCue[slot]) return;
  uint8_t pkt[2] = { cue, param };
  cueChr[slot].write(pkt, 2);
}

void handleCommand(char* line) {
  if (line[0] != 'C') return;
  int id = 0, cue = 0, param = 0;
  if (sscanf(line, "C,%d,%d,%d", &id, &cue, &param) < 2) return;

  if (id == 0) {                                  // broadcast
    for (int i = 0; i < MAX_PODS; i++) sendCue(i, cue, param);
  } else {
    sendCue(slotOfPod((uint8_t)id), cue, param);
  }
}

void pollSerial() {
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n' || ch == '\r') {
      if (cmdLen) { cmdBuf[cmdLen] = 0; handleCommand(cmdBuf); cmdLen = 0; }
    } else if (cmdLen < sizeof(cmdBuf) - 1) {
      cmdBuf[cmdLen++] = ch;
    }
  }
}

void printHealth() {
  char out[48];
  for (int i = 0; i < MAX_PODS; i++) {
    bool used, bat;
    uint16_t mv; uint8_t flags; uint16_t h;
    taskENTER_CRITICAL();
    used  = slotUsed[i];
    bat   = hasBat[i];
    mv    = batMv[i];
    flags = batFlags[i];
    h     = handles[i];
    taskEXIT_CRITICAL();
    if (!used) continue;

    int mvOut  = (bat && (flags & 0x02)) ? mv : 0;
    int chgOut = bat ? ((flags & 0x01) ? 1 : 0) : -1;

    int rssi = 0;
    BLEConnection* c = Bluefruit.Connection(h);
    if (c) rssi = c->getRssi();     // cached by the stack; 0 if not available yet

    snprintf(out, sizeof(out), "H,%u,%d,%d,%d", podIdOf[i], mvOut, chgOut, rssi);
    Serial.println(out);
  }
}

void loop() {
  pollSerial();

  char out[64];
  float q[4];
  for (int i = 0; i < MAX_PODS; i++) {
    // notify_cb writes quat[i] from the BLE task: take a whole copy, never half old / half new.
    taskENTER_CRITICAL();
    // Only fully set-up connections (slotUsed is set at the end of connect_cb); a sample
    // that arrives while a pod is still connecting is dropped.
    bool fresh = dirty[i] && slotUsed[i];
    if (fresh)
      memcpy(q, quat[i], 16);
    dirty[i] = false;
    taskEXIT_CRITICAL();
    if (!fresh) continue;

    snprintf(out, sizeof(out), "P,%u,%.4f,%.4f,%.4f,%.4f",
             podIdOf[i], q[0], q[1], q[2], q[3]);
    Serial.println(out);
  }

  if (millis() - lastStatus >= 1000) {
    lastStatus = millis();
    Serial.print("S,"); Serial.print(connectedCount());
    Serial.print(","); Serial.print(MAX_PODS);
    for (int i = 0; i < MAX_PODS; i++)
      if (slotUsed[i]) { Serial.print(","); Serial.print(podIdOf[i]); }
    Serial.println();
  }

  if (millis() - lastHealth >= HEALTH_PERIOD_MS) {
    lastHealth = millis();
    printHealth();
  }
}
