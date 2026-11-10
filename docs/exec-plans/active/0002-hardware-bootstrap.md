# 0002: Hardware bootstrap

Status: Active · Last updated: 2026-11-10

## Goal
A software-first owner goes from "hardware noob" to a **modular solenoid xylophone body** built
from shared modules ([ADR 0009](../../design-docs/0009-shared-hardware-modules.md)), recorded in
the repo ([ADR 0008](../../design-docs/0008-hardware-as-code.md)), and then reuses those modules
for a **second, different instrument** with no new electronics design. Measured by: the second
instrument needs only a new frame, profile and `instrument.toml`.

## Context
- Brain/body split: [ADR 0006](../../design-docs/0006-swappable-instrument-brains.md)
- Safety rules every step must respect: [RELIABILITY.md](../../RELIABILITY.md)
- Firmware side of the same work: [exec plan 0001](0001-foundation-reboot.md), Phase 1
- Where things are stored: [hardware/README.md](../../../hardware/README.md)

Principle: **learn on one channel, then copy it.** Each phase produces a module that later
phases reuse, and each phase leaves a build record and updated inventory behind.

## Phases

### Phase H0: Workbench & repo scaffolding
- [x] `hardware/` layout, catalog, inventory, interfaces, module template, checker in CI
- [ ] Buy the starter tool kit (see [hardware/README.md](../../../hardware/README.md#starter-tool-kit))
- [ ] Inventory what's already in the drawer (old xylophone parts) into `stock.csv`
- [ ] Owner decides 12 V vs 24 V rail and accepts/edits ADR 0008 and 0009

### Phase H1: One solenoid, on a breadboard (learn the physics)
Teaches: logic vs power rails, common ground, MOSFET low-side switching, flyback diodes,
current limits, why a stuck-on solenoid gets hot.
- [ ] Pico blinks an LED from the Arduino IDE (toolchain works)
- [ ] Pico + one MOSFET + flyback diode + one solenoid on a current-limited bench supply
- [ ] Fixed 20–45 ms pulse from firmware, never "on until told off"
- [ ] Measure solenoid current and temperature after 1 min at max repeat rate. Record it
- [ ] First build record: `MD-BREADBOARD-001` (it's fine for experiments to have records)

### Phase H2: `power-entry` + `driver-mosfet-8ch` (perfboard or off-the-shelf boards)
- [ ] Fused 12 V input, power switch, latching kill switch, 12 V → 5 V buck for logic
- [ ] PCA9685 driving an 8-channel MOSFET board, `act-2p` connector per channel
- [ ] Bench test: all channels pulse, kill switch drops the rail, brain unplug = all off

### Phase H3: `striker-solenoid` rev A (mechanics)
- [ ] OpenSCAD bracket that bolts to `mount-2020` and holds the solenoid at adjustable height
- [ ] Mallet tip options (rubber, felt, plastic) as swappable printed parts
- [ ] Bench test: consistent strike at 8 strikes/s for 60 s without overheating
- [ ] Build three: `MD-STRIKER-SOLENOID-001..003`

### Phase H4: `latency-jig` (measure, don't guess)
- [ ] Piezo or mic on a second Pico, timestamp Note On vs onset
- [ ] Record per-note latency into the xylophone's profile

### Phase H5: Xylophone body v1 (compose the modules)
- [ ] `body-backplane` (Pico socket + Qwiic + ID EEPROM + PCA9685), perfboard first
- [ ] Xylophone frame on 2020 extrusion, 3 → 8 notes using striker rev A
- [ ] `Instruments/xylophone_solenoid/instrument.toml` lists its modules
- [ ] Plays a song from `metrodom play` (ties into exec plan 0001)

### Phase H6: Second instrument from the same modules
Candidates (all reuse `striker-solenoid`): glockenspiel, tuned bells, a small drum kit
(snare, cowbell, woodblock). Pick the cheapest one that sounds good.
- [ ] Only new hardware: frame and tips. Anything else new is a finding: write it down
- [ ] Retrospective: what had to change in the modules? Bump revisions accordingly

### Phase H7 (later): PCB and harnesses
- [ ] KiCad `body-backplane` rev B once perfboard rev A is proven
- [ ] WireViz harness drawings for anything with more than one cable

## Decisions log
- 2026-11-10: Hardware is stored as plain text under `hardware/` (ADR 0008). Instruments are
  composed of shared modules with fixed connector standards (ADR 0009). Learning path starts
  with one solenoid on a breadboard.

## Open questions
- 12 V vs 24 V default actuator rail (ADR 0009).
- Perfboard vs straight-to-PCB for `body-backplane`.
- Which second instrument (H6).
- Budget ceiling for the starter tool kit.
