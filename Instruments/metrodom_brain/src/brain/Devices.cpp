#include "Devices.h"

namespace metrodom {
namespace {

// PCA9685 registers (NXP datasheet, rev. 4).
constexpr uint8_t kMode1 = 0x00;
constexpr uint8_t kMode1AutoIncrement = 0x20;  // SLEEP (0x10) clear: oscillator on; ALLCALL off
constexpr uint8_t kMode2Totem = 0x04;          // OUTDRV: push-pull, right for a MOSFET gate
constexpr uint8_t kLed0 = 0x06;                // LEDn_ON_L, ON_H, OFF_L, OFF_H at 0x06 + 4n
constexpr uint8_t kAllLed = 0xFA;              // ALL_LED_ON_L .. ALL_LED_OFF_H
constexpr uint8_t kFull = 0x10;                // bit 4 of ON_H / OFF_H; full OFF wins over full ON

}  // namespace

bool Pca9685::begin() {
  const uint8_t modes[] = {kMode1, kMode1AutoIncrement, kMode2Totem};  // MODE1 then MODE2
  bool ok = allOff();
  ok = bus_.write(address_, modes, sizeof modes) && ok;
  return allOff() && ok;  // again, now that auto-increment is surely on
}

bool Pca9685::set(uint8_t output, bool on) {
  if (output > 15) return false;
  // On: ON_H full, OFF_H clear. Off: OFF_H full (it overrides ON either way).
  const uint8_t command[] = {static_cast<uint8_t>(kLed0 + 4 * output), 0, on ? kFull : uint8_t{0}, 0,
                             on ? uint8_t{0} : kFull};
  return bus_.write(address_, command, sizeof command);
}

bool Pca9685::allOff() {
  const uint8_t command[] = {kAllLed, 0, 0, 0, kFull};
  return bus_.write(address_, command, sizeof command);
}

bool Eeprom24::present() {
  const uint8_t at[] = {0, 0};
  return bus_.write(address_, at, sizeof at);
}

bool Eeprom24::read(uint16_t at, uint8_t* data, size_t length) {
  while (length > 0) {
    size_t n = length < kPage ? length : kPage;  // small reads fit any Wire buffer
    const uint8_t pointer[] = {static_cast<uint8_t>(at >> 8), static_cast<uint8_t>(at & 0xFF)};
    if (!bus_.write(address_, pointer, sizeof pointer) || !bus_.read(address_, data, n)) return false;
    at = static_cast<uint16_t>(at + n);
    data += n;
    length -= n;
  }
  return true;
}

bool Eeprom24::waitForWrite() {
  // The chip ignores its address while it writes; it answers again when the page is done.
  for (int i = 0; i < 20; ++i) {
    waitMs_(1);
    if (present()) return true;
  }
  return false;
}

bool Eeprom24::write(const uint8_t* data, size_t length) {
  uint8_t page[2 + kPage];
  for (size_t at = 0; at < length; at += kPage) {
    size_t n = length - at < kPage ? length - at : kPage;
    page[0] = static_cast<uint8_t>(at >> 8);
    page[1] = static_cast<uint8_t>(at & 0xFF);
    for (size_t i = 0; i < n; ++i) page[2 + i] = data[at + i];
    if (!bus_.write(address_, page, 2 + n) || !waitForWrite()) return false;
  }

  uint8_t back[kPage];
  for (size_t at = 0; at < length; at += kPage) {
    size_t n = length - at < kPage ? length - at : kPage;
    if (!read(static_cast<uint16_t>(at), back, n)) return false;
    for (size_t i = 0; i < n; ++i) {
      if (back[i] != data[at + i]) return false;
    }
  }
  return true;
}

}  // namespace metrodom
