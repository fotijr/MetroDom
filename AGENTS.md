# AGENTS.md

MetroDom plays **real, physical instruments with robotics**, driven by open standards (MIDI first).
It pushes back against click tracks and pre-recorded "live" music. The same system must work as a
one-person bar band, a crowd-controlled jukebox, or a rented quartet run from a phone app.

This file is a **map, not a manual**. Read the doc that matches your task, then go deeper from there.
`docs/` is the system of record: if it isn't written there, it isn't decided.

## Start here

| If you need to…                                 | Read                                                        |
| ----------------------------------------------- | ----------------------------------------------------------- |
| Understand why MetroDom exists and who it's for | [docs/PRODUCT_SENSE.md](docs/PRODUCT_SENSE.md)              |
| See the system's layers and boundaries          | [ARCHITECTURE.md](ARCHITECTURE.md)                          |
| Know the non-negotiable rules                   | [docs/design-docs/core-beliefs.md](docs/design-docs/core-beliefs.md) |
| Find a design decision (ADRs)                   | [docs/design-docs/index.md](docs/design-docs/index.md)      |
| Work on a user-facing scenario                  | [docs/product-specs/index.md](docs/product-specs/index.md)  |
| See what gets built next, and why               | [docs/ROADMAP.md](docs/ROADMAP.md)                          |
| Pick up or plan multi-step work                 | [docs/PLANS.md](docs/PLANS.md)                              |
| Know what's broken or shaky today               | [docs/exec-plans/tech-debt-tracker.md](docs/exec-plans/tech-debt-tracker.md) |
| Judge the maturity of each component            | [docs/QUALITY_SCORE.md](docs/QUALITY_SCORE.md)              |
| Handle timing, latency, failure, physical safety | [docs/RELIABILITY.md](docs/RELIABILITY.md)                 |
| Handle auth, roles, untrusted crowd input       | [docs/SECURITY.md](docs/SECURITY.md)                        |
| Look up MIDI / protocol details                 | [docs/references/index.md](docs/references/index.md)        |
| Build or modify an instrument (firmware)        | [Instruments/README.md](Instruments/README.md)              |
| Parts, inventory, hardware modules, builds      | [hardware/README.md](hardware/README.md)                    |
| Plan lights, LED strips, matrix displays        | [ADR 0011](docs/design-docs/0011-visuals-are-part-of-the-band.md) |
| Prototype without hardware (virtual band)      | [docs/references/virtual-band.md](docs/references/virtual-band.md) |
| Detect instruments or other MIDI devices        | [ADR 0007](docs/design-docs/0007-instrument-discovery.md)   |

## Repository layout

```
AGENTS.md              ← you are here (table of contents only)
ARCHITECTURE.md        ← layers, boundaries, current code map
src/MetroDom.Core/     ← C# library: routing, panic, song analysis. Pure logic, no OS I/O
src/MetroDom.Conductor/ ← `metrodom` CLI + OS MIDI adapters (ALSA, DryWetMIDI)
tests/                 ← xUnit v3 tests
Instruments/           ← Arduino firmware + per-instrument hardware notes
hardware/              ← parts catalog, inventory, shared modules, build records (ADR 0008)
docs/                  ← knowledge base (Markdown)
policies/              ← shipped policy profiles (live-band.json)
scripts/check_docs.py  ← knowledge-base linter (run before committing doc changes)
scripts/check_hardware.py ← hardware records linter (run before committing hardware changes)
```

## State of the project (read before assuming anything works)

The code is a **2016–2017 prototype being dusted off**. The target architecture in
[ARCHITECTURE.md](ARCHITECTURE.md) is *proposed* and mostly unbuilt. When code and docs disagree,
the docs describe intent and the code describes reality — check
[docs/QUALITY_SCORE.md](docs/QUALITY_SCORE.md) and say which one you're relying on.

## Build & check

```bash
python3 scripts/check_docs.py          # knowledge-base lint (links, orphans, AGENTS.md length)
python3 scripts/check_hardware.py      # parts/BOM/module/build consistency (--buildable: stock report)
dotnet build && dotnet test                      # .NET 10 SDK, any OS (Linux/macOS/Windows)
dotnet run --project src/MetroDom.Conductor -- inspect song.mid
make -C Instruments/metrodom_brain/test          # brain firmware host tests (any C++17 compiler)
```

CI: GitHub Actions (`.github/workflows/`): `docs` (lint) and `build` (Linux, macOS, Windows).
New Conductor code is modern C# on .NET LTS ([ADR 0002](docs/design-docs/0002-headless-conductor.md)).

## Working rules

1. **MIDI is the contract.** Anything that crosses the instrument boundary is MIDI. No new ad-hoc
   serial protocols. See [ADR 0001](docs/design-docs/0001-midi-is-the-contract.md).
2. **Safety beats music.** Actuators must fail silent. Never remove a watchdog, duty-cycle limit,
   or panic path. See [docs/RELIABILITY.md](docs/RELIABILITY.md).
3. **One architecture, many modes.** Bar band / jukebox / rental differ by policy and config, never
   by fork. See [ADR 0005](docs/design-docs/0005-deployment-modes-are-policy.md).
4. **Keep docs in the same change as code.** If you change behavior, update the doc that describes
   it, the quality score, and the tech-debt tracker. Run `scripts/check_docs.py`.
5. **Plans live in the repo.** Non-trivial work gets an exec plan in `docs/exec-plans/active/`.
6. **Keep this file short.** Add a row to a table above, not a paragraph. Details go in `docs/`.
7. **US English everywhere**: code, docs, comments, JSON fields, CLI text (color, behavior, center).
   Library names stay as they are (Spectre's `grey`).
