#include "Brain.h"

#include "Body.h"

#include <stdarg.h>
#include <stdio.h>

namespace metrodom {
namespace {

constexpr uint8_t kNoteOn = 0x90;
constexpr uint8_t kControlChange = 0xB0;
constexpr uint8_t kSystemReset = 0xFF;
constexpr uint8_t kAllSoundOff = 120;
constexpr uint8_t kAllNotesOff = 123;

// Universal Non-Real-Time, General Information (docs/references/midi-identity.md).
constexpr uint8_t kUniversalNonRealTime = 0x7E;
constexpr uint8_t kAllDevices = 0x7F;
constexpr uint8_t kGeneralInformation = 0x06;
constexpr uint8_t kIdentityRequest = 0x01;
constexpr uint8_t kIdentityReply = 0x02;

// MetroDom SysEx (docs/references/midi-profile-exchange.md).
constexpr uint8_t kProfileRequest = 0x01;
constexpr uint8_t kProfileReply = 0x02;
constexpr uint8_t kAllOff = 0x03;
constexpr uint8_t kBodyWrite = 0x04;
constexpr uint8_t kBodyWriteReply = 0x05;
constexpr size_t kBodyChunkHex = 128;
constexpr size_t kChunk = 128;

uint16_t clamp16(uint16_t value, uint16_t low, uint16_t high) {
  return value < low ? low : value > high ? high : value;
}

// Appends to a fixed buffer; remembers if anything didn't fit.
struct Writer {
  char* buffer;
  size_t size;
  size_t length = 0;
  bool overflow = false;

  void add(const char* format, ...) {
    if (overflow) return;
    va_list args;
    va_start(args, format);
    int n = vsnprintf(buffer + length, size - length, format, args);
    va_end(args);
    if (n < 0 || static_cast<size_t>(n) >= size - length) {
      overflow = true;
      buffer[length] = '\0';
      return;
    }
    length += static_cast<size_t>(n);
  }

  // A JSON string: quotes and backslashes escaped, control and non-ASCII bytes dropped.
  void text(const char* value) {
    add("\"");
    for (const char* p = value ? value : ""; *p && !overflow; ++p) {
      unsigned char c = static_cast<unsigned char>(*p);
      if (c == '"' || c == '\\') {
        add("\\%c", c);
      } else if (c >= 0x20 && c < 0x7F) {
        add("%c", c);
      }
    }
    add("\"");
  }
};

int hexDigit(uint8_t c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  return -1;
}

}  // namespace

Brain::Brain(const BrainConfig& config, Outputs& outputs, Replies& replies, BodyStore* store)
    : config_(config), outputs_(outputs), replies_(replies), store_(store) {
  count_ = config.actuators == nullptr ? 0 : config.actuatorCount > kMaxActuators ? kMaxActuators : config.actuatorCount;
  polyphony_ = config.polyphony == 0 ? 1 : config.polyphony;
  // Ceilings: whatever the body says, no pulse over kCeilingOnMs and no duty over kCeilingDutyPercent.
  maxOnMs_ = clamp16(config.maxOnMs, 1, kCeilingOnMs);
  maxDutyPercent_ = config.maxDutyPercent == 0 ? 1
                    : config.maxDutyPercent > kCeilingDutyPercent ? kCeilingDutyPercent
                                                                 : config.maxDutyPercent;
  for (uint8_t i = 0; i < count_; ++i) {
    Actuator a = config.actuators[i];
    a.note &= 0x7F;
    // The pulse never exceeds maxOnMs; a repeat never comes before the pulse has ended.
    a.pulseMs = clamp16(a.pulseMs, 1, maxOnMs_);
    if (a.minPulseMs >= a.pulseMs) a.minPulseMs = 0;  // no range: a fixed pulse
    if (a.minPulseMs > 0) velocity_ = true;
    if (a.minIntervalMs < a.pulseMs) a.minIntervalMs = a.pulseMs;
    actuators_[i] = a;
  }
  buildProfile();
}

void Brain::begin(uint32_t nowMs) {
  enabled_ = false;
  outputs_.enable(false);  // everything off while disabled, then enable
  for (uint8_t i = 0; i < count_; ++i) {
    state_[i] = State{};
    state_[i].heatAt = nowMs;
    off(i);
  }
  lastRefusal_ = Refusal::None;
  updateEnable();
}

void Brain::message(const uint8_t* data, size_t length, uint32_t nowMs) {
  if (data == nullptr || length == 0) return;
  uint8_t status = data[0];
  if (status == 0xF0) {
    sysex(data, length);
    return;
  }

  if (status == kSystemReset) {
    allOff();
    return;
  }

  if (length < 3 || status < 0x80 || status >= 0xF0) return;
  uint8_t type = status & 0xF0;
  uint8_t channel = status & 0x0F;
  if (type == kControlChange && (data[1] == kAllSoundOff || data[1] == kAllNotesOff)) {
    allOff();  // panic arrives on every channel; honor it on any
  } else if (type == kNoteOn && data[2] > 0) {
    noteOn(channel, data[1] & 0x7F, data[2], nowMs);
  }
  // Note Off and Note On velocity 0 change nothing: a strike is a fixed pulse.
}

void Brain::noteOn(uint8_t channel, uint8_t note, uint8_t velocity, uint32_t nowMs) {
  lastRefusal_ = Refusal::NotMine;
  if (config_.channel != kOmni && channel != config_.channel) return;

  uint8_t index = kMaxActuators;
  for (uint8_t i = 0; i < count_; ++i) {
    if (actuators_[i].note == note) {
      index = i;
      break;
    }
  }
  if (index == kMaxActuators) return;

  if (!host_) {
    lastRefusal_ = Refusal::NoHost;
    counters_.noHost++;
    return;
  }
  if (restart_) {
    lastRefusal_ = Refusal::Restarting;  // a new body is written: nothing plays until it boots
    return;
  }
  if (faulted()) {
    lastRefusal_ = Refusal::Fault;  // an output may be stuck on: strike nothing else
    return;
  }

  State& s = state_[index];
  const Actuator& a = actuators_[index];
  if (s.on || (s.struck && nowMs - s.lastStrike < a.minIntervalMs)) {
    lastRefusal_ = s.on ? Refusal::Busy : Refusal::Interval;
    counters_.interval++;
    return;
  }

  // Thermal budget: a leaky bucket that fills by each pulse and drains at maxDutyPercent.
  cool(s, nowMs);
  uint16_t pulse = pulseFor(index, velocity);
  uint32_t capacity = static_cast<uint32_t>(config_.dutyWindowMs) * maxDutyPercent_;
  uint32_t cost = static_cast<uint32_t>(pulse) * 100;
  if (s.heat + cost > capacity) {
    lastRefusal_ = Refusal::Duty;
    counters_.duty++;
    return;
  }

  uint8_t on = 0;
  for (uint8_t i = 0; i < count_; ++i) {
    if (state_[i].on) ++on;
  }
  if (on >= polyphony_) {
    lastRefusal_ = Refusal::Polyphony;
    counters_.polyphony++;
    return;
  }

  if (!outputs_.set(a.output, true)) {
    counters_.faults++;
    off(index);  // it may have turned on: make sure it's off, or keep retrying from tick()
    lastRefusal_ = Refusal::Fault;
    return;
  }
  s.heat += cost;
  s.on = true;
  s.struck = true;
  s.pulse = pulse;
  s.onAt = nowMs;
  s.lastStrike = nowMs;
  lastRefusal_ = Refusal::None;
  counters_.struck++;
}

void Brain::cool(State& s, uint32_t nowMs) const {
  uint32_t drained = (nowMs - s.heatAt) * maxDutyPercent_;
  s.heat = drained >= s.heat ? 0 : s.heat - drained;
  s.heatAt = nowMs;
}

void Brain::tick(uint32_t nowMs) {
  for (uint8_t i = 0; i < count_; ++i) {
    if ((state_[i].on && nowMs - state_[i].onAt >= state_[i].pulse) || state_[i].stuck) off(i);
  }
  updateEnable();
}

void Brain::updateEnable() {
  bool allowed = host_ && !restart_ && !faulted();
  if (allowed == enabled_) return;
  enabled_ = allowed;
  outputs_.enable(allowed);
}

bool Brain::faulted() const {
  for (uint8_t i = 0; i < count_; ++i) {
    if (state_[i].stuck) return true;
  }
  return false;
}

uint16_t Brain::pulseFor(uint8_t index, uint8_t velocity) const {
  const Actuator& a = actuators_[index];
  if (a.minPulseMs == 0) return a.pulseMs;
  uint32_t v = velocity < 1 ? 1 : velocity > 127 ? 127 : velocity;
  return static_cast<uint16_t>(a.minPulseMs + (static_cast<uint32_t>(a.pulseMs - a.minPulseMs) * (v - 1)) / 126);
}

void Brain::hostConnected(bool connected) {
  if (!connected) allOff();
  host_ = connected;
  updateEnable();
}

void Brain::allOff() {
  for (uint8_t i = 0; i < count_; ++i) off(i);
}

void Brain::off(uint8_t index) {
  state_[index].on = false;
  bool ok = outputs_.set(actuators_[index].output, false);  // always written, even if already off
  if (!ok) counters_.faults++;
  state_[index].stuck = !ok;
  if (!ok) updateEnable();  // cut every output in hardware now; tick() re-enables once the off takes
}

void Brain::sysex(const uint8_t* data, size_t length) {
  if (length < 3 || data[length - 1] != 0xF7) return;
  // F0 7E <dev> 06 01 F7: Identity Request, to everyone or to us.
  if (length == 6 && data[1] == kUniversalNonRealTime && data[3] == kGeneralInformation &&
      data[4] == kIdentityRequest && (data[2] == kAllDevices || data[2] == 0x00)) {
    sendIdentity();
    return;
  }

  if (length < 6 || data[1] != kManufacturer || data[2] != kFamilyLsb || data[3] != kFamilyMsb) return;
  if (data[4] == kBodyWrite) {
    bodyChunk(data, length);
    return;
  }
  if (length != 6) return;
  if (data[4] == kAllOff) {
    allOff();  // F0 7D 4D 44 03 F7: panic in one message
  } else if (data[4] == kProfileRequest) {
    sendProfile();  // F0 7D 4D 44 01 F7
  }
}

// F0 7D 4D 44 04 <index> <count> <hex...> F7: one chunk of a body record, in order from 0.
void Brain::bodyChunk(const uint8_t* data, size_t length) {
  if (length < 8) {
    replyWrite(WriteStatus::BadMessage);
    return;
  }
  uint8_t index = data[5], count = data[6];
  size_t hex = length - 8;
  if (index == 0) {
    bodyLength_ = 0;
    bodyNext_ = 0;
    bodyCount_ = count;
  }
  bool ok = count > 0 && count == bodyCount_ && index == bodyNext_ && hex % 2 == 0 && hex <= kBodyChunkHex &&
            bodyLength_ + hex / 2 <= kMaxBodyBytes;
  for (size_t i = 0; ok && i < hex; i += 2) {
    int high = hexDigit(data[7 + i]), low = hexDigit(data[8 + i]);
    ok = high >= 0 && low >= 0;
    if (ok) bodyIn_[bodyLength_++] = static_cast<uint8_t>((high << 4) | low);
  }
  if (!ok) {
    bodyNext_ = bodyCount_ = 0;
    replyWrite(WriteStatus::BadMessage);
    return;
  }
  if (++bodyNext_ < bodyCount_) return;

  bodyNext_ = bodyCount_ = 0;
  Body body;
  BodyError error = parseBody(bodyIn_, bodyLength_, body);
  if (error == BodyError::None && bodyLength_ != static_cast<size_t>(bodyIn_[4] | (bodyIn_[5] << 8))) {
    error = BodyError::Length;  // trailing bytes: the sender and the record disagree
  }
  if (error != BodyError::None) {
    replyWrite(WriteStatus::Invalid, static_cast<uint8_t>(error));
    return;
  }
  if (store_ == nullptr) {
    replyWrite(WriteStatus::NoStore);
    return;
  }

  // Nothing strikes from here: the brain restarts as the new body, or keeps the old one if the
  // write failed (a half-written EEPROM boots as a silent brain, never as a wrong instrument).
  allOff();
  restart_ = true;
  updateEnable();
  if (!store_->write(bodyIn_, bodyLength_)) {
    restart_ = false;
    updateEnable();
    replyWrite(WriteStatus::Failed);
    return;
  }
  replyWrite(WriteStatus::Written);
}

void Brain::replyWrite(WriteStatus status, uint8_t detail) {
  const uint8_t reply[] = {0xF0, kManufacturer, kFamilyLsb, kFamilyMsb, kBodyWriteReply,
                           static_cast<uint8_t>(status), static_cast<uint8_t>(detail & 0x7F), 0xF7};
  replies_.sysex(reply, sizeof reply);
}

void Brain::sendIdentity() {
  const uint8_t reply[] = {
      0xF0, kUniversalNonRealTime, kAllDevices, kGeneralInformation, kIdentityReply,
      kManufacturer, kFamilyLsb, kFamilyMsb,
      static_cast<uint8_t>(config_.model & 0x7F), static_cast<uint8_t>((config_.model >> 7) & 0x7F),
      static_cast<uint8_t>(config_.version[0] & 0x7F), static_cast<uint8_t>(config_.version[1] & 0x7F),
      static_cast<uint8_t>(config_.version[2] & 0x7F), static_cast<uint8_t>(config_.version[3] & 0x7F),
      0xF7};
  replies_.sysex(reply, sizeof reply);
}

void Brain::sendProfile() {
  if (profileLength_ == 0) return;
  size_t count = (profileLength_ + kChunk - 1) / kChunk;
  if (count > 127) return;
  uint8_t chunk[7 + kChunk + 1];
  for (size_t index = 0; index < count; ++index) {
    size_t start = index * kChunk;
    size_t n = profileLength_ - start < kChunk ? profileLength_ - start : kChunk;
    chunk[0] = 0xF0;
    chunk[1] = kManufacturer;
    chunk[2] = kFamilyLsb;
    chunk[3] = kFamilyMsb;
    chunk[4] = kProfileReply;
    chunk[5] = static_cast<uint8_t>(index);
    chunk[6] = static_cast<uint8_t>(count);
    for (size_t i = 0; i < n; ++i) chunk[7 + i] = static_cast<uint8_t>(profile_[start + i]) & 0x7F;
    chunk[7 + n] = 0xF7;
    replies_.sysex(chunk, 8 + n);
  }
}

void Brain::buildProfile() {
  Writer w{profile_, sizeof profile_};
  w.add("{\"schema\":\"metrodom.instrument-profile/0\",\"id\":");
  w.text(config_.id);
  w.add(",\"name\":");
  w.text(config_.name);
  w.add(",\"firmwareVersion\":\"%u.%u.%u\"", config_.version[0], config_.version[1], config_.version[2]);
  w.add(",\"midi\":{");
  if (config_.channel != kOmni) w.add("\"channel\":%u,", (config_.channel & 0x0F) + 1);
  w.add("\"identity\":{\"manufacturer\":\"7D\",\"family\":%u,\"model\":%u}}", kFamily, config_.model);
  w.add(",\"notes\":[");
  for (uint8_t i = 0; i < count_; ++i) {
    const Actuator& a = actuators_[i];
    w.add("%s{\"note\":%u", i == 0 ? "" : ",", a.note);
    if (a.latencyMs > 0) w.add(",\"latencyMs\":%u", a.latencyMs);
    w.add(",\"minIntervalMs\":%u", a.minIntervalMs);
    if (a.minPulseMs > 0) w.add(",\"minPulseMs\":%u", a.minPulseMs);
    w.add(",\"pulseMs\":%u}", a.pulseMs);
  }
  w.add("],\"polyphony\":%u,\"velocity\":{\"supported\":%s}", polyphony_, velocity_ ? "true" : "false");
  w.add(",\"safety\":{\"maxOnMs\":%u,\"maxDutyPercent\":%u,\"dutyWindowMs\":%u,\"watchdogMs\":%u}}",
        maxOnMs_, maxDutyPercent_, config_.dutyWindowMs, config_.watchdogMs);
  profileLength_ = w.overflow ? 0 : w.length;
}

}  // namespace metrodom
