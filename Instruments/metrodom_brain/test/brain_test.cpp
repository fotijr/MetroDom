// Host tests for the brain's core (no Arduino needed): make -C Instruments/metrodom_brain/test
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "../src/brain/Body.h"
#include "../src/brain/Brain.h"
#include "../src/brain/Devices.h"

using namespace metrodom;

namespace {

int failures = 0;
int checks = 0;

#define CHECK(cond)                                                     \
  do {                                                                  \
    ++checks;                                                           \
    if (!(cond)) {                                                      \
      ++failures;                                                       \
      std::printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);     \
    }                                                                   \
  } while (0)

struct FakePins : Outputs {
  bool level[64] = {};
  bool broken[64] = {};  // writes to these fail and change nothing
  std::vector<std::pair<uint8_t, bool>> writes;
  bool enabled = false;
  std::vector<bool> enables;
  void enable(bool on) override {
    enabled = on;
    enables.push_back(on);
  }
  bool set(uint8_t output, bool on) override {
    writes.emplace_back(output, on);
    if (broken[output]) return false;
    level[output] = on;
    return true;
  }
};

struct FakeStore : BodyStore {
  bool works = true;
  std::vector<uint8_t> written;
  bool write(const uint8_t* data, size_t length) override {
    if (!works) return false;
    written.assign(data, data + length);
    return true;
  }
};

struct FakeLeds : Leds {
  uint8_t rgb[kMaxLeds][3] = {};
  uint8_t shown[kMaxLeds][3] = {};
  int shows = 0;
  void set(uint8_t index, uint8_t r, uint8_t g, uint8_t b) override {
    rgb[index][0] = r;
    rgb[index][1] = g;
    rgb[index][2] = b;
  }
  void show() override {
    std::memcpy(shown, rgb, sizeof shown);
    ++shows;
  }
  bool dark() const {
    for (auto& led : shown) {
      if (led[0] || led[1] || led[2]) return false;
    }
    return true;
  }
};

struct FakeReplies : Replies {
  std::vector<std::vector<uint8_t>> sent;
  void sysex(const uint8_t* data, size_t length) override { sent.emplace_back(data, data + length); }
};

const Actuator kThree[] = {
    {60, 2, 30, 60, 9},
    {64, 3, 30, 60, 10},
    {67, 4, 80, 20, 0},  // pulse over maxOnMs, interval under the pulse: both get clamped
};

BrainConfig Three() {
  BrainConfig c;
  c.id = "xylo";
  c.name = "Xylo \"test\"";
  c.actuators = kThree;
  c.actuatorCount = 3;
  c.polyphony = 2;
  return c;
}

struct Rig {
  BrainConfig config;
  FakePins pins;
  FakeReplies replies;
  FakeStore store;
  FakeLeds leds;
  Brain brain;
  explicit Rig(BrainConfig c = Three(), bool canWrite = true, bool withLeds = false)
      : config(c), brain(config, pins, replies, canWrite ? &store : nullptr, withLeds ? &leds : nullptr) {
    brain.begin(0);
  }
  void send(std::initializer_list<uint8_t> bytes, uint32_t now) {
    std::vector<uint8_t> m(bytes);
    brain.message(m.data(), m.size(), now);
  }
  void on(uint8_t note, uint32_t now, uint8_t channel = 0, uint8_t velocity = 100) {
    send({uint8_t(0x90 | channel), note, velocity}, now);
  }
  void raw(const std::vector<uint8_t>& m, uint32_t now = 0) { brain.message(m.data(), m.size(), now); }
};

void begin_TurnsEveryOutputOff() {
  Rig r;
  CHECK(r.pins.writes.size() == 3);
  for (auto& w : r.pins.writes) CHECK(!w.second);
}

void noteOn_StrikesAFixedPulse_WhateverTheNoteOff() {
  Rig r;
  r.on(60, 1000);
  CHECK(r.pins.level[2]);
  r.send({0x80, 60, 0}, 1005);  // early Note Off changes nothing
  r.brain.tick(1029);
  CHECK(r.pins.level[2]);
  r.brain.tick(1030);
  CHECK(!r.pins.level[2]);
  r.on(60, 2000);
  r.brain.tick(2000 + 10'000);  // a long-held note still ends at the pulse
  CHECK(!r.pins.level[2]);
}

void clamps_PulseToMaxOn_AndIntervalToThePulse() {
  Rig r;
  CHECK(r.brain.pulseMs(2) == 45);
  CHECK(r.brain.minIntervalMs(2) == 45);
}

void minInterval_RefusesRepeatsThatComeTooSoon() {
  Rig r;
  r.on(60, 0);
  r.brain.tick(30);
  r.on(60, 59);
  CHECK(r.brain.lastRefusal() == Refusal::Interval);
  CHECK(!r.pins.level[2]);
  r.on(60, 60);
  CHECK(r.brain.lastRefusal() == Refusal::None);
  CHECK(r.pins.level[2]);
}

void stillStriking_IsRefused() {
  Rig r;
  r.on(64, 0);
  r.on(64, 10);
  CHECK(r.brain.lastRefusal() == Refusal::Busy);
}

void duty_RefusesWhenTheThermalBudgetIsSpent() {
  // Back-to-back 30 ms pulses ask for 100% duty against a 30% cap. The 300 ms budget fills by
  // 30 ms and drains 9 ms per strike: 13 in a row, then about one in three (the 30%).
  static const Actuator hot[] = {{60, 2, 30, 30, 0}};
  BrainConfig c = Three();
  c.actuators = hot;
  c.actuatorCount = 1;
  Rig r(c);
  int struck = 0;
  for (uint32_t t = 0; t < 20 * 30; t += 30) {
    r.on(60, t);
    if (r.brain.lastRefusal() == Refusal::None) ++struck;
    r.brain.tick(t + 30);
  }
  CHECK(struck == 15);  // 13 + the 15th and 18th tries
  CHECK(r.brain.counters().duty == 5);

  // A rest refills it.
  r.on(60, 600 + 1000);
  CHECK(r.brain.lastRefusal() == Refusal::None);
}

void duty_SustainedRateMatchesThePercentage() {
  Rig r;
  int struck = 0;
  for (uint32_t t = 0; t < 10'000; t += 60) {
    r.on(60, t);
    if (r.brain.lastRefusal() == Refusal::None) ++struck;
    r.brain.tick(t + 30);
  }
  // 10 s at 30%: 3000 ms of on-time + the 300 ms burst = 110 strikes of 30 ms at most.
  CHECK(struck <= 110);
  CHECK(struck >= 100);
}

void polyphony_LimitsActuatorsOnAtOnce() {
  Rig r;
  r.on(60, 0);
  r.on(64, 0);
  r.on(67, 0);
  CHECK(r.brain.lastRefusal() == Refusal::Polyphony);
  CHECK(!r.pins.level[4]);
}

void panic_TurnsEverythingOff_OnAnyChannel() {
  Rig r;
  r.on(60, 0);
  r.on(64, 0);
  r.send({0xB9, 123, 0}, 5);
  CHECK(!r.pins.level[2] && !r.pins.level[3]);
  r.on(60, 100);
  r.send({0xB0, 120, 0}, 105);
  CHECK(!r.pins.level[2]);
  r.on(64, 200);
  r.send({0xFF}, 205);
  CHECK(!r.pins.level[3]);
}

void metroDomAllOff_TurnsEverythingOff_AndNotesStillPlayAfter() {
  Rig r;
  r.on(60, 0);
  r.on(64, 0);
  r.send({0xF0, 0x7D, 0x4D, 0x44, 0x03, 0xF7}, 5);
  CHECK(!r.pins.level[2] && !r.pins.level[3]);
  CHECK(r.replies.sent.empty());
  r.on(60, 100);  // like CC 120: it stops what's on, it doesn't latch
  CHECK(r.pins.level[2]);
}

void otherSysEx_DoesntPanic() {
  Rig r;
  r.on(60, 0);
  r.send({0xF0, 0x7D, 0x00, 0x00, 0x03, 0xF7}, 1);  // someone else's 7D message
  r.send({0xF0, 0x7D, 0x4D, 0x44, 0x7F, 0xF7}, 1);  // a MetroDom sub-ID we don't know
  CHECK(r.pins.level[2]);
}

void otherControllers_DontPanic() {
  Rig r;
  r.on(60, 0);
  r.send({0xB0, 7, 0}, 1);
  CHECK(r.pins.level[2]);
}

void hostLost_TurnsOff_AndRefusesUntilBack() {
  Rig r;
  r.on(60, 0);
  r.brain.hostConnected(false);
  CHECK(!r.pins.level[2]);
  r.on(64, 500);
  CHECK(r.brain.lastRefusal() == Refusal::NoHost);
  r.brain.hostConnected(true);
  r.on(64, 600);
  CHECK(r.pins.level[3]);
}

void channel_OmniByDefault_OrOnlyItsOwn() {
  Rig omni;
  omni.on(60, 0, 9);
  CHECK(omni.pins.level[2]);

  BrainConfig c = Three();
  c.channel = 0;
  Rig one(c);
  one.on(60, 0, 9);
  CHECK(one.brain.lastRefusal() == Refusal::NotMine);
  one.on(60, 0, 0);
  CHECK(one.pins.level[2]);
}

void unknownNotes_AndVelocityZero_DoNothing() {
  Rig r;
  r.on(61, 0);
  CHECK(r.brain.lastRefusal() == Refusal::NotMine);
  r.send({0x90, 60, 0}, 0);
  CHECK(!r.pins.level[2]);
}

void identityRequest_GetsMetroDomsReply() {
  Rig r;
  r.send({0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7}, 0);
  CHECK(r.replies.sent.size() == 1);
  std::vector<uint8_t> expected = {0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x7D, 0x4D, 0x44, 0x01, 0x00, 0, 1, 0, 0, 0xF7};
  CHECK(r.replies.sent[0] == expected);

  r.send({0xF0, 0x7E, 0x10, 0x06, 0x01, 0xF7}, 0);  // to another device id
  r.send({0xF0, 0x7E, 0x7F, 0x06, 0x02, 0xF7}, 0);  // someone else's reply
  CHECK(r.replies.sent.size() == 1);
}

void profileRequest_SendsTheProfileInChunks() {
  Rig r;
  r.send({0xF0, 0x7D, 0x4D, 0x44, 0x01, 0xF7}, 0);
  size_t length = r.brain.profileLength();
  size_t count = (length + 127) / 128;
  CHECK(length > 128);  // more than one chunk, so reassembly is exercised
  CHECK(r.replies.sent.size() == count);
  std::string text;
  for (size_t i = 0; i < r.replies.sent.size(); ++i) {
    auto& c = r.replies.sent[i];
    CHECK(c[0] == 0xF0 && c[1] == 0x7D && c[2] == 0x4D && c[3] == 0x44 && c[4] == 0x02);
    CHECK(c[5] == i && c[6] == count && c.back() == 0xF7);
    CHECK(c.size() <= 136);
    for (size_t j = 7; j + 1 < c.size(); ++j) {
      CHECK(c[j] <= 0x7F);
      text.push_back(static_cast<char>(c[j]));
    }
  }
  CHECK(text == r.brain.profileJson());
}

void profile_DescribesWhatTheBrainEnforces() {
  Rig r;
  std::string json = r.brain.profileJson();
  CHECK(json.find("\"name\":\"Xylo \\\"test\\\"\"") != std::string::npos);
  CHECK(json.find("{\"note\":60,\"latencyMs\":9,\"minIntervalMs\":60,\"pulseMs\":30}") != std::string::npos);
  CHECK(json.find("{\"note\":67,\"minIntervalMs\":45,\"pulseMs\":45}") != std::string::npos);  // clamped, unmeasured
  CHECK(json.find("\"dutyWindowMs\":1000") != std::string::npos);
  CHECK(json.find("\"polyphony\":2") != std::string::npos);
  CHECK(json.find("\"identity\":{\"manufacturer\":\"7D\",\"family\":8781,\"model\":1}") != std::string::npos);
  CHECK(json.find("\"channel\"") == std::string::npos);  // omni: no auto-band hint
}

void benchProfile_MatchesTheCommittedFile() {
  // The M0 bench config's profile is committed as bench-profile.json; the .NET tests parse
  // that file with the Conductor's own reader, so firmware and Conductor agree on the format.
  static const Actuator bench[] = {{60, 15, 30, 60, 0}};
  BrainConfig c;
  c.id = "bench-solenoid";
  c.name = "Bench solenoid";
  c.actuators = bench;
  c.actuatorCount = 1;
  FakePins pins;
  FakeReplies replies;
  Brain brain(c, pins, replies);
  std::ifstream file(BENCH_PROFILE);
  std::stringstream text;
  text << file.rdbuf();
  std::string committed = text.str();
  while (!committed.empty() && (committed.back() == '\n' || committed.back() == '\r')) committed.pop_back();
  if (committed != brain.profileJson()) std::printf("  expected bench-profile.json to be:\n%s\n", brain.profileJson());
  CHECK(committed == brain.profileJson());
}

void timeWrap_IsHandled() {
  Rig r;
  uint32_t t = 0xFFFFFFF0u;
  r.brain.begin(t);
  r.on(60, t);
  r.brain.tick(t + 30);  // wraps past zero
  CHECK(!r.pins.level[2]);
  r.on(60, t + 70);
  CHECK(r.brain.lastRefusal() == Refusal::None);
}

void velocity_ScalesThePulse_BetweenMinPulseAndPulse() {
  static const Actuator soft[] = {{60, 2, 40, 100, 0, 10}};
  BrainConfig c = Three();
  c.actuators = soft;
  c.actuatorCount = 1;
  Rig r(c);
  CHECK(r.brain.pulseFor(0, 127) == 40);
  CHECK(r.brain.pulseFor(0, 1) == 10);
  CHECK(r.brain.pulseFor(0, 64) == 25);
  r.on(60, 0, 0, 1);
  r.brain.tick(9);
  CHECK(r.pins.level[2]);
  r.brain.tick(10);
  CHECK(!r.pins.level[2]);
  r.on(60, 1000, 0, 127);
  r.brain.tick(1039);
  CHECK(r.pins.level[2]);
  r.brain.tick(1040);
  CHECK(!r.pins.level[2]);
  std::string json = r.brain.profileJson();
  CHECK(json.find("\"velocity\":{\"supported\":true}") != std::string::npos);
  CHECK(json.find("{\"note\":60,\"minIntervalMs\":100,\"minPulseMs\":10,\"pulseMs\":40}") != std::string::npos);
}

void velocity_SoftStrikesSpendLessDuty() {
  // 30 ms at 100% asks too much; 10 ms strikes every 30 ms is 33%, close to the 30% cap.
  static const Actuator soft[] = {{60, 2, 30, 30, 0, 10}};
  BrainConfig c = Three();
  c.actuators = soft;
  c.actuatorCount = 1;
  Rig loud(c), quiet(c);
  int hard = 0, light = 0;
  for (uint32_t t = 0; t < 3000; t += 30) {
    loud.on(60, t, 0, 127);
    if (loud.brain.lastRefusal() == Refusal::None) ++hard;
    loud.brain.tick(t + 30);
    quiet.on(60, t, 0, 1);
    if (quiet.brain.lastRefusal() == Refusal::None) ++light;
    quiet.brain.tick(t + 30);
  }
  CHECK(light > hard * 2);
}

void ceilings_NoConfigCanRaiseThem() {
  static const Actuator long_[] = {{60, 2, 900, 900, 0}};
  BrainConfig c = Three();
  c.actuators = long_;
  c.actuatorCount = 1;
  c.maxOnMs = 1000;
  c.maxDutyPercent = 90;
  Rig r(c);
  CHECK(r.brain.pulseMs(0) == kCeilingOnMs);
  std::string json = r.brain.profileJson();
  CHECK(json.find("\"maxOnMs\":250,\"maxDutyPercent\":50") != std::string::npos);
}

void offThatFails_BlocksEveryStrike_UntilItTakes() {
  Rig r;
  r.on(60, 0);
  r.pins.broken[2] = true;  // the driver stops answering mid-strike
  r.brain.tick(30);
  CHECK(r.brain.faulted());
  CHECK(r.brain.counters().faults == 1);
  r.on(64, 100);
  CHECK(r.brain.lastRefusal() == Refusal::Fault);
  CHECK(!r.pins.level[3]);
  r.brain.tick(101);  // still broken: retried, still faulted
  CHECK(r.brain.faulted());
  r.pins.broken[2] = false;
  r.brain.tick(102);
  CHECK(!r.brain.faulted());
  CHECK(!r.pins.level[2]);
  r.on(64, 200);
  CHECK(r.pins.level[3]);
}

void onThatFails_IsRefused_AndTurnedOff() {
  Rig r;
  r.pins.broken[2] = true;
  r.on(60, 0);
  CHECK(r.brain.lastRefusal() == Refusal::Fault);
  CHECK(!r.brain.isOn(0));
  CHECK(r.brain.faulted());  // the off failed too: keep trying
  r.pins.broken[2] = false;
  r.brain.tick(1);
  CHECK(!r.brain.faulted());
}

void noActuators_IsASilentBrain_ThatStillDescribesItself() {
  BrainConfig c;
  c.id = "metrodom-brain";
  c.name = "MetroDom brain (body: bad CRC)";
  c.actuators = nullptr;
  c.actuatorCount = 0;
  Rig r(c);
  r.on(60, 0);
  CHECK(r.brain.lastRefusal() == Refusal::NotMine);
  std::string json = r.brain.profileJson();
  CHECK(json.find("\"notes\":[]") != std::string::npos);
  CHECK(json.find("(body: bad CRC)") != std::string::npos);
}

// The M1 xylophone body (bodies/m1-xylophone.json): three bars on a PCA9685, softer with velocity.
Body M1() {
  Body b;
  b.driver = BodyDriver::Pca9685;
  b.address = 0x40;
  b.polyphony = 2;
  b.maxOnMs = 45;
  b.maxDutyPercent = 30;
  b.count = 3;
  b.actuators[0] = {72, 0, 30, 60, 0, 12};
  b.actuators[1] = {74, 1, 30, 60, 0, 12};
  b.actuators[2] = {76, 2, 30, 60, 0, 12};
  std::strcpy(b.id, "xylophone-m1");
  std::strcpy(b.name, "MetroDom xylophone");
  return b;
}

std::vector<uint8_t> Encode(const Body& b) {
  std::vector<uint8_t> out(kBodyMaxBytes);
  out.resize(encodeBody(b, out.data(), out.size()));
  return out;
}

std::string Hex(const std::vector<uint8_t>& bytes) {
  std::string text;
  char pair[3];
  for (uint8_t b : bytes) {
    std::snprintf(pair, sizeof pair, "%02X", b);
    text += pair;
  }
  return text;
}

void body_RoundTrips() {
  Body m1 = M1();
  auto bytes = Encode(m1);
  CHECK(bytes.size() == 20 + 3 * 10 + 12 + 18 + 4 + 3 * 5 + 2);
  Body back;
  CHECK(parseBody(bytes.data(), bytes.size(), back) == BodyError::None);
  CHECK(back.driver == BodyDriver::Pca9685 && back.address == 0x40 && back.count == 3);
  CHECK(back.actuators[2].note == 76 && back.actuators[2].output == 2 && back.actuators[2].minPulseMs == 12);
  CHECK(std::string(back.name) == "MetroDom xylophone");
  CHECK(Encode(back) == bytes);

  // An EEPROM read is longer than the record: the rest is ignored.
  bytes.resize(kBodyMaxBytes, 0xFF);
  CHECK(parseBody(bytes.data(), bytes.size(), back) == BodyError::None);
}

// The M1 xylophone with accent LEDs (bodies/m1-xylophone-accent.json): three LEDs under each bar.
Body M1Accent() {
  Body b = M1();
  std::strcpy(b.id, "xylophone-m1-accent");
  b.ledCount = 9;
  b.ledBrightnessPercent = 20;
  b.flashMs = 80;
  const uint8_t colors[3][3] = {{0xFF, 0x40, 0x00}, {0xFF, 0xC0, 0x00}, {0x00, 0x80, 0xFF}};
  for (uint8_t i = 0; i < 3; ++i) {
    b.actuators[i].ledFirst = static_cast<uint8_t>(i * 3);
    b.actuators[i].ledCount = 3;
    std::memcpy(b.actuators[i].color, colors[i], 3);
  }
  return b;
}

std::string ReadFixture(const char* path) {
  std::ifstream file(path);
  std::stringstream text;
  text << file.rdbuf();
  std::string committed = text.str();
  while (!committed.empty() && (committed.back() == '\n' || committed.back() == '\r')) committed.pop_back();
  return committed;
}

void accent_RoundTrips_AndMatchesTheFixture() {
  auto bytes = Encode(M1Accent());
  CHECK(bytes.size() == 20 + 3 * 10 + 19 + 18 + 4 + 3 * 5 + 2);
  Body back;
  CHECK(parseBody(bytes.data(), bytes.size(), back) == BodyError::None);
  CHECK(back.ledCount == 9 && back.ledBrightnessPercent == 20 && back.flashMs == 80);
  CHECK(back.actuators[2].ledFirst == 6 && back.actuators[2].ledCount == 3 && back.actuators[2].color[2] == 0xFF);
  CHECK(Encode(back) == bytes);
  CHECK(Encode(M1()).size() == 20 + 3 * 10 + 12 + 18 + 4 + 3 * 5 + 2);  // no LEDs: the block is zeros

  std::string expected = Hex(bytes);
  std::string committed = ReadFixture(BODY_ACCENT_FIXTURE);
  if (committed != expected) std::printf("  expected m1-xylophone-accent.hex to be:\n%s\n", expected.c_str());
  CHECK(committed == expected);

  // The largest record still fits the brain's buffer.
  Body big = M1Accent();
  big.count = kMaxActuators;
  big.ledCount = kMaxLeds;
  big.polyphony = 1;
  for (uint8_t i = 0; i < kMaxActuators; ++i) {
    big.actuators[i] = {static_cast<uint8_t>(40 + i), i, 30, 60, 0, 12};
    big.actuators[i].ledFirst = static_cast<uint8_t>(i * 4);
    big.actuators[i].ledCount = 4;
    big.actuators[i].color[0] = 1;
  }
  std::memset(big.id, 'a', kBodyMaxId);
  std::memset(big.name, 'N', kBodyMaxName);
  CHECK(Encode(big).size() == kBodyMaxBytes);
}

void accent_BodyRefusesWhatItCantShow() {
  auto refused = [](void (*change)(Body&)) {
    Body b = M1Accent();
    change(b);
    return validateBody(b) == BodyError::Leds;
  };
  CHECK(refused([](Body& b) { b.ledBrightnessPercent = kCeilingLedPercent + 1; }));
  CHECK(refused([](Body& b) { b.ledBrightnessPercent = 0; }));
  CHECK(refused([](Body& b) { b.flashMs = 9; }));
  CHECK(refused([](Body& b) { b.flashMs = 1001; }));
  CHECK(refused([](Body& b) { b.ledCount = kMaxLeds + 1; }));
  CHECK(refused([](Body& b) { b.actuators[2].ledFirst = 7; }));              // runs off the strip
  CHECK(refused([](Body& b) { b.actuators[1].ledFirst = 2; }));              // overlaps the first bar
  CHECK(refused([](Body& b) { b.actuators[1].ledCount = 0; }));              // no LEDs but a color
  CHECK(refused([](Body& b) { b.ledCount = 0; }));                           // notes with LEDs, no strip
  CHECK(refused([](Body& b) { b.ledCount = 0; for (auto& a : b.actuators) a = Actuator{a.note, a.output, a.pulseMs, a.minIntervalMs, 0, a.minPulseMs}; b.flashMs = 80; }));
  Body fine = M1Accent();
  fine.actuators[1].ledCount = 0;
  fine.actuators[1].ledFirst = 0;
  std::memset(fine.actuators[1].color, 0, 3);
  CHECK(validateBody(fine) == BodyError::None);  // a note may have no LEDs

  // GPIO 7 is the strip's data pin, but only on a body with a strip.
  Body gpio = M1();
  gpio.driver = BodyDriver::Gpio;
  gpio.address = 0;
  gpio.actuators[0].output = 7;
  CHECK(validateBody(gpio) == BodyError::None);
  gpio.ledCount = 9;
  gpio.ledBrightnessPercent = 20;
  gpio.flashMs = 80;
  CHECK(validateBody(gpio) == BodyError::Output);

  // No strip, but the notes still name LEDs.
  auto bytes = Encode(M1Accent());
  bytes[20 + 3 * 10 + 19 + 18] = 0;  // ledCount
  uint16_t crc = crc16(bytes.data(), bytes.size() - 2);
  bytes[bytes.size() - 2] = static_cast<uint8_t>(crc & 0xFF);
  bytes[bytes.size() - 1] = static_cast<uint8_t>(crc >> 8);
  Body back;
  CHECK(parseBody(bytes.data(), bytes.size(), back) == BodyError::Leds);
  CHECK(std::string(bodyErrorText(BodyError::Leds)) == "bad accent LEDs");
}

void accent_StrikeFlashesItsLeds_ForFlashMs() {
  Body body = M1Accent();
  Rig r(body.config(), true, true);
  CHECK(r.leds.dark() && r.leds.shows == 1);  // begin() clears the strip
  r.on(74, 1000, 0, 127);
  // #FFC000 at 20%: 51, 38, 0.
  for (int led = 3; led < 6; ++led) {
    CHECK(r.leds.shown[led][0] == 51 && r.leds.shown[led][1] == 38 && r.leds.shown[led][2] == 0);
  }
  CHECK(r.leds.shown[0][0] == 0 && r.leds.shown[6][2] == 0);
  CHECK(r.brain.isLit(1));
  r.brain.tick(1079);
  CHECK(r.brain.isLit(1) && r.leds.shown[3][0] == 51);
  r.brain.tick(1080);
  CHECK(!r.brain.isLit(1) && r.leds.dark());

  // Softer is dimmer, but never dark.
  r.on(72, 2000, 0, 1);
  CHECK(r.leds.shown[0][0] == 1 && r.leds.shown[0][1] == 1 && r.leds.shown[0][2] == 0);
}

void accent_RefusedNotesDontFlash_AndPanicDarkens() {
  Body body = M1Accent();
  Rig r(body.config(), true, true);
  r.on(72, 1000);
  r.brain.tick(1035);
  r.on(72, 1040);  // too soon: refused
  CHECK(r.brain.lastRefusal() == Refusal::Interval);
  r.brain.tick(1080);
  CHECK(r.leds.dark());  // the refused repeat didn't relight it

  r.on(74, 2000);
  r.send({0xB5, 120, 0}, 2001);
  CHECK(r.leds.dark() && !r.brain.isLit(1));
  r.on(76, 3000);
  r.brain.hostConnected(false);
  CHECK(r.leds.dark());
  r.brain.hostConnected(true);
  r.on(76, 3100);
  r.raw({0xF0, 0x7D, 0x4D, 0x44, 0x03, 0xF7}, 3101);
  CHECK(r.leds.dark());
}

void accent_CeilingsAndRanges_AreClamped() {
  Actuator wide[2] = {{60, 2, 30, 60, 0}, {64, 3, 30, 60, 0}};
  wide[0].ledFirst = 0;
  wide[0].ledCount = 200;  // past the strip: trimmed to it
  wide[0].color[0] = 255;
  wide[1].ledFirst = 10;   // starts past the strip: no LEDs
  wide[1].ledCount = 2;
  wide[1].color[0] = 255;
  BrainConfig c;
  c.id = "x";
  c.name = "x";
  c.actuators = wide;
  c.actuatorCount = 2;
  c.ledCount = 8;
  c.ledBrightnessPercent = 100;  // over the ceiling
  c.flashMs = 1;
  Rig r(c, true, true);
  CHECK(r.brain.ledCount() == 8 && r.brain.ledBrightnessPercent() == kCeilingLedPercent);
  r.on(60, 1000, 0, 127);
  CHECK(r.leds.shown[7][0] == 255 * kCeilingLedPercent / 100);
  r.brain.tick(1009);
  CHECK(!r.leds.dark());  // flash is at least 10 ms
  r.brain.tick(1010);
  CHECK(r.leds.dark());
  int shows = r.leds.shows;
  r.on(64, 2000);
  CHECK(r.leds.shows == shows && !r.brain.isLit(1));
  std::string json(r.brain.profileJson(), r.brain.profileLength());
  CHECK(json.find("\"accent\":{\"leds\":8,\"maxBrightnessPercent\":25,\"flashMs\":10}") != std::string::npos);
  CHECK(json.find("\"led\":{\"first\":0,\"count\":8,\"color\":\"#FF0000\"}") != std::string::npos);

  // No strip wired: no LEDs, whatever the config says, and the profile doesn't claim any.
  Rig none(c, true, false);
  CHECK(none.brain.ledCount() == 0);
  none.on(60, 1000);
  std::string plain(none.brain.profileJson(), none.brain.profileLength());
  CHECK(plain.find("accent") == std::string::npos && plain.find("\"led\"") == std::string::npos);
}

void accent_LargestProfileFits() {
  Actuator many[kMaxActuators];
  for (uint8_t i = 0; i < kMaxActuators; ++i) {
    many[i] = {static_cast<uint8_t>(100 + i), i, 250, 10000, 1000, 249};
    many[i].ledFirst = static_cast<uint8_t>(i * 4);
    many[i].ledCount = 4;
    many[i].color[0] = 0xAB;
  }
  BrainConfig c;
  c.id = "abcdefghijklmnopqrstuvwxyz012345";
  c.name = "NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNN";
  c.actuators = many;
  c.actuatorCount = kMaxActuators;
  c.channel = 15;
  c.maxOnMs = 250;
  c.ledCount = kMaxLeds;
  c.ledBrightnessPercent = 25;
  c.flashMs = 1000;
  Rig r(c, true, true);
  CHECK(r.brain.profileLength() > 0);
}

void body_MatchesTheCommittedFixture() {
  // The Conductor's encoder (BodyImage) must produce the same bytes from bodies/m1-xylophone.json.
  std::ifstream file(BODY_FIXTURE);
  std::stringstream text;
  text << file.rdbuf();
  std::string committed = text.str();
  while (!committed.empty() && (committed.back() == '\n' || committed.back() == '\r')) committed.pop_back();
  std::string expected = Hex(Encode(M1()));
  if (committed != expected) std::printf("  expected m1-xylophone.hex to be:\n%s\n", expected.c_str());
  CHECK(committed == expected);
}

BodyError Broken(void (*change)(Body&)) {
  Body b = M1();
  change(b);
  std::vector<uint8_t> bytes(kBodyMaxBytes);
  CHECK(encodeBody(b, bytes.data(), bytes.size()) == 0);  // the encoder refuses it too
  return validateBody(b);
}

void body_RefusesWhatItCantSafelyBe() {
  CHECK(Broken([](Body& b) { b.address = 0x20; }) == BodyError::Driver);
  CHECK(Broken([](Body& b) { b.driver = static_cast<BodyDriver>(7); }) == BodyError::Driver);
  CHECK(Broken([](Body& b) { b.count = 0; }) == BodyError::Count);
  CHECK(Broken([](Body& b) { b.count = 17; }) == BodyError::Count);
  CHECK(Broken([](Body& b) { b.maxOnMs = kCeilingOnMs + 1; }) == BodyError::Limits);
  CHECK(Broken([](Body& b) { b.maxDutyPercent = kCeilingDutyPercent + 1; }) == BodyError::Limits);
  CHECK(Broken([](Body& b) { b.channel = 16; }) == BodyError::Limits);
  CHECK(Broken([](Body& b) { b.polyphony = 0; }) == BodyError::Limits);
  CHECK(Broken([](Body& b) { b.watchdogMs = 9000; }) == BodyError::Limits);
  CHECK(Broken([](Body& b) { b.actuators[1].note = 72; }) == BodyError::Note);
  CHECK(Broken([](Body& b) { b.actuators[1].note = 128; }) == BodyError::Note);
  CHECK(Broken([](Body& b) { b.actuators[1].output = 0; }) == BodyError::Output);
  CHECK(Broken([](Body& b) { b.actuators[1].output = 16; }) == BodyError::Output);
  CHECK(Broken([](Body& b) { b.actuators[0].pulseMs = 46; }) == BodyError::Pulse);
  CHECK(Broken([](Body& b) { b.actuators[0].minPulseMs = 31; }) == BodyError::Pulse);
  CHECK(Broken([](Body& b) { b.actuators[0].minIntervalMs = 29; }) == BodyError::Interval);
  CHECK(Broken([](Body& b) { b.actuators[0].latencyMs = 1001; }) == BodyError::Interval);
  CHECK(Broken([](Body& b) { std::strcpy(b.id, "Xylo"); }) == BodyError::Text);
  CHECK(Broken([](Body& b) { b.name[0] = '\0'; }) == BodyError::Text);

  // GPIO bodies: pins 0-28, no address.
  Body gpio = M1();
  gpio.driver = BodyDriver::Gpio;
  gpio.address = 0;
  gpio.actuators[2].output = 28;
  CHECK(validateBody(gpio) == BodyError::None);
  gpio.actuators[2].output = 29;
  CHECK(validateBody(gpio) == BodyError::Output);
  for (uint8_t pin : {kPinSda, kPinScl, kPinOutputEnable}) {  // the backplane's I2C and /OE
    gpio.actuators[2].output = pin;
    CHECK(validateBody(gpio) == BodyError::Output);
  }
}

void body_ParseSaysWhatsWrong() {
  auto good = Encode(M1());
  Body b;
  std::vector<uint8_t> blank(64, 0xFF);
  CHECK(parseBody(blank.data(), blank.size(), b) == BodyError::Empty);
  auto bad = good;
  bad[0] = 'X';
  CHECK(parseBody(bad.data(), bad.size(), b) == BodyError::Magic);
  bad = good;
  bad[3] = 3;
  CHECK(parseBody(bad.data(), bad.size(), b) == BodyError::Version);
  bad = good;
  bad[25] ^= 1;  // a pulse byte, so only the CRC catches it
  CHECK(parseBody(bad.data(), bad.size(), b) == BodyError::Crc);
  CHECK(parseBody(good.data(), good.size() - 1, b) == BodyError::Length);
  CHECK(std::string(bodyErrorText(BodyError::Crc)) == "bad CRC");
}

void body_ConfigIsWhatTheBrainRuns() {
  Body m1 = M1();
  BrainConfig c = m1.config();
  FakePins pins;
  FakeReplies replies;
  Brain brain(c, pins, replies);
  brain.begin(0);
  std::string json = brain.profileJson();
  CHECK(json.find("\"id\":\"xylophone-m1\",\"name\":\"MetroDom xylophone\"") != std::string::npos);
  CHECK(json.find("{\"note\":76,\"minIntervalMs\":60,\"minPulseMs\":12,\"pulseMs\":30}") != std::string::npos);
  CHECK(json.find("\"polyphony\":2") != std::string::npos);
}

// The Body Write chunks `metrodom body write` sends: hex text, 128 characters a chunk.
std::vector<std::vector<uint8_t>> WriteChunks(const std::vector<uint8_t>& bytes) {
  std::string hex = Hex(bytes);
  size_t count = (hex.size() + 127) / 128;
  std::vector<std::vector<uint8_t>> chunks;
  for (size_t i = 0; i < count; ++i) {
    std::vector<uint8_t> m = {0xF0, 0x7D, 0x4D, 0x44, 0x04, uint8_t(i), uint8_t(count)};
    for (char ch : hex.substr(i * 128, 128)) m.push_back(static_cast<uint8_t>(ch));
    m.push_back(0xF7);
    chunks.push_back(m);
  }
  return chunks;
}

std::vector<uint8_t> WriteReply(uint8_t status, uint8_t detail = 0) {
  return {0xF0, 0x7D, 0x4D, 0x44, 0x05, status, detail, 0xF7};
}

void bodyWrite_StoresTheRecord_ThenRestarts() {
  Rig r;
  r.on(60, 0);
  auto bytes = Encode(M1());
  auto chunks = WriteChunks(bytes);
  CHECK(chunks.size() == 2);
  for (auto& c : chunks) r.raw(c);
  CHECK(r.store.written == bytes);
  CHECK(r.replies.sent.size() == 1 && r.replies.sent[0] == WriteReply(0));
  CHECK(r.brain.restartRequested());
  CHECK(!r.pins.level[2]);  // everything off before the write
  r.on(64, 500);
  CHECK(r.brain.lastRefusal() == Refusal::Restarting);
}

void bodyWrite_RefusesBadRecords_AndKeepsPlaying() {
  Rig r;
  auto bytes = Encode(M1());
  bytes[25] ^= 1;
  for (auto& c : WriteChunks(bytes)) r.raw(c);
  CHECK(r.replies.sent.back() == WriteReply(2, static_cast<uint8_t>(BodyError::Crc)));
  CHECK(r.store.written.empty());
  CHECK(!r.brain.restartRequested());
  r.on(60, 0);
  CHECK(r.pins.level[2]);
}

void bodyWrite_RefusesChunksOutOfOrder_OrNotHex() {
  Rig r;
  auto chunks = WriteChunks(Encode(M1()));
  r.raw(chunks[1]);
  CHECK(r.replies.sent.back() == WriteReply(1));
  auto notHex = chunks[0];
  notHex[9] = 'G';
  r.raw(notHex);
  CHECK(r.replies.sent.back() == WriteReply(1));
  r.raw({0xF0, 0x7D, 0x4D, 0x44, 0x04, 0xF7});
  CHECK(r.replies.sent.back() == WriteReply(1));
  // Starting over from chunk 0 works.
  for (auto& c : chunks) r.raw(c);
  CHECK(r.replies.sent.back() == WriteReply(0));
}

void bodyWrite_SaysWhenItCant() {
  Rig noStore(Three(), false);
  for (auto& c : WriteChunks(Encode(M1()))) noStore.raw(c);
  CHECK(noStore.replies.sent.back() == WriteReply(4));

  Rig failing;
  failing.store.works = false;  // write-protected, or the read-back differed
  for (auto& c : WriteChunks(Encode(M1()))) failing.raw(c);
  CHECK(failing.replies.sent.back() == WriteReply(3));
  CHECK(!failing.brain.restartRequested());
  failing.on(60, 0);
  CHECK(failing.pins.level[2]);
}

// An I2C bus with a PCA9685 at 0x40 and a 24LC32 at 0x50.
struct FakeBus : I2c {
  uint8_t pca[256] = {};
  uint8_t eeprom[4096];
  uint16_t pointer = 0;
  bool eepromThere = true, protect = false, pcaThere = true;
  int busy = 0;  // NACKs left while a page write finishes
  std::vector<std::vector<uint8_t>> pcaWrites;
  FakeBus() {
    std::memset(eeprom, 0xFF, sizeof eeprom);
    for (int ch = 0; ch < 16; ++ch) pca[0x06 + 4 * ch + 3] = 0x10;  // power-on: full off
  }
  bool write(uint8_t address, const uint8_t* data, size_t length) override {
    if (address == 0x40 && pcaThere) {
      pcaWrites.emplace_back(data, data + length);
      for (size_t i = 1; i < length; ++i) pca[(data[0] + i - 1) & 0xFF] = data[i];
      return true;
    }
    if (address == 0x50 && eepromThere) {
      if (busy > 0) {
        --busy;
        return false;
      }
      if (length < 2) return false;
      pointer = static_cast<uint16_t>((data[0] << 8) | data[1]);
      if (length > 2) {
        for (size_t i = 2; i < length; ++i) {
          // Within the page: the address wraps at the page end, like the real chip.
          uint16_t at = static_cast<uint16_t>((pointer & ~31u) | ((pointer + i - 2) & 31u));
          if (!protect) eeprom[at & 4095] = data[i];
        }
        busy = 2;
      }
      return true;
    }
    return false;
  }
  bool read(uint8_t address, uint8_t* data, size_t length) override {
    if (address != 0x50 || !eepromThere || busy > 0) return false;
    for (size_t i = 0; i < length; ++i) data[i] = eeprom[(pointer + i) & 4095];
    pointer = static_cast<uint16_t>(pointer + length);
    return true;
  }
  bool fullOn(int ch) const { return (pca[0x06 + 4 * ch + 1] & 0x10) && !(pca[0x06 + 4 * ch + 3] & 0x10); }
};

void noWait(uint32_t) {}

void pca9685_DrivesFullOnAndFullOff() {
  FakeBus bus;
  Pca9685 pca(bus, 0x40);
  CHECK(pca.begin());
  CHECK(bus.pca[0x00] == 0x20);  // auto-increment, awake
  CHECK(bus.pca[0x01] == 0x04);  // totem pole
  CHECK(pca.set(3, true));
  CHECK(bus.fullOn(3));
  CHECK((bus.pcaWrites.back() == std::vector<uint8_t>{0x12, 0, 0x10, 0, 0}));
  CHECK(pca.set(3, false));
  CHECK(!bus.fullOn(3));
  CHECK(pca.set(5, true));
  CHECK(pca.allOff());
  CHECK(bus.pca[0xFD] == 0x10);
  CHECK(!pca.set(16, true));
  bus.pcaThere = false;
  CHECK(!pca.set(3, false));  // the brain sees the failure and keeps retrying
}

void pca9685_UnderTheBrain_FaultsWhenTheChipGoesAway() {
  FakeBus bus;
  Pca9685 pca(bus, 0x40);
  pca.begin();
  Body m1 = M1();
  BrainConfig c = m1.config();
  FakeReplies replies;
  Brain brain(c, pca, replies);
  brain.begin(0);
  uint8_t on[] = {0x90, 74, 100};
  brain.message(on, 3, 0);
  CHECK(bus.fullOn(1));
  brain.tick(30);
  CHECK(!bus.fullOn(1));
  brain.message(on, 3, 100);
  bus.pcaThere = false;  // Qwiic cable pulled mid-strike
  brain.tick(130);
  CHECK(brain.faulted());
  bus.pcaThere = true;
  brain.tick(131);
  CHECK(!brain.faulted());
  CHECK(!bus.fullOn(1));
}

void eeprom_WritesPages_AndVerifies() {
  FakeBus bus;
  Eeprom24 eeprom(bus, noWait);
  CHECK(eeprom.present());
  auto bytes = Encode(M1());
  CHECK(eeprom.write(bytes.data(), bytes.size()));
  std::vector<uint8_t> back(kBodyMaxBytes);
  CHECK(eeprom.read(0, back.data(), back.size()));
  Body b;
  CHECK(parseBody(back.data(), back.size(), b) == BodyError::None);
  CHECK(std::string(b.id) == "xylophone-m1");

  bus.protect = true;  // write button not held (WP high): acks, keeps the old bytes
  Body other = M1();
  std::strcpy(other.name, "Other");
  auto otherBytes = Encode(other);
  CHECK(!eeprom.write(otherBytes.data(), otherBytes.size()));

  bus.eepromThere = false;
  CHECK(!eeprom.present());
}

void outputEnable_OnlyWhileTheBrainIsHealthy() {
  Rig r;
  CHECK(r.pins.enables.size() == 2 && !r.pins.enables[0] && r.pins.enables[1]);  // off, outputs off, then on
  CHECK(r.pins.writes.size() == 3);  // the offs happened while disabled

  r.brain.hostConnected(false);
  CHECK(!r.pins.enabled);
  r.brain.hostConnected(true);
  CHECK(r.pins.enabled);

  r.on(60, 0);
  r.pins.broken[2] = true;  // the PCA9685 stops answering mid-strike
  r.brain.tick(30);
  CHECK(!r.pins.enabled);   // /OE cuts it at once, bus or no bus
  r.brain.tick(31);
  CHECK(!r.pins.enabled);
  r.pins.broken[2] = false;
  r.brain.tick(32);
  CHECK(r.pins.enabled);
  size_t changes = r.pins.enables.size();
  r.brain.tick(33);
  CHECK(r.pins.enables.size() == changes);  // written on change only

  r.store.works = false;
  for (auto& c : WriteChunks(Encode(M1()))) r.raw(c);
  CHECK(r.pins.enabled);  // a failed write keeps the old body playing
  r.store.works = true;
  for (auto& c : WriteChunks(Encode(M1()))) r.raw(c);
  CHECK(!r.pins.enabled);  // written: disabled until the restart
}

bool oePin = false;  // true = outputs enabled (/OE low)
void setOe(bool on) { oePin = on; }

void pca9685_OutputEnable_DrivesOe_EvenWithTheChipGone() {
  FakeBus bus;
  Pca9685 pca(bus, 0x40, setOe);
  pca.begin();
  Body m1 = M1();
  BrainConfig c = m1.config();
  FakeReplies replies;
  Brain brain(c, pca, replies);
  brain.begin(0);
  CHECK(oePin);
  uint8_t on[] = {0x90, 72, 100};
  brain.message(on, 3, 0);
  bus.pcaThere = false;
  brain.tick(30);
  CHECK(!oePin);
  bus.pcaThere = true;
  brain.tick(31);
  CHECK(oePin);

  Pca9685 unwired(bus, 0x40);
  unwired.enable(false);  // no /OE: a no-op, not a crash
}

void bodyRead_SendsTheRecordItBootedFrom() {
  Rig r;
  r.raw({0xF0, 0x7D, 0x4D, 0x44, 0x06, 0xF7});
  CHECK(r.replies.sent.size() == 1);
  CHECK((r.replies.sent[0] == std::vector<uint8_t>{0xF0, 0x7D, 0x4D, 0x44, 0x07, 0, 0, 0xF7}));  // bench: no record

  auto bytes = Encode(M1());
  r.brain.setBodyRecord(bytes.data(), bytes.size());
  r.replies.sent.clear();
  r.raw({0xF0, 0x7D, 0x4D, 0x44, 0x06, 0xF7});
  std::string hex;
  for (size_t i = 0; i < r.replies.sent.size(); ++i) {
    auto& c = r.replies.sent[i];
    CHECK(c[4] == 0x07 && c[5] == i && c[6] == r.replies.sent.size() && c.back() == 0xF7);
    hex.append(c.begin() + 7, c.end() - 1);
  }
  CHECK(r.replies.sent.size() == 2);
  CHECK(hex == Hex(bytes));
}

}  // namespace

int main() {
  void (*tests[])() = {
      begin_TurnsEveryOutputOff, noteOn_StrikesAFixedPulse_WhateverTheNoteOff,
      clamps_PulseToMaxOn_AndIntervalToThePulse, minInterval_RefusesRepeatsThatComeTooSoon,
      stillStriking_IsRefused, duty_RefusesWhenTheThermalBudgetIsSpent, duty_SustainedRateMatchesThePercentage,
      polyphony_LimitsActuatorsOnAtOnce, panic_TurnsEverythingOff_OnAnyChannel, otherControllers_DontPanic,
      metroDomAllOff_TurnsEverythingOff_AndNotesStillPlayAfter, otherSysEx_DoesntPanic,
      hostLost_TurnsOff_AndRefusesUntilBack, channel_OmniByDefault_OrOnlyItsOwn,
      unknownNotes_AndVelocityZero_DoNothing, identityRequest_GetsMetroDomsReply,
      profileRequest_SendsTheProfileInChunks, profile_DescribesWhatTheBrainEnforces,
      benchProfile_MatchesTheCommittedFile, timeWrap_IsHandled,
      velocity_ScalesThePulse_BetweenMinPulseAndPulse, velocity_SoftStrikesSpendLessDuty,
      ceilings_NoConfigCanRaiseThem, offThatFails_BlocksEveryStrike_UntilItTakes,
      onThatFails_IsRefused_AndTurnedOff, noActuators_IsASilentBrain_ThatStillDescribesItself,
      body_RoundTrips, body_MatchesTheCommittedFixture, body_RefusesWhatItCantSafelyBe,
      body_ParseSaysWhatsWrong, body_ConfigIsWhatTheBrainRuns, bodyWrite_StoresTheRecord_ThenRestarts,
      bodyWrite_RefusesBadRecords_AndKeepsPlaying, bodyWrite_RefusesChunksOutOfOrder_OrNotHex,
      bodyWrite_SaysWhenItCant, pca9685_DrivesFullOnAndFullOff,
      pca9685_UnderTheBrain_FaultsWhenTheChipGoesAway, eeprom_WritesPages_AndVerifies,
      outputEnable_OnlyWhileTheBrainIsHealthy, pca9685_OutputEnable_DrivesOe_EvenWithTheChipGone,
      bodyRead_SendsTheRecordItBootedFrom, accent_RoundTrips_AndMatchesTheFixture,
      accent_BodyRefusesWhatItCantShow, accent_StrikeFlashesItsLeds_ForFlashMs,
      accent_RefusedNotesDontFlash_AndPanicDarkens, accent_CeilingsAndRanges_AreClamped, accent_LargestProfileFits,
  };
  for (auto test : tests) test();
  std::printf("brain: %d checks, %d failed\n", checks, failures);
  return failures == 0 ? 0 : 1;
}
