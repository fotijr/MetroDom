// The body's I2C parts, platform-free: the sketch supplies the bus (Wire), the tests a fake one.
//
// - Pca9685: 16 outputs, each driven full on or full off (a MOSFET gate or a driver board's input).
//   PWM is not used: a strike's length is the brain's pulse, timed in the loop.
// - Eeprom24: a 24LC32-class EEPROM (2-byte addresses, 32-byte pages) holding the body record.
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "Brain.h"

namespace metrodom {

class I2c {
 public:
  // One transaction each. false on a NACK or bus error.
  virtual bool write(uint8_t address, const uint8_t* data, size_t length) = 0;
  virtual bool read(uint8_t address, uint8_t* data, size_t length) = 0;

 protected:
  ~I2c() = default;
};

class Pca9685 : public Outputs {
 public:
  Pca9685(I2c& bus, uint8_t address) : bus_(bus), address_(address) {}

  // Auto-increment, totem-pole outputs, oscillator running, every output full off.
  bool begin();

  // Full on or full off for one channel (0-15). false if the chip didn't take it.
  bool set(uint8_t output, bool on) override;

  // Every channel off in one write (ALL_LED registers).
  bool allOff();

 private:
  I2c& bus_;
  uint8_t address_;
};

class Eeprom24 : public BodyStore {
 public:
  static constexpr uint8_t kAddress = 0x50;
  static constexpr size_t kPage = 32;

  // waitMs lets the write cycle finish (5 ms per page on a 24LC32).
  Eeprom24(I2c& bus, void (*waitMs)(uint32_t), uint8_t address = kAddress)
      : bus_(bus), waitMs_(waitMs), address_(address) {}

  // Does anything answer at the EEPROM's address?
  bool present();

  bool read(uint16_t at, uint8_t* data, size_t length);

  // Writes page by page, then reads everything back. false if a page failed or the read-back
  // differs (a write-protected EEPROM acknowledges writes and keeps its old bytes).
  bool write(const uint8_t* data, size_t length) override;

 private:
  bool waitForWrite();

  I2c& bus_;
  void (*waitMs_)(uint32_t);
  uint8_t address_;
};

}  // namespace metrodom
