// MetroDom generic brain firmware: the platform-free core (ADR 0006).
//
// Everything that decides whether an actuator may move lives here, with no Arduino calls, so
// it builds and is tested on a laptop (test/brain_test.cpp). The sketch (metrodom_brain.ino)
// only moves bytes and pins: USB-MIDI in, SysEx out, GPIO, the hardware watchdog.
//
// Safety (docs/RELIABILITY.md, firmware layer), enforced here and never trusting the host:
// - All off at begin(), on panic (CC 120/123 on any channel, System Reset, MetroDom SysEx all-off)
//   and when USB goes away.
// - A strike is a fixed pulse, never longer than maxOnMs, however long the Note On lasts.
// - Per actuator: a minimum interval between strikes and a duty-cycle (thermal) budget.
//   Notes over either limit are refused, never queued.
// - At most `polyphony` actuators on at once.
#pragma once

#include <stddef.h>
#include <stdint.h>

namespace metrodom {

constexpr uint8_t kMaxActuators = 16;
constexpr uint8_t kOmni = 0xFF;
constexpr size_t kProfileBufferSize = 2048;

// MetroDom's MIDI identity (docs/references/midi-identity.md).
constexpr uint8_t kManufacturer = 0x7D;
constexpr uint8_t kFamilyLsb = 0x4D, kFamilyMsb = 0x44;  // "MD", family 8781
constexpr uint16_t kFamily = kFamilyLsb | (kFamilyMsb << 7);

// One solenoid (or other striker) and the note that fires it.
struct Actuator {
  uint8_t note;            // MIDI note 0-127
  uint8_t output;          // GPIO pin (or driver channel, later)
  uint16_t pulseMs;        // strike length; clamped to maxOnMs
  uint16_t minIntervalMs;  // fastest repeat; never less than the pulse
  uint16_t latencyMs;      // measured Note On -> sound, 0 if not measured yet
};

// What one body is. Today compiled in (config.h); later read from the body's EEPROM.
struct BrainConfig {
  const char* id;                 // e.g. "bench-solenoid" (ASCII)
  const char* name;               // e.g. "Bench solenoid" (ASCII)
  const Actuator* actuators;
  uint8_t actuatorCount;          // 1-kMaxActuators
  uint8_t channel = kOmni;        // 0-15 to listen on one channel; omni by default (one port per instrument)
  uint8_t polyphony = 1;          // actuators on at once
  uint16_t maxOnMs = 45;          // hard limit on any pulse
  uint8_t maxDutyPercent = 30;    // on-time budget per actuator
  uint16_t dutyWindowMs = 1000;   // burst size: maxDutyPercent of this window
  uint16_t watchdogMs = 500;      // hardware watchdog: a hung loop restarts the brain (all off at boot)
  uint16_t model = 1;             // identity model: 1 = generic brain
  uint8_t version[4] = {0, 1, 0, 0};
};

// Pins (or a driver). set() must be fast and must not fail silently on "off".
class Outputs {
 public:
  virtual void set(uint8_t output, bool on) = 0;

 protected:
  ~Outputs() = default;
};

// Where SysEx replies go (USB-MIDI out). data includes F0 ... F7.
class Replies {
 public:
  virtual void sysex(const uint8_t* data, size_t length) = 0;

 protected:
  ~Replies() = default;
};

// Why the last Note On didn't strike.
enum class Refusal : uint8_t { None, NotMine, Busy, Interval, Duty, Polyphony, NoHost };

struct Counters {
  uint32_t struck = 0;
  uint32_t interval = 0;   // too soon after the last strike (or still striking)
  uint32_t duty = 0;       // over the thermal budget
  uint32_t polyphony = 0;  // too many on at once
  uint32_t noHost = 0;     // USB not connected
};

class Brain {
 public:
  Brain(const BrainConfig& config, Outputs& outputs, Replies& replies);

  // Everything off. Call first in setup(), before USB starts.
  void begin(uint32_t nowMs);

  // One complete MIDI message: a channel message, a System Reset, or SysEx including F0/F7.
  void message(const uint8_t* data, size_t length, uint32_t nowMs);

  // Ends pulses that are due. Call every loop.
  void tick(uint32_t nowMs);

  // USB mounted / unmounted. Losing the host turns everything off and refuses notes until it's back.
  void hostConnected(bool connected);

  // Panic: every actuator off now.
  void allOff();

  bool isOn(uint8_t index) const { return index < count_ && state_[index].on; }
  Refusal lastRefusal() const { return lastRefusal_; }
  const Counters& counters() const { return counters_; }

  // The instrument profile JSON (docs/references/instrument-profile.md) built from the config,
  // so what the brain says and what it enforces can't disagree.
  const char* profileJson() const { return profile_; }
  size_t profileLength() const { return profileLength_; }

  // Effective limits after clamping (for tests and the profile).
  uint16_t pulseMs(uint8_t index) const { return actuators_[index].pulseMs; }
  uint16_t minIntervalMs(uint8_t index) const { return actuators_[index].minIntervalMs; }

 private:
  struct State {
    bool on = false;
    bool struck = false;     // has struck since begin()
    uint32_t onAt = 0;
    uint32_t lastStrike = 0;
    uint32_t heat = 0;       // on-time budget used, in ms x 100
    uint32_t heatAt = 0;
  };

  void noteOn(uint8_t channel, uint8_t note, uint8_t velocity, uint32_t nowMs);
  void sysex(const uint8_t* data, size_t length);
  void sendIdentity();
  void sendProfile();
  void off(uint8_t index);
  void cool(State& s, uint32_t nowMs) const;
  void buildProfile();

  const BrainConfig& config_;
  Outputs& outputs_;
  Replies& replies_;
  Actuator actuators_[kMaxActuators] = {};
  State state_[kMaxActuators] = {};
  uint8_t count_ = 0;
  uint8_t polyphony_ = 1;
  bool host_ = true;
  Refusal lastRefusal_ = Refusal::None;
  Counters counters_;
  char profile_[kProfileBufferSize] = {};
  size_t profileLength_ = 0;
};

}  // namespace metrodom
