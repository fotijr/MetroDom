# 0006: Swappable instrument brains (brain / body split)

Status: **Proposed** (2026-11-10)

## Context
Hardware priorities from the owner: **ease of programming, low cost, interchangeability.**
Ideally the "brain" of any instrument can be swapped as needed: a dead board at a gig gets
replaced from a spare in a minute, and a better MCU later doesn't mean redesigning every
instrument. The prototype has a different sketch, pinout and protocol per instrument.

## Decision
Split every instrument into a **brain** and a **body**:

| | Brain | Body |
| --- | --- | --- |
| What | Off-the-shelf MCU board running **one generic MetroDom firmware** | Instrument-specific hardware: actuator drivers, power, mechanics |
| Knows | MIDI, safety rules, how to read a body | Nothing in software. It *describes itself* via an ID chip |
| Swap | Any brain fits any body | Bodies are built per instrument |

1. **Brain standard: the Raspberry Pi Pico footprint (RP2040 / RP2350).**
   - Cost: ~$4–7. Many pin-compatible variants (Pico, Pico 2, W/2W, and clones).
   - Ease: programmable from Arduino IDE / PlatformIO (`arduino-pico` core + Adafruit TinyUSB
     for USB-MIDI) or CircuitPython (`usb_midi`). Drag-and-drop UF2 flashing, no programmer.
   - Native USB means class-compliant USB-MIDI ([ADR 0001](0001-midi-is-the-contract.md)).
   - Bodies use a socket (headers) for the Pico footprint, so swapping is unplug/plug.
2. **Brain ↔ body interface: I²C over a Qwiic/STEMMA QT connector** (open, cheap, polarized),
   plus power. Actuators are driven through standard I²C parts so the brain needs few pins:
   - PCA9685 16-channel PWM for servos and for solenoids via MOSFET boards.
   - MCP23017 GPIO expander where plain on/off suffices.
   Direct GPIO is allowed for timing-critical actuators, through a fixed, documented pin map.
3. **Body identity: an I²C EEPROM on the body** (e.g. 24LC-series) holds the body's
   [instrument profile](../references/instrument-profile.md) plus its channel map
   (MIDI note → driver channel, pulse length, duty limits). Any brain plugged into any body
   boots, reads the EEPROM, and becomes that instrument. The EEPROM can be rewritten over MIDI
   SysEx from the Conductor (owner role only). Draft format and command:
   [body record](../references/body-record.md) (a binary table the brain builds its profile from).
4. **One firmware for all brains.** Instrument differences are data (the profile), not code.
   Special actuators get a driver module in the shared firmware, enabled by the profile.
5. **Safety stays in the brain firmware** (watchdog, max on-time, duty limits from the profile)
   *and* a body-level hardware backstop where cheap (e.g. solenoid driver with RC/one-shot
   pulse limiting) so a hung brain can't hold a solenoid on.

## Consequences
- Spares kit = a few pre-flashed Picos. Field swap needs no laptop.
- Existing Arduino sketches are ported into the generic firmware (exec plan 0001, Phase 1).
- I²C adds a little latency (~0.1–0.3 ms per PCA9685 update at 400 kHz). Fine against
  mechanical latency, but measure it.
- USB identity: brains report a USB product string with the instrument name read from the body.
  For a dedicated USB VID/PID, apply to pid.codes (free for open-source hardware).

## Alternatives considered
- **Arduino Leonardo/Micro (ATmega32U4):** native USB-MIDI, but pricier and more constrained.
  Existing Leonardo code ports easily, so it's acceptable for prototypes but not the standard.
- **Teensy:** best `usbMIDI` ergonomics, but higher cost and a single vendor.
- **ESP32-S3:** adds Wi-Fi/BLE MIDI. Worth it later for wireless instruments.
  Could be a second brain profile on the same body interface.

## Open questions
- Exact body connector pinout and power rails (5 V logic + separate actuator supply).
  Proposed in [ADR 0009](0009-shared-hardware-modules.md) / [hardware/interfaces.md](../../hardware/interfaces.md).
- Generic firmware in Arduino C++ (speed, libraries) vs CircuitPython (easiest edits).
  Leaning Arduino C++ for timing.
