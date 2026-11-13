#include "Body.h"

#include <string.h>

namespace metrodom {
namespace {

constexpr uint8_t kGpioOutputs = 29;  // Pico GPIO 0-28

// The backplane's pins (hardware/modules/body-backplane): Qwiic SDA/SCL and the PCA9685's /OE.
bool reserved(uint8_t pin, bool leds) {
  return pin == kPinSda || pin == kPinScl || pin == kPinOutputEnable || (leds && pin == kPinLeds);
}
constexpr uint8_t kPcaOutputs = 16;

uint16_t get16(const uint8_t* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }

void put16(uint8_t* p, uint16_t value) {
  p[0] = static_cast<uint8_t>(value & 0xFF);
  p[1] = static_cast<uint8_t>(value >> 8);
}

bool idChar(char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'; }

bool nameChar(char c) { return c >= 0x20 && c < 0x7F; }

bool textOk(const char* text, size_t max, bool (*allowed)(char)) {
  size_t n = strnlen(text, max + 1);
  if (n == 0 || n > max) return false;
  for (size_t i = 0; i < n; ++i) {
    if (!allowed(text[i])) return false;
  }
  return true;
}

BodyError validateLeds(const Body& b) {
  if (b.ledCount == 0) {
    // No strip: the rest of the block is zero, so a record has one spelling.
    if (b.ledBrightnessPercent != 0 || b.flashMs != 0) return BodyError::Leds;
    for (uint8_t i = 0; i < b.count; ++i) {
      const Actuator& a = b.actuators[i];
      if (a.ledFirst != 0 || a.ledCount != 0 || a.color[0] != 0 || a.color[1] != 0 || a.color[2] != 0) return BodyError::Leds;
    }
    return BodyError::None;
  }
  if (b.ledCount > kMaxLeds || b.ledBrightnessPercent < 1 || b.ledBrightnessPercent > kCeilingLedPercent ||
      b.flashMs < 10 || b.flashMs > 1000) {
    return BodyError::Leds;
  }
  for (uint8_t i = 0; i < b.count; ++i) {
    const Actuator& a = b.actuators[i];
    if (a.ledCount == 0) {
      if (a.ledFirst != 0 || a.color[0] != 0 || a.color[1] != 0 || a.color[2] != 0) return BodyError::Leds;
      continue;
    }
    if (a.ledFirst + a.ledCount > b.ledCount) return BodyError::Leds;
    for (uint8_t j = 0; j < i; ++j) {
      const Actuator& o = b.actuators[j];
      if (o.ledCount > 0 && a.ledFirst < o.ledFirst + o.ledCount && o.ledFirst < a.ledFirst + a.ledCount) {
        return BodyError::Leds;
      }
    }
  }
  return BodyError::None;
}

size_t recordLength(size_t count, size_t idLength, size_t nameLength) {
  return kBodyHeaderBytes + count * (kBodyActuatorBytes + kBodyLedBytes) + idLength + nameLength + kBodyLedHeaderBytes + 2;
}

}  // namespace

const char* bodyErrorText(BodyError error) {
  switch (error) {
    case BodyError::None: return "ok";
    case BodyError::Empty: return "blank";
    case BodyError::Magic: return "not a body record";
    case BodyError::Version: return "unknown version";
    case BodyError::Length: return "bad length";
    case BodyError::Crc: return "bad CRC";
    case BodyError::Driver: return "bad driver";
    case BodyError::Count: return "bad actuator count";
    case BodyError::Limits: return "limits out of range";
    case BodyError::Note: return "bad note";
    case BodyError::Output: return "bad output";
    case BodyError::Pulse: return "bad pulse";
    case BodyError::Interval: return "bad interval or latency";
    case BodyError::Text: return "bad id or name";
    case BodyError::Leds: return "bad accent LEDs";
  }
  return "unknown";
}

BrainConfig Body::config() const {
  BrainConfig c;
  c.id = id;
  c.name = name;
  c.actuators = actuators;
  c.actuatorCount = count;
  c.channel = channel;
  c.polyphony = polyphony;
  c.maxOnMs = maxOnMs;
  c.maxDutyPercent = maxDutyPercent;
  c.dutyWindowMs = dutyWindowMs;
  c.watchdogMs = watchdogMs;
  c.ledCount = ledCount;
  c.ledBrightnessPercent = ledBrightnessPercent;
  if (ledCount > 0) c.flashMs = flashMs;
  return c;
}

uint16_t crc16(const uint8_t* data, size_t length) {
  uint16_t crc = 0xFFFF;  // CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, no reflection
  for (size_t i = 0; i < length; ++i) {
    crc = static_cast<uint16_t>(crc ^ (data[i] << 8));
    for (int bit = 0; bit < 8; ++bit) {
      crc = (crc & 0x8000) ? static_cast<uint16_t>((crc << 1) ^ 0x1021) : static_cast<uint16_t>(crc << 1);
    }
  }
  return crc;
}

BodyError validateBody(const Body& b) {
  uint8_t outputs = 0;
  if (b.driver == BodyDriver::Gpio) {
    if (b.address != 0) return BodyError::Driver;
    outputs = kGpioOutputs;
  } else if (b.driver == BodyDriver::Pca9685) {
    if (b.address < 0x40 || b.address > 0x7F) return BodyError::Driver;
    outputs = kPcaOutputs;
  } else {
    return BodyError::Driver;
  }

  if (b.count < 1 || b.count > kMaxActuators) return BodyError::Count;
  if ((b.channel > 15 && b.channel != kOmni) || b.polyphony < 1 || b.polyphony > kMaxActuators ||
      b.maxOnMs < 1 || b.maxOnMs > kCeilingOnMs || b.maxDutyPercent < 1 ||
      b.maxDutyPercent > kCeilingDutyPercent || b.dutyWindowMs < 100 || b.dutyWindowMs > 10000 ||
      b.watchdogMs < 200 || b.watchdogMs > 8000) {
    return BodyError::Limits;
  }

  for (uint8_t i = 0; i < b.count; ++i) {
    const Actuator& a = b.actuators[i];
    if (a.note > 127) return BodyError::Note;
    if (a.output >= outputs || (b.driver == BodyDriver::Gpio && reserved(a.output, b.ledCount > 0))) return BodyError::Output;
    for (uint8_t j = 0; j < i; ++j) {
      if (b.actuators[j].note == a.note) return BodyError::Note;
      if (b.actuators[j].output == a.output) return BodyError::Output;
    }
    if (a.pulseMs < 1 || a.pulseMs > b.maxOnMs || a.minPulseMs > a.pulseMs) return BodyError::Pulse;
    if (a.minIntervalMs < a.pulseMs || a.minIntervalMs > 10000 || a.latencyMs > 1000) return BodyError::Interval;
  }

  if (!textOk(b.id, kBodyMaxId, idChar) || !textOk(b.name, kBodyMaxName, nameChar)) return BodyError::Text;
  return validateLeds(b);
}

BodyError parseBody(const uint8_t* data, size_t length, Body& body) {
  if (data == nullptr || length < 3) return BodyError::Length;
  bool ff = true, zero = true;
  for (size_t i = 0; i < length && i < kBodyHeaderBytes; ++i) {
    ff = ff && data[i] == 0xFF;
    zero = zero && data[i] == 0x00;
  }
  if (ff || zero) return BodyError::Empty;
  if (data[0] != 'M' || data[1] != 'D' || data[2] != 'B') return BodyError::Magic;
  if (length < kBodyHeaderBytes) return BodyError::Length;
  if (data[3] != kBodyVersion) return BodyError::Version;

  size_t total = get16(data + 4);
  if (total < kBodyHeaderBytes + 2 || total > kBodyMaxBytes || total > length) return BodyError::Length;
  if (crc16(data, total - 2) != get16(data + total - 2)) return BodyError::Crc;

  uint8_t count = data[11];
  if (count < 1 || count > kMaxActuators) return BodyError::Count;
  size_t idLength = data[18], nameLength = data[19];
  if (idLength > kBodyMaxId || nameLength > kBodyMaxName) return BodyError::Text;
  if (total != recordLength(count, idLength, nameLength)) return BodyError::Length;

  Body b;
  b.driver = static_cast<BodyDriver>(data[6]);
  b.address = data[7];
  b.channel = data[8];
  b.polyphony = data[9];
  b.maxDutyPercent = data[10];
  b.count = count;
  b.maxOnMs = get16(data + 12);
  b.dutyWindowMs = get16(data + 14);
  b.watchdogMs = get16(data + 16);
  const uint8_t* p = data + kBodyHeaderBytes;
  for (uint8_t i = 0; i < count; ++i, p += kBodyActuatorBytes) {
    Actuator& a = b.actuators[i];
    a.note = p[0];
    a.output = p[1];
    a.pulseMs = get16(p + 2);
    a.minPulseMs = get16(p + 4);
    a.minIntervalMs = get16(p + 6);
    a.latencyMs = get16(p + 8);
  }
  memcpy(b.id, p, idLength);
  memcpy(b.name, p + idLength, nameLength);
  p += idLength + nameLength;
  b.ledCount = p[0];
  b.ledBrightnessPercent = p[1];
  b.flashMs = get16(p + 2);
  p += kBodyLedHeaderBytes;
  for (uint8_t i = 0; i < count; ++i, p += kBodyLedBytes) {
    Actuator& a = b.actuators[i];
    a.ledFirst = p[0];
    a.ledCount = p[1];
    a.color[0] = p[2];
    a.color[1] = p[3];
    a.color[2] = p[4];
  }

  BodyError error = validateBody(b);
  if (error == BodyError::None) body = b;
  return error;
}

size_t encodeBody(const Body& b, uint8_t* out, size_t size) {
  if (validateBody(b) != BodyError::None) return 0;
  size_t idLength = strnlen(b.id, kBodyMaxId), nameLength = strnlen(b.name, kBodyMaxName);
  size_t total = recordLength(b.count, idLength, nameLength);
  if (out == nullptr || size < total) return 0;

  out[0] = 'M';
  out[1] = 'D';
  out[2] = 'B';
  out[3] = kBodyVersion;
  put16(out + 4, static_cast<uint16_t>(total));
  out[6] = static_cast<uint8_t>(b.driver);
  out[7] = b.address;
  out[8] = b.channel;
  out[9] = b.polyphony;
  out[10] = b.maxDutyPercent;
  out[11] = b.count;
  put16(out + 12, b.maxOnMs);
  put16(out + 14, b.dutyWindowMs);
  put16(out + 16, b.watchdogMs);
  out[18] = static_cast<uint8_t>(idLength);
  out[19] = static_cast<uint8_t>(nameLength);
  uint8_t* p = out + kBodyHeaderBytes;
  for (uint8_t i = 0; i < b.count; ++i, p += kBodyActuatorBytes) {
    const Actuator& a = b.actuators[i];
    p[0] = a.note;
    p[1] = a.output;
    put16(p + 2, a.pulseMs);
    put16(p + 4, a.minPulseMs);
    put16(p + 6, a.minIntervalMs);
    put16(p + 8, a.latencyMs);
  }
  memcpy(p, b.id, idLength);
  memcpy(p + idLength, b.name, nameLength);
  p += idLength + nameLength;
  p[0] = b.ledCount;
  p[1] = b.ledBrightnessPercent;
  put16(p + 2, b.flashMs);
  p += kBodyLedHeaderBytes;
  for (uint8_t i = 0; i < b.count; ++i, p += kBodyLedBytes) {
    const Actuator& a = b.actuators[i];
    p[0] = a.ledFirst;
    p[1] = a.ledCount;
    p[2] = a.color[0];
    p[3] = a.color[1];
    p[4] = a.color[2];
  }
  put16(out + total - 2, crc16(out, total - 2));
  return total;
}

}  // namespace metrodom
