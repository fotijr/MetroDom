// Host tests for the brain's core (no Arduino needed): make -C Instruments/metrodom_brain/test
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "../src/brain/Brain.h"

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
  std::vector<std::pair<uint8_t, bool>> writes;
  void set(uint8_t output, bool on) override {
    level[output] = on;
    writes.emplace_back(output, on);
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
  Brain brain;
  explicit Rig(BrainConfig c = Three()) : config(c), brain(config, pins, replies) { brain.begin(0); }
  void send(std::initializer_list<uint8_t> bytes, uint32_t now) {
    std::vector<uint8_t> m(bytes);
    brain.message(m.data(), m.size(), now);
  }
  void on(uint8_t note, uint32_t now, uint8_t channel = 0) { send({uint8_t(0x90 | channel), note, 100}, now); }
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
  CHECK(json.find("{\"note\":60,\"latencyMs\":9,\"minIntervalMs\":60}") != std::string::npos);
  CHECK(json.find("{\"note\":67,\"minIntervalMs\":45}") != std::string::npos);  // clamped, unmeasured
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

}  // namespace

int main() {
  void (*tests[])() = {
      begin_TurnsEveryOutputOff, noteOn_StrikesAFixedPulse_WhateverTheNoteOff,
      clamps_PulseToMaxOn_AndIntervalToThePulse, minInterval_RefusesRepeatsThatComeTooSoon,
      stillStriking_IsRefused, duty_RefusesWhenTheThermalBudgetIsSpent, duty_SustainedRateMatchesThePercentage,
      polyphony_LimitsActuatorsOnAtOnce, panic_TurnsEverythingOff_OnAnyChannel, otherControllers_DontPanic,
      hostLost_TurnsOff_AndRefusesUntilBack, channel_OmniByDefault_OrOnlyItsOwn,
      unknownNotes_AndVelocityZero_DoNothing, identityRequest_GetsMetroDomsReply,
      profileRequest_SendsTheProfileInChunks, profile_DescribesWhatTheBrainEnforces,
      benchProfile_MatchesTheCommittedFile, timeWrap_IsHandled,
  };
  for (auto test : tests) test();
  std::printf("brain: %d checks, %d failed\n", checks, failures);
  return failures == 0 ? 0 : 1;
}
