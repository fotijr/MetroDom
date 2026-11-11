# 0001: Foundation reboot

Status: Active · Last updated: 2026-11-10

## Goal
Go from the 2017 prototype to the smallest version of the target
[architecture](../../../ARCHITECTURE.md) that can play a real song on two physical instruments
from a Standard MIDI File, with human-controlled tempo and working panic, on a headless box.
That is the first milestone of the [live band spec](../../product-specs/live-band.md).

## Context
- Sequenced by the [roadmap](../../ROADMAP.md): build in milestone order, not phase order
- Decisions: [ADR 0001–0005](../../design-docs/index.md) (all Proposed. Owner to accept)
- Current state: [QUALITY_SCORE.md](../../QUALITY_SCORE.md), [tech debt](../tech-debt-tracker.md)

## Phases

### Phase 0: Knowledge base & hygiene
- [x] AGENTS.md table of contents + `docs/` knowledge base
- [x] Docs lint (`scripts/check_docs.py`) in GitHub Actions
- [ ] Owner reviews and accepts/edits ADRs 0001–0005
- [x] Replace Travis with GitHub Actions
- [x] Migrate to .NET 10 SDK-style projects, cross-platform. Retire WinForms for the `metrodom` CLI
- [ ] Confirm `build` workflow runs green on GitHub

### Phase 1: Instruments speak MIDI
- [ ] Accept brain/body split ([ADR 0006](../../design-docs/0006-swappable-instrument-brains.md)). Define body connector + EEPROM layout
- [ ] Generic brain firmware (Pico, Arduino C++): USB-MIDI, Note On → fixed strike pulse, watchdog, all-off
- [ ] Port solenoid xylophone to a body (PCA9685/MOSFET + ID EEPROM). Hardware side: [exec plan 0002](0002-hardware-bootstrap.md)
- [ ] SysEx Identity Reply + [instrument profile](../../references/instrument-profile.md) read from body EEPROM
- [ ] Bench-test with a stock DAW/keyboard (no MetroDom software)
- [ ] Measure and record per-note latency

### Phase 2: Headless Conductor core
- [x] SDK-style `net10.0` Core + CLI + tests, `dotnet test` on Linux/macOS/Windows CI
- [x] Core: SMF analysis (DryWetMIDI), channel routing with safe mute, panic
- [x] Linux MIDI device adapter (ALSA sequencer), round trip tested (locally; hosted CI can't load the module, TD-18)
- [x] Core: arranger (song part → instrument by fit, transpose, fold, drop, thin). `metrodom arrange`, `--arrange` on `simulate`/`play`. Limits: TD-21
- [x] Core: latency-compensating scheduler with fake-clock tests (`LatencyScheduler`, `VirtualClock`)
- [x] Virtual band: `VirtualInstrument`, `metrodom simulate` (virtual clock), `metrodom virtual` (OS ports), `demo-song`. See [virtual band](../../references/virtual-band.md)
- [x] `play` drives real ports through the scheduler, several outputs with per-output channels and latency, re-timing on tempo change
- [x] Adapters: unplug goes silent, replug reconnects with a panic first. One failing output can't block others
- [ ] Measure ALSA latency on a Pi
- [ ] Discovery per [ADR 0007](../../design-docs/0007-instrument-discovery.md): MetroDom + general MIDI devices
- [x] Safety governor + panic, tested (`SafetyGovernor` in front of every output; `--max-velocity`, `--max-rate`, `--quiet`). Limits: TD-22
- [x] Tap tempo (terminal keys) and `--tempo`
- [ ] Tap from a MIDI input (foot pedal) / MIDI Clock in

### Phase 2b: Play-along ([ADR 0012](../../design-docs/0012-humans-in-the-band.md))
- [ ] Track roles (track names, GM drums channel, saved per-song mapping)
- [ ] Seats: a human takes a role, arranger mutes it (generalize `--mute`)
- [ ] Count-in on a chosen instrument. SMF marker sections. Vamp / jump to outro
- [ ] Foot controller mapping in the live-band policy (start/stop, next, tap, vamp, panic)
- [ ] Tempo tracker with beat prediction, tested with recorded human timings + fake clock
- [ ] Human-leads input from drum triggers (`trigger-piezo`)

### Phase 3: Control API v0
- [ ] OpenAPI spec: transport, queue, instruments, panic
- [ ] WebSocket state stream
- [ ] Minimal web UI served by the Conductor

## Decisions log
- 2026-11-10: Adopted harness-engineering style repo knowledge base. Architecture written as
  Proposed ADRs pending owner review.
- 2026-11-10: Conductor stays modern C# / .NET LTS (ADR 0002 accepted). Hardware priorities:
  ease of programming, cost, interchangeable brains (ADR 0006). Discovery keeps the `md?`
  auto-detect intent using standard SysEx Identity + general MIDI support (ADR 0007).
- 2026-11-10: Migrated to .NET 10 (latest stable, LTS. .NET 11 still RC). Must build and test
  on any OS. WinForms app and serial discovery deleted (git history keeps them). The Explorer
  and live-control ideas live on as `metrodom inspect/play/route`.

- 2026-11-10: CLI moved from a hand-written parser to Spectre.Console(.Cli): per-command help
  and validation, typed options with short forms (`-o`, `-m`, `-i`), a port picker when
  `--output`/`--input` is omitted in a terminal, and a live status panel for `play`/`route`
  (progress, 16-channel activity, connection state, warnings). Piped/CI runs stay plain text.
  A full-screen TUI (e.g. Terminal.Gui) is deferred: per ADR 0002 it should be a Control API
  client, not a second in-process controller.

- 2026-11-10: Linux MIDI talks to the ALSA sequencer directly (P/Invoke to libasound) rather
  than RtMidi: no native library to build per architecture, and the sequencer sees USB devices,
  interfaces, soft synths and virtual ports alike.

- 2026-11-10: Virtual band for roadmap S1. Two layers: an in-process simulation on a virtual
  clock (deterministic, CI-friendly, proves the scheduler math) and OS virtual MIDI ports (proves
  the device path with stock tools). Virtual instruments mimic the generic firmware: strike on
  Note On, Note Off ignored, drop unplayable notes, refuse too-fast repeats. Result: compensation
  takes the default band's spread from 25 ms to 0 ms, and under 4 ms with ±2 ms jitter. The
  scheduler polls (`Pump` at `NextDue`) instead of owning a thread, so the same code runs on a
  fake clock in tests and on a real clock later.
- 2026-11-10: `play` moved from DryWetMIDI `Playback` to `LatencyScheduler` + `SchedulerLoop`.
  Events stay in song time; a speed factor (× the file's tempo) re-anchors at the current
  position, so tap tempo works without re-reading the file and respects tempo maps. Outputs
  became `-o PORT[=CHANNELS][@LATENCY_MS]`, repeatable. The first real-time run had beat one
  13 ms late (cold send path); a panic before starting (start from silence) plus a 250 ms
  pre-roll fixed it. Measured ≤ 1 ms between a 10 ms and a 35 ms virtual instrument.
- 2026-11-11: Arranger first cut. A part is one track on one channel. Each instrument gets at
  most one part, chosen greedily by how many of its notes it can play after the best octave
  shift; channel 10 (GM percussion) pairs only with percussion instruments and is never
  transposed. Out-of-range notes fold to the same pitch class, else drop; repeats faster than
  `MinInterval` are thinned. The arranged timeline puts each part on its instrument's first
  channel, so routing, mutes and the scheduler are unchanged. Instrument notes are given as
  `/NOTES` on the CLI until profile files and discovery exist. Pinning parts waits for roles.
- 2026-11-11: Safety governor is an `IMidiOutput` decorator per instrument, so the orchestra,
  scheduler and live routing all go through it without knowing it exists. It only ever holds
  back or turns down new notes (Note On, velocity > 0); everything else passes, so panic can't be
  blocked. A velocity cap sends a copy because the scheduler shares event objects between
  instruments. Policy limits are flags until policy profiles (ADR 0005) exist. Measured: demo song
  with `--max-rate 3 --max-velocity 50` into `virtual` never exceeded 3 strikes in any second
  and nothing louder than 50; silent quiet hours sent no notes but both panics.

## Open questions
- Target box model (ADR 0002).
