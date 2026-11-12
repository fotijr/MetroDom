# 0009: Shared hardware modules and connector standards

Status: **Proposed** (2026-11-10)

## Context
Every instrument is different on the outside, but robotic instruments repeat a small set of
jobs: **strike, press, pluck, damp, position**. The 2016 prototypes each used their own pins,
wiring and mounts. [ADR 0006](0006-swappable-instrument-brains.md) already makes the *brain*
interchangeable. This ADR extends the same idea to the rest of the hardware so that what you
learn and build for one instrument carries to the next.

The biggest lever for interchangeability is not the parts, it is the **interfaces**: the
connectors, voltages and mounting pattern. Fix those early and modules become swappable.

## Decision

### 1. Instruments are built from modules, grouped by job
| Layer | Module (planned id) | Job | Reused by |
| --- | --- | --- | --- |
| Brain | `brain-pico` | Generic firmware, USB-MIDI, safety ([ADR 0006](0006-swappable-instrument-brains.md)) | Every instrument |
| Body logic | `body-backplane` | Pico socket, Qwiic, ID EEPROM, PCA9685, one connector per channel | Every instrument |
| Power | `power-entry` | DC input, fuse, switch, kill switch, actuator rail + logic rail | Every instrument |
| Drive | `driver-mosfet-8ch` | Low-side switching with flyback diodes, hardware pulse limit later | Solenoid instruments |
| Actuate | `striker-solenoid` | Push solenoid + return + mallet, adjustable height | Xylophone, glockenspiel, drums, bells, cowbell |
| Actuate | `striker-servo` | Servo arm + mallet for slower, quieter or velocity-shaped strikes | Hand percussion, cymbal |
| Actuate | `presser-solenoid` | Same solenoid, pusher tip instead of mallet | Keyboard keys, buttons |
| Actuate | `damper-solenoid` | Same solenoid, felt pad that mutes a bar or string | Vibraphone, strings |
| Frame | `frame-2020` | 2020 aluminum extrusion + printed adapter plates on a 20 mm grid | Every instrument |
| Transport | `case-road` | Road case, connector panel, transport locks ([ADR 0010](0010-road-ready-instruments.md)) | Long-term, if instruments travel |
| Transport | `rig-head` | 19" rack: Conductor, power strip, hub, master kill | Long-term, if instruments travel |
| Control | `trigger-piezo` | Clip-on piezo drum triggers → MIDI, for following a human drummer ([ADR 0012](0012-humans-in-the-band.md)) | Owner's drum set. Shares the piezo approach with `latency-jig` |
| Control | `footswitch-midi` | Stomp switches on a brain running the generic firmware | Play-along |
| Measure | `latency-jig` | Piezo/mic on a Pico that times command → sound onset | Filling every profile's `latencyMs` |

Rule of thumb: **one actuator family, many tips.** A solenoid striker, presser and damper share
the solenoid, driver channel, connector, bracket and firmware driver. Only the tip changes.

### 2. Interface standards (the contract between modules)
Defined once in [hardware/interfaces.md](../../hardware/interfaces.md); modules reference them
by name. Initial proposal:

| Interface | Standard | Notes |
| --- | --- | --- |
| `usb-midi` | USB to the brain | Data + 5 V for logic ([ADR 0001](0001-midi-is-the-contract.md)) |
| `qwiic` | JST-SH 4-pin, 3.3 V I²C | Brain ↔ body, body ↔ I²C add-ons ([ADR 0006](0006-swappable-instrument-brains.md)) |
| `rail-12v` | 12 V actuator rail, 5.5×2.5 mm barrel ≤ 5 A, XT30 above | Fused at `power-entry`. Never shares a wire with logic |
| `act-2p` | JST-XH 2-pin, one per actuator channel | Solenoid / motor. Pin 1 = +rail, pin 2 = switched low side |
| `servo-3p` | Standard 0.1" servo header (GND, V+, signal) | PCA9685 native |
| `mount-2020` | 2020 extrusion, M5 T-nuts, 20 mm hole grid on adapter plates | Every printed bracket bolts to it |

### 3. A module is "done" when
It has a BOM that resolves against the catalog, a wiring diagram (Mermaid or photo), a bench
test with pass/fail criteria, at least one build record, and (for actuators) measured latency
and max safe repeat rate.

## Consequences
- The first instrument costs the most. The second solenoid instrument is mostly a new frame,
  a new profile and more of the same striker.
- Choosing 12 V and JST-XH now limits very strong solenoids (24 V) and > 3 A channels. Those
  get a new interface name (`rail-24v`, `act-2p-hc`) rather than overloading the existing one.
- Hardware safety backstops ([RELIABILITY.md](../RELIABILITY.md)) are designed once in
  `driver-mosfet-8ch` and `power-entry`, then inherited by every instrument.

## Alternatives considered
- **Bespoke wiring per instrument:** faster for instrument #1, and how the prototype was built.
  Every later instrument starts from zero and spares don't fit.
- **Off-the-shelf solenoid/servo HATs only:** fine inside a module, but HATs don't define the
  mechanical or power interfaces, which is where most of the reuse comes from.

## Open questions
- 12 V vs 24 V as the default actuator rail. Leaning 12 V: cheaper solenoids and supplies, easier
  to source, safer for a beginner.
- Whether `body-backplane` starts as perfboard (learn) or goes straight to a small KiCad PCB.
