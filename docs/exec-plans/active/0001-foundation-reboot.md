# 0001: Foundation reboot

Status: Active · Last updated: 2026-11-10

## Goal
Go from the 2017 prototype to the smallest version of the target
[architecture](../../../ARCHITECTURE.md) that can play a real song on two physical instruments
from a Standard MIDI File, with human-controlled tempo and working panic, on a headless box.
That is the first milestone of the [live band spec](../../product-specs/live-band.md).

## Context
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
- [ ] Port solenoid xylophone to a body (PCA9685/MOSFET + ID EEPROM)
- [ ] SysEx Identity Reply + [instrument profile](../../references/instrument-profile.md) read from body EEPROM
- [ ] Bench-test with a stock DAW/keyboard (no MetroDom software)
- [ ] Measure and record per-note latency

### Phase 2: Headless Conductor core
- [x] SDK-style `net10.0` Core + CLI + tests, `dotnet test` on Linux/macOS/Windows CI
- [x] Core: SMF analysis (DryWetMIDI), channel routing with safe mute, panic
- [x] Linux MIDI device adapter (ALSA sequencer), round trip tested in CI
- [ ] Core: arranger (track → instrument), scheduler with fake-clock tests
- [ ] Adapters: hot-plug (TD-16). Measure ALSA latency on a Pi
- [ ] Discovery per [ADR 0007](../../design-docs/0007-instrument-discovery.md): MetroDom + general MIDI devices
- [ ] Safety governor + panic, tested
- [ ] Tap tempo / MIDI Clock in

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

- 2026-11-10: Linux MIDI talks to the ALSA sequencer directly (P/Invoke to libasound) rather
  than RtMidi: no native library to build per architecture, and the sequencer sees USB devices,
  interfaces, soft synths and virtual ports alike.

## Open questions
- Target box model (ADR 0002).
