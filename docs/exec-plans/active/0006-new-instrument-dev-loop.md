# 0006: New-instrument dev loop

Status: Active · Last updated: 2026-11-12

## Goal
Building a new MetroDom instrument is a short loop a developer can follow alone, from one guide:
**scaffold → describe the body → hear it on a virtual port behaving exactly like a brain →
flash → write the body → bench and calibrate → commit the records.** No firmware fork, no
copying files by hand, no knowledge you'd only find by reading several ADRs.

Measured by:
- **Software half (no hardware):** from a fresh clone, a new body plays the demo song on a
  virtual port in under 15 minutes, using only the guide, with the notes it would refuse reported.
- **Hardware half:** from a soldered body to a calibrated, playing instrument in one bench
  session, using only the guide and `metrodom` commands. The first real run is M2's snare.
- **Parity:** one shared vector file of strikes and expected outcomes passes both the brain's
  C++ host test and the Conductor's .NET brain model.

## Context
- Roadmap [M2](../../ROADMAP.md#m2-two-piece-band-) (the second instrument is the first real user
  of this loop) and M6 (each new mechanic is a new instrument). Rule 3: software runs ahead.
- ADRs [0006](../../design-docs/0006-swappable-instrument-brains.md) (brain/body split),
  [0008](../../design-docs/0008-hardware-as-code.md) (hardware records),
  [0009](../../design-docs/0009-shared-hardware-modules.md) (modules).
- What exists today: [body record](../../references/body-record.md) (`body check|write|read`),
  [virtual band](../../references/virtual-band.md), [calibration](../../references/calibration.md),
  [brain firmware](../../../Instruments/metrodom_brain/README.md),
  [hardware/README.md](../../../hardware/README.md). Debt: TD-29 (brain never flashed).
- Builds on [plan 0005](0005-brain-for-m1.md); N5's flashing and N6's proof wait on
  [plan 0002](0002-hardware-bootstrap.md) H1.

## Today's friction (why this plan exists)
1. No front door: the path is spread over about six docs; the Instruments README leads with the legacy sketches.
2. No scaffold: no `Instruments/_template/`, no command to start a body. You copy `m1-xylophone.json`.
3. Body files live in `metrodom_brain/bodies/`, not next to the instrument they describe.
4. `metrodom virtual` takes a Body Write but still plays as its `-i` spec: you can't hear what a
   body would refuse (interval, duty, polyphony) or how velocity maps to pulse length.
5. Firmware setup is Arduino IDE clicks. No pinned versions, no CI compile against the real core.
6. No guided first power-on: identify, fire each note, soak, calibrate are separate commands you
   have to know about.
7. Nothing checks that a body fits its `instrument.toml` (outputs vs driver channels, notes vs strikers).

## Phases

### Phase N1: Front door
- [ ] `docs/references/new-instrument.md`: the whole loop, one command per step, linking out
      for depth rather than restating. Written first against today's commands, then updated as
      each phase lands
- [ ] Row in AGENTS.md ("Build a new instrument"); Instruments README leads with the brain and
      the guide, legacy sketches moved to the bottom
- [ ] `Instruments/_template/`: `instrument.toml`, `body.json`, `README.md` (wiring, power,
      measured latency, bench results), each with the fields to fill marked

### Phase N2: Scaffold, and bodies live with their instrument
- [ ] `metrodom instrument new <id>`: creates `Instruments/<id>/` from the template, asks (or
      takes flags for) name, driver, notes, outputs and striker module, and writes a body that
      already passes `body check` with safe defaults (30 ms pulse, 60 ms interval, 30% duty)
- [ ] Real instruments' body files move to `Instruments/<id>/body.json`; test fixtures
      (`m1-xylophone*.{json,hex}`) stay with the brain's tests. Docs and examples updated
- [ ] `body check` with no argument checks every `Instruments/*/body.json`, and CI runs it

### Phase N3: A virtual brain that behaves like the real one
- [ ] `BrainModel` in Core: the brain's acceptance rules (interval, duty leaky bucket,
      polyphony, ceilings, velocity → pulse, accent flash) as pure logic on a fake clock
- [ ] Shared parity vectors (`Instruments/metrodom_brain/test/vectors/*.json`: body + timed
      strikes + expected accept/refuse/pulse/flash) run by both `make -C test` and the .NET tests,
      so the model can't drift from the firmware
- [ ] `metrodom virtual --body FILE` (repeatable): a port named after the body that plays only
      its notes, refuses what the brain would refuse, and shows strikes, refusals (with reason)
      and LED flashes live
- [ ] `metrodom simulate SONG --body FILE`: per-note refusal counts and reasons for a whole
      song, so a body can be tuned against real music before anything is soldered
- [ ] The governor and `BrainModel` share their rules where they overlap, or a test shows they agree

### Phase N4: Records that check each other
- [ ] `check_hardware.py` reads `Instruments/<id>/body.json` next to `instrument.toml`: output
      count fits the driver modules' channels, note count fits the striker/presser quantity, a
      `pca9685` body has a `body-backplane`, an `accent` block has `led-accent`
- [ ] Clear messages that say which file and field to change

### Phase N5: One-command firmware build and flash
- [ ] `make -C Instruments/metrodom_brain build|flash` on `arduino-cli`, with the arduino-pico
      core, Adafruit TinyUSB, MIDI Library and NeoPixel versions pinned in the repo
- [ ] CI job compiles the sketch against the real core on Linux (closes part of TD-29)
- [ ] `flash` finds a Pico in BOOTSEL mode and copies the UF2, so no IDE is needed
- [ ] On hardware (needs plan 0002 H1): the M0 bench body flashes and plays from the make target

### Phase N6: Guided bench
- [ ] `metrodom bench <PORT>`: identify and show the body; fire each note slowly at low, then
      full velocity, asking "heard it?"; a short soak at the duty limit with a reminder to feel
      the coil; then offers `calibrate` and `body write --calibrated`
- [ ] Writes a bench summary (per note: heard, latency, soak result) the developer pastes into
      the build record (`hardware/builds/MD-…-NNN.md`)
- [ ] Runs end to end against `virtual --body` in a test; on hardware with M2's snare

## Decisions log
- 2026-11-12: Plan written at the owner's request after a review of the new-instrument story.
  Order: N1 to N4 need no hardware and give the smooth first hour; N5 and N6 finish on real
  hardware. N3 is the core of the loop and gets the parity vectors so a software brain can be
  trusted as a stand-in.

## Open questions
- Command name: `metrodom instrument new` (an instrument is more than its body) or `metrodom body new`?
- Bodies at `Instruments/<id>/body.json`: one body per instrument, or a folder for variants
  (with and without accent LEDs)?
- Firmware build: `arduino-cli` (closest to today's IDE steps) or PlatformIO (better pinning,
  heavier)? The plan assumes `arduino-cli`.
- Should `bench` require an onset input for latency, or keep the phone-recording fallback M1 allows?
