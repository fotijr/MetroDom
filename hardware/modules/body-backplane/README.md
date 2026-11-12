# body-backplane (rev A)

The logic board every instrument body carries: a socket for the brain (Pico), the Qwiic bus,
the ID EEPROM that tells the brain which instrument it is, and a PCA9685 that drives up to 16
actuator channels through a driver board ([ADR 0006](../../../docs/design-docs/0006-swappable-instrument-brains.md),
[ADR 0009](../../../docs/design-docs/0009-shared-hardware-modules.md)). First user: the M1 xylophone.

## Interfaces
| Interface | Role | Notes |
| --- | --- | --- |
| `qwiic` | data | I²C0 of the brain: SDA GPIO 4, SCL GPIO 5, 3.3 V, 400 kHz |
| `usb-midi` | data | The brain's USB, passed through to the case |

## What the firmware expects
The [brain firmware](../../../Instruments/metrodom_brain/README.md) relies on these; change them
only together with it.

| Part | Address / pin | Why |
| --- | --- | --- |
| 24LC32A EEPROM (`eeprom-24lc32`) | `0x50` (A0-A2 to GND) | Holds the [body record](../../../docs/references/body-record.md) |
| EEPROM `WP` | 10 kΩ to 3.3 V, jumper to GND | Protected by default. Fit the jumper only while running `metrodom body write` |
| PCA9685 | `0x40` (no address jumpers), or what the body record says | Each channel full on or full off; outputs totem-pole |
| PCA9685 `/OE` | to GND | The firmware doesn't drive it; outputs power up off |
| Driver inputs (MOSFET gates) | 10 kΩ pull-down each | Off while the PCA9685 is unpowered or the brain is out of its socket |
| I²C pull-ups | 4.7 kΩ to 3.3 V, once on the bus | |

No EEPROM answering at `0x50` makes the brain use its compiled-in bench body (GPIO 15). A body
with an EEPROM that holds no valid record gets a silent brain, never the bench body.

## Bench test
| Test | Pass when |
| --- | --- |
| Blank EEPROM | `metrodom devices --identify` shows `MetroDom brain (body: blank)` and no notes |
| Write with the jumper off | `metrodom body write` says the EEPROM didn't keep it; the brain still answers as before |
| Write with the jumper on | It answers as the new body within 5 s; remove the jumper |
| Pull the Qwiic cable mid-song | The brain refuses new notes until the PCA9685 answers again; nothing stays on |

## Known limits & safety
- Not built yet (status `planned`). The firmware side is host-tested only (TD-29).
- A PCA9685 holds its outputs if the brain hangs mid-strike. The watchdog restarts the brain
  within `watchdogMs` (500 ms) and boot turns every channel off; until then the driver board's own
  pulse limit (ADR 0006 §5) is the backstop. Wiring `/OE` to a brain pin with a pull-up (outputs
  enabled only while the firmware holds it low) would close that gap: open question in
  [plan 0005](../../../docs/exec-plans/active/0005-brain-for-m1.md).

## Revision history
- A: first version (perfboard).
