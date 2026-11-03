// 15 - RECEIVER v2. Bluefruit multi-central.
// Board: Seeed nRF52 Boards (classic) -> Seeed XIAO nRF52840
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
//  IN (PC -> receiver), one command per line:
//      C,<id>,<cue>,<param>      fire a cue on one pod
//      C,0,<cue>,<param>         id 0 = broadcast to every pod
//
//      cue: 0 STOP  1 CLICK  2 PULSE  3 TRIPLE  4 LONG  5 DOUBLE
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

bool     slotUsed[MAX_SLOTS];
bool     hasCue[MAX_SLOTS];
uint8_t  podIdOf[MAX_SLOTS];
uint16_t handles[MAX_SLOTS];
float    quat[MAX_SLOTS][4];
volatile bool dirty[MAX_SLOTS];

char    cmdBuf[48];
uint8_t cmdLen = 0;
unsigned long lastStatus = 0;

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

void setup() {
  Serial.begin(115200);
  while (!Serial) delay(10);

  for (int i = 0; i < MAX_SLOTS; i++) {
    slotUsed[i] = false; hasCue[i] = false; dirty[i] = false;
    podIdOf[i]  = 0;     handles[i] = BLE_CONN_HANDLE_INVALID;
  }

  if (!Bluefruit.begin(0, MAX_PODS)) {
    Serial.println("E,bluefruit begin failed");
    while (1) delay(100);
  }
  Bluefruit.setName("RECEIVER");

  for (int i = 0; i < MAX_PODS; i++) {
    podSvc[i].begin();
    quatChr[i].setNotifyCallback(notify_cb);
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

  if (!podSvc[id].discover(conn_handle) || !quatChr[id].discover()
      || !quatChr[id].enableNotify()) {
    Serial.print("E,discover failed for "); Serial.println(name);
    Bluefruit.disconnect(conn_handle);
    Bluefruit.Scanner.start(0);
    return;
  }

  hasCue[id] = cueChr[id].discover();     // optional: old pods lack it

  podIdOf[id]  = pid;
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
      handles[i]  = BLE_CONN_HANDLE_INVALID;
      break;
    }
  }
  Bluefruit.Scanner.start(0);
}

void notify_cb(BLEClientCharacteristic* chr, uint8_t* data, uint16_t len) {
  if (len != 16) return;
  for (int i = 0; i < MAX_PODS; i++) {
    if (&quatChr[i] == chr) {
      memcpy(quat[i], data, 16);
      dirty[i] = true;
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

void loop() {
  pollSerial();

  char out[64];
  for (int i = 0; i < MAX_PODS; i++) {
    if (!dirty[i]) continue;
    dirty[i] = false;
    snprintf(out, sizeof(out), "P,%u,%.4f,%.4f,%.4f,%.4f",
             podIdOf[i], quat[i][0], quat[i][1], quat[i][2], quat[i][3]);
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
}
