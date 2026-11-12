# Fabrication

How parts that aren't bought get made, and who can make them. The owner has no 3D printer, so
fabrication is a **trip or an order**, and the goal is to make each one count.

| File | What |
| --- | --- |
| [capabilities.csv](capabilities.csv) | Every process we can reach (`process`) and every place that does it (`provider`), with limits and cost |
| [queue.csv](queue.csv) | Jobs waiting to be made. Batch them before a library trip or an online order |

Modules declare which processes they need in `module.toml` (`fabrication = ["3d-print-fdm"]`).
`scripts/check_hardware.py` checks that every process exists in `capabilities.csv`, and
`--fab-queue` prints queued jobs grouped by process with the providers that can do them.

## Batching workflow
1. Finishing a CAD part adds a row to `queue.csv`: `status = queued`.
2. Before a trip or order, run `python3 scripts/check_hardware.py --fab-queue`. Pick a provider,
   give all chosen rows the same `batch` id (e.g. `2026-11-library-1`), set `status = batched`.
3. When parts come back, mark them `done` and add printed parts to `stock.csv` like bought parts.
4. Print **one test copy** of anything new before batching ten of them. Fit is rarely right first time.

`status`: `queued` · `batched` · `done` · `canceled`.

## Design rules (so any provider can make it)
- **Fit the smallest printer we can reach.** Until the library's specs are filled in, keep
  every printed part inside **180 × 180 × 180 mm**. Split bigger parts and bolt them together.
- **PLA is fine for home builds.** If instruments ever travel, parts that ride in a vehicle move
  to PETG or ASA, since PLA softens around 55–60 °C
  ([ADR 0010](../../docs/design-docs/0010-road-ready-instruments.md), long-term).
- Use heat-set inserts or captive nuts, not screws into plastic, for anything taken apart often.
- Name exports after the module and part (`striker-solenoid_bracket.stl`) so a batch of 20 files
  from a library USB stick can be sorted out again.
