# 0005: The brain for M1 (self-describing bodies)

Status: Active · Last updated: 2026-11-12

## Goal
When the M1 xylophone body is soldered, the brain firmware and the Conductor need no new code.
A brain plugged into a body reads the body's ID EEPROM, becomes that instrument, and drives its
actuators through a PCA9685. The owner writes a body from a JSON file with one command.
Measured by: host tests in `make -C Instruments/metrodom_brain/test`, .NET tests for the
Conductor side, and one image fixture that both sides must agree on byte for byte.

## Context
- Roadmap [M1](../../ROADMAP.md#m1-three-note-xylophone-on-the-brainbody-split-): bet B2.
  Rule 3 (software runs ahead).
- ADRs [0006](../../design-docs/0006-swappable-instrument-brains.md) (brain/body split),
  [0007](../../design-docs/0007-instrument-discovery.md) (discovery),
  [0001](../../design-docs/0001-midi-is-the-contract.md) (MIDI is the contract).
- Safety: [RELIABILITY.md](../../RELIABILITY.md) firmware layer. Debt: TD-29.
- Hardware side: [plan 0002](0002-hardware-bootstrap.md) (`body-backplane`).

## Phases

### Phase B1: Body record
- [x] Binary body record (`MDB`, version 1, CRC-16) parsed and encoded in the platform-free core
- [x] Boot: valid body → that instrument; no EEPROM → the compiled-in `config.h`; an EEPROM that
      answers with a bad record → a silent brain that says why in its profile name
- [x] Firmware ceilings no body can raise (pulse, duty)

### Phase B2: Drivers and velocity
- [x] PCA9685 driver (full on / full off per channel), host-tested against a fake I²C bus
- [x] An output that fails to turn off is retried every loop and blocks new strikes
- [x] Velocity → pulse length between a note's `minPulseMs` and `pulseMs`

### Phase B3: Writing a body
- [x] Body Write over MetroDom SysEx, verified by reading the EEPROM back, then a restart
- [x] `metrodom body check FILE` and `metrodom body write PORT FILE`
- [x] M1 body fixture (`bodies/m1-xylophone.json` → `.hex`) checked by both test suites

### Phase B4: On real hardware (needs plan 0002 H1 and the backplane)
- [ ] First real build with arduino-pico + TinyUSB; flash; the M0 bench body plays
- [ ] Backplane bench tests ([body-backplane](../../../hardware/modules/body-backplane/README.md#bench-test))
- [ ] Measure the PCA9685's added latency (ADR 0006: ~0.1-0.3 ms) with `metrodom calibrate`

## Decisions log
- 2026-11-12: Plan written when plan 0004 finished, because M0/M1 hardware is next and the
  firmware is what it would wait on.
- 2026-11-12: B1-B3 shape. The EEPROM holds a binary table, not the
  profile JSON verbatim: the brain enforces the table and builds the profile from it, so they
  can't disagree, and the brain never parses JSON. One fixture (`m1-xylophone.hex`) pins the
  layout for both the C++ and the C# code. A bad record boots a silent brain, never the bench
  pin map. Firmware ceilings (250 ms, 50%) sit above any body. PCA9685 channels are full on/off
  (no PWM): the pulse is timed by the brain. Body Write sends hex in order and is verified by
  reading the EEPROM back; write protection is the EEPROM's WP jumper, because USB MIDI has no
  login. The brain restarts after a write so boot stays the one place a body is applied.
  Firmware version 0.2.0. `metrodom virtual` accepts Body Write so the command can be tried
  without hardware: checked on macOS, the virtual xylophone answered as the M1 body.
- 2026-11-12: Owner decisions. (1) The binary body record is accepted. (2) The PCA9685's `/OE`
  goes to brain GPIO 6 with a 10 kΩ pull-up: outputs are enabled only while the firmware holds it
  low, so a reset or a pulled brain cuts every channel at once, and the firmware lets go of it
  on an output fault, when USB is lost and after a body write. (3) Write protection is a
  momentary button wired to the EEPROM's `WP` pin, held during the write: it can't be left on
  like a jumper, sits outside the case, and needs no firmware. `body write` asks for it.

## Open questions
