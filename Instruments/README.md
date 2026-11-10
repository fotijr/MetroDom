# MetroDom instruments

Each folder is one physical instrument: firmware, wiring notes, (soon) a `profile.json`, and an
`instrument.toml` listing the shared hardware modules it is built from
([hardware/](../hardware/README.md), [ADR 0009](../docs/design-docs/0009-shared-hardware-modules.md)).

Target design: a swappable **brain** (Pico-footprint board running one generic firmware) plugs
into an instrument-specific **body** that stores its own profile
([ADR 0006](../docs/design-docs/0006-swappable-instrument-brains.md)). The Conductor finds
instruments automatically ([ADR 0007](../docs/design-docs/0007-instrument-discovery.md)).

## Rules for instruments
1. Speak **MIDI**: class-compliant USB-MIDI preferred
   ([ADR 0001](../docs/design-docs/0001-midi-is-the-contract.md), [MIDI essentials](../docs/references/midi-essentials.md)).
2. Protect your own hardware: fixed strike pulse, duty limits, watchdog, all-off on boot and
   disconnect ([RELIABILITY.md](../docs/RELIABILITY.md)).
3. Describe yourself with an [instrument profile](../docs/references/instrument-profile.md),
   including **measured** per-note latency.
4. Ship a bench-test sketch and a README with wiring, power supply and measured latency.

## Current instruments (legacy serial protocol)
| Folder | Actuation | Protocol |
| --- | --- | --- |
| [xylophone_solenoid/](xylophone_solenoid/) | 3 solenoids via MOSFETs, pins 9–11 | [legacy ASCII](../docs/references/legacy-serial-protocol.md) |
| [xylophone_servo/](xylophone_servo/) | Spin servo (pin 9) + strike servo (pin 10) | [legacy ASCII](../docs/references/legacy-serial-protocol.md) |

Both predate ADR 0001 and are tracked for porting in
[exec plan 0001](../docs/exec-plans/active/0001-foundation-reboot.md).
