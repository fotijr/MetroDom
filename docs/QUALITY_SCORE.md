# Quality score

Honest grades per component, so agents know what to trust. Update in the same change that moves
a grade. Scale: **A** production-ready · **B** solid, minor gaps · **C** works, fragile or
untested · **D** prototype/partial · **F** stub or broken · **—** not started.

Last reviewed: 2026-11-10

| Component | Grade | Tests | Docs | Notes |
| --- | --- | --- | --- | --- |
| Instrument: xylophone (solenoid) | D | Bench only | Partial | 3 notes, custom ASCII protocol, debug prints on serial |
| Instrument: xylophone (servo) | D | Tester sketch | Partial | Different protocol from solenoid version. `?` reply lacks `Name:` |
| Instrument firmware standard (USB-MIDI, watchdog) | — | — | Spec only | [ADR 0001](design-docs/0001-midi-is-the-contract.md), [0006](design-docs/0006-swappable-instrument-brains.md) |
| Instrument discovery (Identity Request, general MIDI) | — | — | ADR | [ADR 0007](design-docs/0007-instrument-discovery.md) |
| Core: routing, mute, panic (`Orchestra`) | C | Unit | ARCHITECTURE | No scheduler/latency yet. Single output per run in the CLI |
| Core: song analysis (`SongAnalyzer`) | C | Unit | ARCHITECTURE | Read-only analysis. No arranger yet |
| CLI: `inspect` | C | Smoke (manual) | `--help` | Any OS |
| CLI: `devices` / `play` / `route` / `panic` | D | ALSA loopback (CI) | `--help` | Linux (ALSA), macOS, Windows. No hot-plug (TD-16). Not yet tested against a real instrument (TD-15) |
| Scheduler / latency compensation | — | — | ADR | [ADR 0004](design-docs/0004-latency-compensation.md) |
| Control API | — | — | ADR | [ADR 0003](design-docs/0003-control-api.md) |
| Policy / roles | — | — | ADR | [ADR 0005](design-docs/0005-deployment-modes-are-policy.md) |
| Control apps (owner, jukebox, rental) | — | — | Specs | [product-specs](product-specs/index.md) |
| CI | C | — | — | GitHub Actions: docs lint + build/test on Linux, macOS, Windows. Not yet run on GitHub (TD-04) |
| Knowledge base | B | `scripts/check_docs.py` | This repo | New. Keep it gardened |
