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

### Phase N1: Getting started
- [x] `docs/references/new-instrument.md`: the whole loop, one command per step, linking out
      for depth rather than restating. Written against today's commands; each step still
      planned says what to do instead. Update it as each phase lands
- [x] Row in AGENTS.md ("Build a new instrument"); Instruments README leads with the guide and
      the three files, legacy sketches at the bottom
- [x] `Instruments/_template/`: `instrument.toml`, `body.json` (checked by `body check`),
      `README.md` (wiring, power, bench results). `check_hardware.py` skips `_template`

### Phase N2: Scaffold, and bodies live with their instrument
- [x] `metrodom instrument new <id>`: creates `Instruments/<id>/` (`--notes`, `--driver`,
      `--actuator`, `--plays`, `--name`, `--dir`), writes a body that passes `body check` with safe
      defaults (30 ms pulse, 12 ms soft, 60 ms interval, 30% duty, polyphony ≤ 2), an
      `instrument.toml` with an actuator per note and a driver per 8, and the README from the
      template (built into the CLI). Checks `--actuator` and `--plays` against the checkout's records
- [x] The M1 body lives at `Instruments/xylophone_solenoid/body.json`; test fixtures
      (`m1-xylophone*.{json,hex}`) stay with the brain's tests. Docs and CLI examples updated
- [x] `body check` with no argument checks every `Instruments/*/body.json`; a .NET test runs it,
      so CI does

### Phase N3: A virtual brain that behaves like the real one
- [x] `BrainModel` in Core: the brain's strike decisions (busy, interval, duty leaky bucket,
      polyphony, channel, ceilings, velocity → pulse, accent flash) as pure logic in whole ms
- [x] Shared parity vectors (`Instruments/metrodom_brain/test/vectors/*.vectors`: a body hex
      fixture + timed Note Ons, panics and state checks with expected outcomes, pulses and flash
      colors) run by both `make -C test` and the .NET tests
- [x] `metrodom virtual --body FILE` (repeatable): a port named after the body that strikes and
      refuses as the brain would, printing each strike's pulse and LED swatch, and each refusal's reason
- [x] `metrodom simulate SONG --body FILE`: the body in the band (governed, as `play`), plus a
      rerun straight to each brain (as a DAW) with refusals counted by reason
- [x] The governor and `BrainModel` agree: seeded random streams (dense, chords, mixed
      velocities) through the governor never reach a refusal in the model

### Phase N4: Records that check each other
- [x] `check_hardware.py` reads `Instruments/<id>/body.json` next to `instrument.toml`: same id,
      notes fit the drive modules' `outputs` (new field, required on drive modules) and PCA9685
      outputs sit within them, an actuator module (strike, press, pluck, damp) per note, a
      `body-backplane` for every body, `led-accent` for an `accent` block, and a `body.json` for
      every instrument with `brain-pico`
- [x] Clear messages that say which file and field to change; `scripts/test_check_hardware.py`
      (stdlib unittest, in the docs workflow) covers each rule, the template and the repo

### Phase N5: One-command firmware build and flash
- [x] `make -C Instruments/metrodom_brain build|flash|test` on `arduino-cli` (PATH, or the one
      inside the Arduino IDE), with the arduino-pico core 6.3.1 (its own Adafruit TinyUSB), MIDI
      Library 5.0.2 and Adafruit NeoPixel 1.15.5 pinned as `sketch.yaml` build profiles (`pico`, `pico2`)
- [x] First real compile, 2026-11-12 on macOS: both boards build with the pinned versions, no
      warnings from our code at `--warnings all`
- [x] CI job compiles both boards against the pinned core and keeps the UF2s as artifacts
      (not yet run on GitHub, TD-04)
- [x] `flash` waits for a Pico in BOOTSEL (`RPI-RP2` / `RP2350` drive on macOS and Linux) and
      copies the UF2 (`flash.sh`; tried against a fake drive), so no IDE is needed
- [ ] On hardware (needs plan 0002 H1): the M0 bench body flashes and plays from the make target

### Phase N6: Guided bench
- [x] `metrodom bench <PORT> [--onsets PORT]`: identify, then every note soft and hard with
      "heard it?" (both, soft only, hard only, neither, again); a soak at the limit through the
      governor, then "how warm is the coil?"; with `--onsets`, each note's latency measured and
      saved for `body write --calibrated`. Panic at the end and on Ctrl+C
- [x] Prints a Markdown bench table (`--out` saves it) for the README and build record
- [x] Runs end to end against the virtual brain in tests (scripted answers, virtual clock) and
      by hand against `virtual --body --onsets` on macOS
- [ ] On hardware with M2's snare

## Decisions log
- 2026-11-12: Plan written at the owner's request after a review of the new-instrument story.
  Order: N1 to N4 need no hardware and give the smooth first hour; N5 and N6 finish on real
  hardware. N3 is the core of the loop and gets the parity vectors so a software brain can be
  trusted as a stand-in.

- 2026-11-12: Owner decisions. (1) The scaffold is `metrodom instrument new <id>`: it makes the
  whole folder, and `body` stays for the EEPROM record (check, read, write). (2) One body per
  instrument at `Instruments/<id>/body.json`; accent LEDs stay an optional block in it, and
  variants wait until one is needed. (3) Firmware builds and flashes with `arduino-cli`, on the
  same core and libraries as the IDE steps, versions pinned in the repo. (4) `bench` uses an
  onset input when given one; without it, it asks "heard it?" and records latency as not
  measured (the M1 phone-recording fallback still applies).

- 2026-11-12: N1 and N2 built. `instrument new` takes flags only, no prompts: every option has a
  safe default, the result is meant to be edited, and flags keep it scriptable and testable.
  Prompts can come later if people miss them. The README template is the one in
  `Instruments/_template/`, embedded in the CLI, so the hand-copy and the command can't drift
  apart. The template's `instrument.toml` and `body.json` are hand-copy examples; the command
  writes its own from the request. `body check` with no file includes `_template/body.json`, so
  the example stays valid. The M1 body is copied to its instrument folder, not moved: the
  fixture pins the byte layout for both test suites, and the real body will diverge from it
  (measured latencies).

- 2026-11-12: N3 built. The model is a port of `Brain.cpp`'s decisions, not a second design:
  same integer units, same order of checks, same clamps, ticking before each Note On as the
  firmware's loop does. Parity vectors are plain text so the C++ test reads them without a JSON
  parser; they name the existing hex fixtures, which both suites already pin. `VirtualInstrument.ForBody`
  carries the model, so `virtual`, `simulate` and the onsets port all get it for free.
  `simulate --body` reports twice on purpose: governed (what `play` sends) and straight (what a
  DAW would hit), because the second is what you tune a body against. The governor's duty math
  uses exact time and the brain's whole milliseconds; in theory that leaves a window under 1 ms of
  drain where the governor passes a note the brain refuses. 2,000 random seeds at mixed and full
  velocity found none, and USB jitter on real hardware is larger, so the governor wasn't changed.
  Not modeled: USB loss, output faults and Body Write (hardware-only); a body written to a
  `--body` port changes its description, not its behavior; `--drift` is for `-i` instruments only.

- 2026-11-12: N4 built. The lint checks the body against the hardware, not the body's own rules
  (`metrodom body check` has those, from the firmware), so Python never re-implements them.
  Every body needs a `body-backplane`, GPIO ones too: the body lives in its EEPROM. Actuators
  are counted at least one per note, not exactly, so a frame built for an octave can play three
  notes while the rest are printed. The M1 body's id became `xylophone-solenoid` to match its
  instrument; the `xylophone-m1` fixtures stay as they are.

- 2026-11-12: N5 and N6 built. Pins live in `sketch.yaml` build profiles, which `arduino-cli`
  installs on its own, rather than in a setup script: one file says what a brain is built from,
  and bumping it is a reviewed change. 6.3.1, not 6.3.2: a core release under two weeks old
  isn't pinned. TinyUSB isn't pinned separately; it ships inside the core, and a second copy would
  clash. `flash` copies to the BOOTSEL drive instead of `arduino-cli upload`, because the
  brain's USB is MIDI with no serial port to reset through. `bench` takes a port, not `-o`, like
  `body write`, and builds its own governor: it drives one MetroDom brain, never a band. It can't
  test unplugging USB (the port goes with it), so the guide keeps that as a manual check.

- 2026-11-12: The first real compile worked once the library index was fresh: the Arduino
  IDE's cached index predated NeoPixel 1.15.5, and a profile can't install what the index
  doesn't list. `make build` now refreshes the index whenever `sketch.yaml` changes. The sketch
  needed no changes; the only warnings at `--warnings all` are in TinyUSB and NeoPixel.

## Open questions
(none)
