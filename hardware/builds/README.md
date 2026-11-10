# Build records

One file per physical unit: `MD-<MODULE-OR-INSTRUMENT-ID>-<NNN>.md`, uppercase, three-digit
counter. Print the serial on a label and stick it on the unit. When something breaks at a gig,
the label leads straight to how it was built and tested.

Write the record in the same commit that decrements the used parts in
[stock.csv](../inventory/stock.csv). Photos go in `img/` (small JPEGs).
`scripts/check_hardware.py` checks that `Module` or `Instrument` names something that exists.

## Template

```markdown
# MD-STRIKER-SOLENOID-001

| Field | Value |
| --- | --- |
| Module | striker-solenoid |
| Revision | A |
| Built | 2026-11-10 |
| State | bench / in service / spare / retired |
| Installed in | MD-XYLOPHONE-SOLENOID-001 (or blank) |

## Bench test
| Test | Result |
| --- | --- |

## Measurements
Current, temperature, latency, max repeat rate.

## Deviations from the module design
Anything substituted or changed. If it should become the design, bump the module revision.

## Issues log
- YYYY-MM-DD: what happened, what was done.
```

## Records
None yet.
