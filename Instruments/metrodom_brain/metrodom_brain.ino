// MetroDom generic brain firmware, first cut (ADR 0006, exec plan 0001 Phase 1).
//
// Board: Raspberry Pi Pico / Pico 2 with the arduino-pico core (Earle Philhower).
// Tools > USB Stack: "Adafruit TinyUSB". Libraries: "MIDI Library" (FortySevenEffects).
// See README.md for wiring and flashing.
//
// This file only moves bytes and pins. Every decision (pulse length, limits, panic, identity,
// profile) is in src/brain/, which is tested on a laptop: make -C test.

#include <Adafruit_TinyUSB.h>
#include <MIDI.h>

#include "config.h"
#include "src/brain/Brain.h"

namespace {

Adafruit_USBD_MIDI usbMidi;
MIDI_CREATE_INSTANCE(Adafruit_USBD_MIDI, usbMidi, MIDI);

class Pins : public metrodom::Outputs {
 public:
  void set(uint8_t output, bool on) override { digitalWrite(output, on ? HIGH : LOW); }
};

class UsbReplies : public metrodom::Replies {
 public:
  void sysex(const uint8_t* data, size_t length) override {
    MIDI.sendSysEx(static_cast<unsigned>(length), data, true);  // data includes F0/F7
  }
};

const metrodom::BrainConfig kConfig = config::make();
Pins pins;
UsbReplies replies;
metrodom::Brain brain(kConfig, pins, replies);
bool mounted = false;

void send(uint8_t a, uint8_t b, uint8_t c) {
  const uint8_t message[] = {a, b, c};
  brain.message(message, sizeof message, millis());
}

void onNoteOn(byte channel, byte note, byte velocity) { send(0x90 | ((channel - 1) & 0x0F), note, velocity); }

void onControlChange(byte channel, byte number, byte value) { send(0xB0 | ((channel - 1) & 0x0F), number, value); }

void onSystemReset() {
  const uint8_t message[] = {0xFF};
  brain.message(message, sizeof message, millis());
}

void onSysEx(byte* data, unsigned size) { brain.message(data, size, millis()); }

}  // namespace

void setup() {
  // Outputs low before anything else: a brain that boots or resets is silent.
  for (uint8_t i = 0; i < kConfig.actuatorCount; ++i) {
    pinMode(kConfig.actuators[i].output, OUTPUT);
    digitalWrite(kConfig.actuators[i].output, LOW);
  }
  brain.begin(millis());

  if (!TinyUSBDevice.isInitialized()) TinyUSBDevice.begin(0);
  TinyUSBDevice.setProductDescriptor(kConfig.name);
  usbMidi.setStringDescriptor(kConfig.name);
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
  rp2040.wdt_begin(kConfig.watchdogMs);
}

void loop() {
  rp2040.wdt_reset();
#ifdef TINYUSB_NEED_POLLING_TASK
  TinyUSBDevice.task();
#endif
  bool now = TinyUSBDevice.mounted();
  if (now != mounted) {
    mounted = now;
    brain.hostConnected(now);  // unplugged: all off, no notes until it's back
  }
  while (MIDI.read()) {
  }
  brain.tick(millis());
}
