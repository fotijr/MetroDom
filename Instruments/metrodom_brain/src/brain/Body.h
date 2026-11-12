// The body record: what a body's ID EEPROM holds (ADR 0006 §3, exec plan 0005).
//
// A brain reads it at boot and becomes that instrument. The same bytes are written over
// MetroDom SysEx by `metrodom body write`, and the Conductor's encoder
// (src/MetroDom.Core/Bodies/BodyImage.cs) must produce exactly what this parser accepts:
// both test suites check the bench fixture (bench-body.hex).
//
// Layout, little-endian, version 1 (docs/references/body-record.md):
//   0  3  "MDB"            magic
//   3  1  1                format version
//   4  2  length           whole record including the CRC
//   6  1  driver           0 = GPIO pins, 1 = PCA9685
//   7  1  address          PCA9685 I2C address (0x40-0x7F); 0 for GPIO
//   8  1  channel          0-15, or 0xFF for omni
//   9  1  polyphony        1-16
//  10  1  maxDutyPercent   1-kCeilingDutyPercent
//  11  1  count            actuators, 1-16
//  12  2  maxOnMs          1-kCeilingOnMs
//  14  2  dutyWindowMs     100-10000
//  16  2  watchdogMs       200-8000 (a body write must finish inside it)
//  18  1  idLength         1-32
//  19  1  nameLength       1-48
//  20  count x 10          note, output, pulseMs, minPulseMs, minIntervalMs, latencyMs
//                          (a GPIO output can't be 4, 5 or 6: the backplane's I2C and /OE)
//   .  idLength            id: a-z, 0-9, '-'
//   .  nameLength          name: printable ASCII
//  -2  2  CRC-16/CCITT-FALSE of everything before it
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "Brain.h"

namespace metrodom {

constexpr size_t kBodyHeaderBytes = 20;
constexpr size_t kBodyActuatorBytes = 10;
constexpr size_t kBodyMaxId = 32;
constexpr size_t kBodyMaxName = 48;
constexpr size_t kBodyMaxBytes = kBodyHeaderBytes + kMaxActuators * kBodyActuatorBytes + kBodyMaxId + kBodyMaxName + 2;
static_assert(kBodyMaxBytes == kMaxBodyBytes, "Brain.h's buffer must hold the largest record");
constexpr uint8_t kBodyVersion = 1;

// Brain pins the backplane uses, so a GPIO body can't (hardware/modules/body-backplane).
constexpr uint8_t kPinSda = 4, kPinScl = 5, kPinOutputEnable = 6;

enum class BodyDriver : uint8_t { Gpio = 0, Pca9685 = 1 };

// Why a record was refused. The numbers go over SysEx in a Body Write Reply: never renumber.
enum class BodyError : uint8_t {
  None = 0,
  Empty = 1,      // all 0xFF or all 0x00: a blank EEPROM
  Magic = 2,
  Version = 3,
  Length = 4,
  Crc = 5,
  Driver = 6,     // unknown driver or bad address
  Count = 7,      // actuators outside 1-16
  Limits = 8,     // polyphony, maxOnMs, duty, window, watchdog or channel out of range
  Note = 9,       // note over 127 or used twice
  Output = 10,    // output out of range for the driver, or used twice
  Pulse = 11,     // pulse outside 1-maxOnMs, or minPulse over the pulse
  Interval = 12,  // minInterval under the pulse or over 10 s, latency over 1 s
  Text = 13,      // id or name empty, too long, or with characters it can't have
};

const char* bodyErrorText(BodyError error);

struct Body {
  BodyDriver driver = BodyDriver::Gpio;
  uint8_t address = 0;
  uint8_t channel = kOmni;
  uint8_t polyphony = 1;
  uint8_t maxDutyPercent = 30;
  uint16_t maxOnMs = 45;
  uint16_t dutyWindowMs = 1000;
  uint16_t watchdogMs = 500;
  uint8_t count = 0;
  Actuator actuators[kMaxActuators] = {};
  char id[kBodyMaxId + 1] = {};
  char name[kBodyMaxName + 1] = {};

  // The Brain's view of this body. Points into this Body, so keep it alive.
  BrainConfig config() const;
};

uint16_t crc16(const uint8_t* data, size_t length);

// Checks every field against the rules above.
BodyError validateBody(const Body& body);

// Reads a record from the start of data (an EEPROM read may be longer than the record).
BodyError parseBody(const uint8_t* data, size_t length, Body& body);

// Writes a valid body's record; returns its length, or 0 if the body is invalid or out is too small.
size_t encodeBody(const Body& body, uint8_t* out, size_t size);

}  // namespace metrodom
