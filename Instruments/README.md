# MetroDom instruments

**Building a new instrument? Start with the [new-instrument guide](../docs/references/new-instrument.md)**,
or just run:

```bash
metrodom instrument new snare --notes 38 --plays drum-kit
```

Each folder is one physical instrument, built from the same three things:

| File | What |
| --- | --- |
| `instrument.toml` | The shared hardware modules it's built from ([hardware/](../hardware/README.md), [ADR 0009](../docs/design-docs/0009-shared-hardware-modules.md)) |
| `body.json` | Its [body record](../docs/references/body-record.md): notes, outputs, pulses, limits. Written into the body's ID EEPROM |
| `README.md` | Wiring, power supply, bench results and measured latency |

[_template/](_template/README.md) holds those three files with example values. `metrodom instrument new` fills them in for you.

Every instrument runs the one **brain** firmware, a Pico that becomes whatever its **body** says
it is ([ADR 0006](../docs/design-docs/0006-swappable-instrument-brains.md)). The Conductor finds
instruments automatically ([ADR 0007](../docs/design-docs/0007-instrument-discovery.md)).

## Rules for instruments
1. Speak **MIDI**: class-compliant USB-MIDI preferred
   ([ADR 0001](../docs/design-docs/0001-midi-is-the-contract.md), [MIDI essentials](../docs/references/midi-essentials.md)).
2. Protect your own hardware: fixed strike pulse, duty limits, watchdog, all-off on boot and
   disconnect ([RELIABILITY.md](../docs/RELIABILITY.md)). The brain does all of this for you.
3. Describe yourself with a body record; the brain builds the
   [instrument profile](../docs/references/instrument-profile.md) from it, including **measured** per-note latency.
4. Ship a README with wiring, power supply, bench results and measured latency.

## Generic brain firmware
[metrodom_brain/](metrodom_brain/): the one firmware for every brain. USB-MIDI, fixed strike
pulses, duty and interval limits, panic, watchdog, Identity Reply, and the profile over SysEx. It
reads the body from its ID EEPROM. Host-tested; not yet flashed (TD-29).

## Instruments
| Folder | Body | Status |
| --- | --- | --- |
| [xylophone_solenoid/](xylophone_solenoid/) | [body.json](xylophone_solenoid/body.json): 3 bars on a PCA9685 (M1) | Planned on the brain; the 2017 sketch still uses the legacy protocol |
| [xylophone_servo/](xylophone_servo/) | none (legacy) | 2017 sketch: spin servo (pin 9) + strike servo (pin 10) |

Both 2017 sketches predate ADR 0001 and speak the [legacy ASCII protocol](../docs/references/legacy-serial-protocol.md).
They're being moved onto the brain firmware in [exec plan 0001](../docs/exec-plans/active/0001-foundation-reboot.md).
