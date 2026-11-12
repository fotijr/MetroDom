// MetroDom generic brain firmware (ADR 0006, exec plans 0001 Phase 1 and 0005).
//
// Board: Raspberry Pi Pico / Pico 2 with the arduino-pico core (Earle Philhower).
// Tools > USB Stack: "Adafruit TinyUSB". Libraries: "MIDI Library" (FortySevenEffects).
// See README.md for wiring and flashing.
//
// This file only moves bytes and pins. Every decision (which body, pulse length, limits, panic,
// identity, profile, body writes) is in src/brain/, which is tested on a laptop: make -C test.
//
// Boot picks the body:
// - the body's ID EEPROM (24LC32 at 0x50 on the Qwiic bus) holds a valid record: that body;
// - nothing answers at 0x50: the compiled-in bench body (config.h);
// - the EEPROM answers with a blank or bad record: no actuators at all, and the profile name says
//   why, so `metrodom devices --identify` shows it and `metrodom body write` can fix it.

#include <Adafruit_TinyUSB.h>
#include <MIDI.h>
#include <Wire.h>
#include <stdio.h>

#include "config.h"
#include "src/brain/Body.h"
#include "src/brain/Brain.h"
#include "src/brain/Devices.h"

namespace {

// The backplane (hardware/modules/body-backplane): Qwiic I2C0 on GPIO 4 (SDA) and 5 (SCL) at
// 400 kHz, and the PCA9685's /OE on GPIO 6 with a pull-up (high = every output off).
constexpr uint8_t kSda = metrodom::kPinSda, kScl = metrodom::kPinScl, kOe = metrodom::kPinOutputEnable;
constexpr uint32_t kI2cHz = 400000;
constexpr uint32_t kRestartDelayMs = 50;  // lets the Body Write Reply leave before the reboot

Adafruit_USBD_MIDI usbMidi;
MIDI_CREATE_INSTANCE(Adafruit_USBD_MIDI, usbMidi, MIDI);

class WireBus : public metrodom::I2c {
 public:
  bool write(uint8_t address, const uint8_t* data, size_t length) override {
    Wire.beginTransmission(address);
    Wire.write(data, length);
    return Wire.endTransmission() == 0;
  }
  bool read(uint8_t address, uint8_t* data, size_t length) override {
    if (Wire.requestFrom(address, length) != length) return false;
    for (size_t i = 0; i < length; ++i) data[i] = static_cast<uint8_t>(Wire.read());
    return true;
  }
};

class Pins : public metrodom::Outputs {
 public:
  bool set(uint8_t output, bool on) override {
    digitalWrite(output, on ? HIGH : LOW);
    return true;
  }
};

class UsbReplies : public metrodom::Replies {
 public:
  void sysex(const uint8_t* data, size_t length) override {
    MIDI.sendSysEx(static_cast<unsigned>(length), data, true);  // data includes F0/F7
  }
};

void waitMs(uint32_t ms) { delay(ms); }

// /OE is active low: on = drive low. The brain only enables outputs while it's healthy.
void outputEnable(bool on) { digitalWrite(kOe, on ? LOW : HIGH); }

WireBus bus;
metrodom::Eeprom24 eeprom(bus, waitMs);
metrodom::Body body;
metrodom::BrainConfig active;
char silentName[64];
Pins pins;
metrodom::Pca9685* pca = nullptr;
UsbReplies replies;
metrodom::Brain* brain = nullptr;
bool mounted = false;
uint32_t restartAt = 0;

// Which body this brain is, and the outputs that drive it.
metrodom::Outputs& chooseBody() {
  uint8_t record[metrodom::kBodyMaxBytes];
  if (!eeprom.present()) {
    active = config::make();  // a bench rig with no EEPROM
    return pins;
  }
  metrodom::BodyError error = eeprom.read(0, record, sizeof record)
                                  ? metrodom::parseBody(record, sizeof record, body)
                                  : metrodom::BodyError::Length;
  if (error != metrodom::BodyError::None) {
    snprintf(silentName, sizeof silentName, "MetroDom brain (body: %s)", metrodom::bodyErrorText(error));
    active = metrodom::BrainConfig{};
    active.id = "metrodom-brain";
    active.name = silentName;
    active.actuators = nullptr;
    active.actuatorCount = 0;
    return pins;
  }
  active = body.config();
  if (body.driver == metrodom::BodyDriver::Pca9685) {
    static metrodom::Pca9685 chip(bus, body.address, outputEnable);
    pca = &chip;
    return chip;
  }
  return pins;
}

void send(uint8_t a, uint8_t b, uint8_t c) {
  const uint8_t message[] = {a, b, c};
  brain->message(message, sizeof message, millis());
}

void onNoteOn(byte channel, byte note, byte velocity) { send(0x90 | ((channel - 1) & 0x0F), note, velocity); }

void onControlChange(byte channel, byte number, byte value) { send(0xB0 | ((channel - 1) & 0x0F), number, value); }

void onSystemReset() {
  const uint8_t message[] = {0xFF};
  brain->message(message, sizeof message, millis());
}

void onSysEx(byte* data, unsigned size) { brain->message(data, size, millis()); }

}  // namespace

void setup() {
  // /OE high first: until the brain says otherwise, a PCA9685 body's outputs are disabled (the
  // pull-up already holds it there while the brain resets or is out of its socket).
  pinMode(kOe, OUTPUT);
  digitalWrite(kOe, HIGH);
  // The bench pins low before anything else: a brain that boots or resets is silent. A PCA9685
  // powers up with every output off, and Pca9685::begin() turns them all off again.
  {
    const metrodom::BrainConfig bench = config::make();
    for (uint8_t i = 0; i < bench.actuatorCount; ++i) {
      pinMode(bench.actuators[i].output, OUTPUT);
      digitalWrite(bench.actuators[i].output, LOW);
    }
  }
  Wire.setSDA(kSda);
  Wire.setSCL(kScl);
  Wire.begin();
  Wire.setClock(kI2cHz);

  metrodom::Outputs& outputs = chooseBody();
  if (pca != nullptr) {
    pca->begin();
  } else {
    for (uint8_t i = 0; i < active.actuatorCount; ++i) {
      pinMode(active.actuators[i].output, OUTPUT);
      digitalWrite(active.actuators[i].output, LOW);
    }
  }
  static metrodom::Brain theBrain(active, outputs, replies, &eeprom);
  brain = &theBrain;
  brain->begin(millis());

  if (!TinyUSBDevice.isInitialized()) TinyUSBDevice.begin(0);
  TinyUSBDevice.setProductDescriptor(active.name);  // the body's name, so a DAW lists the instrument
  usbMidi.setStringDescriptor(active.name);
  MIDI.begin(MIDI_CHANNEL_OMNI);
  MIDI.turnThruOff();
  MIDI.setHandleNoteOn(onNoteOn);
  MIDI.setHandleControlChange(onControlChange);
  MIDI.setHandleSystemReset(onSystemReset);
  MIDI.setHandleSystemExclusive(onSysEx);
  if (TinyUSBDevice.mounted()) {  // re-enumerate so the host sees the MIDI interface
    TinyUSBDevice.detach();
    delay(10);
    TinyUSBDevice.attach();
  }

  // A loop that stops running restarts the brain, and setup() turns everything off again.
  rp2040.wdt_begin(active.watchdogMs);
}

void loop() {
  rp2040.wdt_reset();
#ifdef TINYUSB_NEED_POLLING_TASK
  TinyUSBDevice.task();
#endif
  bool now = TinyUSBDevice.mounted();
  if (now != mounted) {
    mounted = now;
    brain->hostConnected(now);  // unplugged: all off, no notes until it's back
  }
  while (MIDI.read()) {
  }
  brain->tick(millis());

  // A new body was written and verified: boot as it.
  if (brain->restartRequested()) {
    if (restartAt == 0) restartAt = millis() + kRestartDelayMs;
    if (static_cast<int32_t>(millis() - restartAt) >= 0) rp2040.reboot();
  }
}
