# 0008: Hardware as code: how hardware lives in the repo

Status: **Proposed** (2026-11-10)

## Context
Software decisions are easy to keep in the repo: Markdown, Mermaid, code, tests, a linter.
Hardware usually lives in someone's head, a vendor cart, and a drawer. MetroDom needs agents and
humans to answer, from the repo alone: *what parts exist, what do I own, what is each instrument
built from, which modules are shared, and what was actually built (and how well it worked)?*

The owner is strong in software and new to hardware, so the format should borrow software
habits (packages, interfaces, lockfiles, CI) wherever they fit.

## Decision
Hardware is described in **plain-text, diffable files** under `hardware/`, validated by
`scripts/check_hardware.py` in CI. Binary/proprietary formats are allowed only as exports or
vendor files, never as the only source.

### Four kinds of record
| Record | Software analogy | Where | Format |
| --- | --- | --- | --- |
| **Part**: a thing you can buy (what it *is*) | package registry entry | `hardware/parts/catalog.csv` | CSV, one row per part, slug `id` |
| **Stock**: what you physically have (how many, which bin) | local cache | `hardware/inventory/stock.csv` | CSV: `part_id, qty, location, notes` |
| **Module**: a reusable assembly with defined interfaces | library | `hardware/modules/<id>/` | `module.toml` + `bom.csv` + `README.md` (+ CAD) |
| **Build**: one physical unit you made, with its label | deployment / lockfile | `hardware/builds/<serial>.md` | Markdown with a fixed header table |

Instruments are compositions of modules: `Instruments/<name>/instrument.toml` lists the
modules (and quantities) it uses, next to its firmware and profile.

### Formats by concern
| Concern | Source format (committed) | Why |
| --- | --- | --- |
| Block diagrams, power & signal topology | **Mermaid** in the module/instrument README | Same tool as software docs, renders on GitHub |
| Cable harnesses & connector pinouts | **WireViz** YAML (later) | Text in, diagram + cable BOM out |
| Schematics & PCBs | **KiCad** (later, once a perfboard design is proven) | Open, text-based files, `kicad-cli` exports in CI |
| Printed / machined parts | **Code CAD**: OpenSCAD `.scad` (default) or build123d `.py` | Parametric, diffable, agents can edit it |
| Vendor models, datasheets | Link in `catalog.csv`. Commit vendor STEP only if no stable URL | Avoid bloating git |
| Photos of builds | `hardware/builds/img/`, small JPEGs (≤ 500 KB) | A photo of the wiring is the best wiring doc for a noob |
| Exports (STL, Gerber, PNG renders) | Generated, not committed (CI artifact or release asset) | One source of truth |

### Identity rules
- Part and module ids are **lowercase slugs** (`pca9685-breakout`, `striker-solenoid`).
  Readable by humans and agents. Never renamed. Deprecate instead.
- Modules carry a **revision** (`A`, `B`, …). Bump it when the interface or BOM changes.
  An interface change is a breaking change: note it in the module README.
- Every physical unit gets a **serial** printed on a label: `MD-<MODULE-ID>-<NNN>`, e.g.
  `MD-STRIKER-SOLENOID-003`. Its build record says which revision it is and how it tested.

### Linting (`scripts/check_hardware.py`, stdlib only)
1. Catalog ids are unique slugs. Stock and BOM rows reference existing parts.
2. Every module has `module.toml` (id, name, revision, status, interfaces) matching its folder.
3. Module interfaces name only connectors defined in
   [hardware/interfaces.md](../../hardware/interfaces.md).
4. Instrument `instrument.toml` references only existing modules.
5. Build records reference an existing module or instrument.

## Consequences
- An agent can answer "can I build another striker with what's in the drawer?" by joining
  `bom.csv` with `stock.csv`. The checker can print that report.
- Inventory goes stale unless updated when parts are bought or used. Rule: update `stock.csv`
  in the same commit as a build record, like docs in the same change as code.
- CSV and TOML need no dependencies (Python stdlib has `csv` and `tomllib`), so the checker
  runs anywhere CI does.
- KiCad/WireViz are deferred until there is something worth drawing. Mermaid + photos first.

## Alternatives considered
- **Inventory apps (InvenTree, PartKeepr):** powerful, but state lives outside the repo and
  agents can't diff it. Could import the CSV later if the parts count explodes.
- **YAML everywhere:** needs a third-party parser for the checker. CSV renders as a table on
  GitHub and opens in any spreadsheet. TOML is stdlib.
- **Spreadsheets in Google Sheets:** not versioned with the design, no review.

## Open questions
- OpenSCAD vs build123d as the default code-CAD tool. Leaning OpenSCAD for printed brackets
  (simplest, largest example corpus), build123d if parts get complex.
- Git LFS for photos and vendor STEP files if they grow.
