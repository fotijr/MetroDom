# Quality score

Honest grades per component, so agents know what to trust. Update in the same change that moves
a grade. Scale: **A** production-ready · **B** solid, minor gaps · **C** works, fragile or
untested · **D** prototype/partial · **F** stub or broken · **—** not started.

Last reviewed: 2026-11-11

| Component | Grade | Tests | Docs | Notes |
| --- | --- | --- | --- | --- |
| Instrument: xylophone (solenoid) | D | Bench only | Partial | 3 notes, custom ASCII protocol, debug prints on serial |
| Instrument: xylophone (servo) | D | Tester sketch | Partial | Different protocol from solenoid version. `?` reply lacks `Name:` |
| Instrument firmware standard (USB-MIDI, watchdog) | — | — | Spec only | [ADR 0001](design-docs/0001-midi-is-the-contract.md), [0006](design-docs/0006-swappable-instrument-brains.md) |
| Hardware records (catalog, inventory, modules, builds) | D | `scripts/check_hardware.py` (CI) | [ADR 0008](design-docs/0008-hardware-as-code.md) | Scaffolded. Stock empty, one equipment row, six musical instruments (details to fill in), fabrication capabilities need library printer specs, all modules planned, no builds |
| Road-ready cases & setup | — | — | [ADR 0010](design-docs/0010-road-ready-instruments.md) | Long-term goal, deliberately deferred. Home band first |
| Visuals: accent LEDs, stage lights, matrix | — | — | [ADR 0011](design-docs/0011-visuals-are-part-of-the-band.md) | [Exec plan 0003](exec-plans/active/0003-visuals.md) |
| Play-along (seats, count-in, foot control, tempo following) | D | Unit (tap tempo) | [ADR 0012](design-docs/0012-humans-in-the-band.md) | Seeds: `play --mute`, `--tempo`, tap/nudge keys. No pedal, seats or count-in yet |
| Shared hardware modules (striker, driver, power, frame) | — | — | [ADR 0009](design-docs/0009-shared-hardware-modules.md) | [Exec plan 0002](exec-plans/active/0002-hardware-bootstrap.md) |
| Instrument discovery (Identity Request, general MIDI) | D | Unit (bytes, fake devices), CoreMIDI round trip (local) | [ADR 0007](design-docs/0007-instrument-discovery.md), [midi-identity](references/midi-identity.md) | `devices --identify` asks every output at once (≤ 250 ms). `virtual` answers as MetroDom. No profile fetch, hot-plug identify or remembered assignments; never run against hardware or ALSA (TD-23) |
| Core: safety governor (`SafetyGovernor`) | C | Unit (fake clock), CLI, measured `play` → `virtual` | [RELIABILITY](RELIABILITY.md) | In front of every output in `play`/`route`: instrument notes and min interval, `--max-velocity`, `--max-rate`, `--quiet`. Note Offs and panic always pass. No duty-cycle/polyphony limit, no MetroDom SysEx all-off, limits from flags not policy profiles (TD-22) |
| Core: routing, mute, panic (`Orchestra`) | C | Unit | ARCHITECTURE | Per-output routing for the scheduler. `play`/`route` take several outputs, each with its own channels |
| Core: song analysis (`SongAnalyzer`) | C | Unit | ARCHITECTURE | Read-only analysis for `inspect` |
| Core: arranger (`Arranger`) | D | Unit, CLI, `play --arrange` → note-limited `virtual` (0 skipped) | [virtual band](references/virtual-band.md) | One part per instrument by fit. No roles, seats, pins, polyphony limit or drum-note substitution yet (TD-21) |
| CLI: `inspect` | C | Unit (exit codes) | `--help` | Any OS. Spectre.Console tables |
| CLI: `devices` / `play` / `route` / `panic` | D | ALSA loopback (local only, skipped on hosted CI: TD-18), unit (validation, status panel) | `--help` | Linux (ALSA), macOS, Windows. `play` goes through the scheduler with several outputs and live tempo keys (keys not yet exercised by an automated test). Live status panel and port prompt in a terminal, plain lines when piped. Survives unplug/replug (unit tested with a fake backend). Not yet tested against a real instrument (TD-15) |
| Scheduler / latency compensation | C | Unit (fake clock), real-time loop test (logic exact everywhere; 5 ms precision bound only off CI, hosted runners wake threads 30+ ms late), measured `play` → `virtual` | [ADR 0004](design-docs/0004-latency-compensation.md), [virtual band](references/virtual-band.md) | Drives `play`. Re-times on tempo change. Measured ≤ 1 ms between instruments on macOS virtual ports. Not yet on hardware or a Pi. Live input (`route`) not compensated |
| Virtual band (`VirtualInstrument`, `simulate`, `virtual`, `demo-song`) | C | Unit, CLI, CoreMIDI round trip (local), ALSA path untested (TD-19) | [virtual band](references/virtual-band.md) | `virtual` checked by hand on macOS with `play` driving it from another process. No Windows virtual ports (loopMIDI) |
| Control API | — | — | ADR | [ADR 0003](design-docs/0003-control-api.md) |
| Policy / roles | — | — | ADR | [ADR 0005](design-docs/0005-deployment-modes-are-policy.md) |
| Control apps (owner, jukebox, rental) | — | — | Specs | [product-specs](product-specs/index.md) |
| CI | C | — | — | GitHub Actions: docs lint + build/test on Linux, macOS, Windows. Not yet run on GitHub (TD-04) |
| Knowledge base | B | `scripts/check_docs.py` | This repo | New. Keep it gardened |
