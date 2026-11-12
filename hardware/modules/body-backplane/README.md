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
| EEPROM `WP` | 10 kΩ to 3.3 V, momentary button (`switch-momentary`) to GND, on the outside of the case | Protected unless the button is held. Hold it while `metrodom body write` runs |
| PCA9685 | `0x40` (no address jumpers), or what the body record says | Each channel full on or full off; outputs totem-pole |
| PCA9685 `/OE` | Brain GPIO 6, 10 kΩ to 3.3 V | Outputs enabled only while the firmware holds it low. A resetting or unplugged brain leaves it pulled high: every channel off at once. The firmware also lets go of it on a fault, when USB is lost and after a body write |
| Driver inputs (MOSFET gates) | 10 kΩ pull-down each | Off while the PCA9685 is unpowered or the brain is out of its socket |
| I²C pull-ups | 4.7 kΩ to 3.3 V, once on the bus | |
| Accent strip data (optional) | Brain GPIO 7 to a [led-accent](../led-accent/README.md) | Only on a body whose record has `accent` |

No EEPROM answering at `0x50` makes the brain use its compiled-in bench body (GPIO 15). A body
with an EEPROM that holds no valid record gets a silent brain, never the bench body.

## Bench test
| Test | Pass when |
| --- | --- |
| Blank EEPROM | `metrodom devices --identify` shows `MetroDom brain (body: blank)` and no notes |
| Write without holding the button | `metrodom body write` says the EEPROM didn't keep it; the brain still answers as before |
| Write holding the button | It answers as the new body within 5 s |
| Pull the Qwiic cable mid-song | The brain refuses new notes until the PCA9685 answers again; nothing stays on |
| Hold the brain in reset (RUN to GND) mid-strike | `/OE` goes high and the channel is off at once, not after the watchdog |

## Known limits & safety
- Not built yet (status `planned`). The firmware side is host-tested only (TD-29).
- A brain that hangs with its loop stuck but `/OE` still driven low leaves PCA9685 outputs as
  they were until the watchdog restarts it (within `watchdogMs`, 500 ms); a reset or a pulled brain
  cuts them at once through the `/OE` pull-up. The driver board's own pulse limit (ADR 0006 §5)
  is the last backstop.

## Revision history
- A: first version (perfboard).
