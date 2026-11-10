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
- [ ] Replace Travis with a GitHub Actions build + test workflow

### Phase 1: Instruments speak MIDI
- [ ] Pick the standard board for new instruments (ADR 0001 open question)
- [ ] Port solenoid xylophone to USB-MIDI: Note On → fixed strike pulse, watchdog, all-off
- [ ] SysEx Identity Reply + [instrument profile](../../references/instrument-profile.md) JSON
- [ ] Bench-test with a stock DAW/keyboard (no MetroDom software)
- [ ] Measure and record per-note latency

### Phase 2: Headless Conductor core
- [ ] Decide Conductor runtime (ADR 0002 open question)
- [ ] Core: SMF loading, arranger (track → instrument), scheduler with fake-clock tests
- [ ] Adapters: OS MIDI in/out, discover instruments via Identity Request
- [ ] Safety governor + panic, tested
- [ ] Tap tempo / MIDI Clock in

### Phase 3: Control API v0
- [ ] OpenAPI spec: transport, queue, instruments, panic
- [ ] WebSocket state stream
- [ ] Minimal web UI served by the Conductor (replaces WinForms Explorer for daily use)

## Decisions log
- 2026-11-10: Adopted harness-engineering style repo knowledge base. Architecture written as
  Proposed ADRs pending owner review.

## Open questions
- Conductor runtime and target box (ADR 0002).
- Keep the WinForms Conductor as a dev tool or retire it at Phase 3?
