# {{name}}

`{{id}}` · plays {{plays}} · status: planned

One paragraph: what this instrument is, what it plays in the band, and what's unusual about it.
Built with the [new-instrument guide](../../docs/references/new-instrument.md).

## Files

| File | What |
| --- | --- |
| `instrument.toml` | The shared modules it's built from ([hardware/](../../hardware/README.md)) |
| `body.json` | Its body record: notes, outputs, pulses, limits ([body record](../../docs/references/body-record.md)). Written into the body's EEPROM with `metrodom body write` |

## Notes

{{notes}}

## Wiring

Brain ↔ [body-backplane](../../hardware/modules/body-backplane/README.md) over Qwiic. Which
PCA9685 channel drives which actuator, and where each actuator sits on the instrument. A labeled
photo once it's built.

## Power

Supply voltage and current, fuse, and the kill switch ([power-entry](../../hardware/modules/power-entry/module.toml)).
Never power actuators from the brain.

## Bench results

From `metrodom bench` (or by hand until it exists). Copy into the build record too.

| Note | Heard | Latency | Fastest clean repeat | Notes |
| --- | --- | --- | --- | --- |
| | | not measured | | |

Coil temperature after 1 minute at the duty limit:

## Known limits

Anything it can't do yet, and what would break it.
