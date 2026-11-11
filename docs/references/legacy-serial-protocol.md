# Legacy serial protocol (2017 prototype)

Status: **Deprecated** by [ADR 0001](../design-docs/0001-midi-is-the-contract.md). Documented
so the existing firmware can be understood and ported. Do not extend it. The host side
(`MetroDom.Conductor/SerialService.cs`, `MetroDom.Core/Song.cs`) was deleted on 2026-11-10.
See git history at commit `266b6b5`.

All links: USB serial, 9600 baud, 8N1, ASCII.

## Host (removed; was `MetroDom.Conductor/SerialService.cs`)
- Probes every COM port by sending `md?`.
- Expects newline-terminated lines: `Name:<text>` and `Id:<guid>`. Other lines are logged.
- Discovery waited ≥1.2 s per port and could loop forever if a port never answered `Name:`.
  Superseded by [ADR 0007](../design-docs/0007-instrument-discovery.md).
- Live MIDI → serial mapping (in `LiveControlForm.cs`, commented out): note 60→`0`, 64→`4`, 67→`7`.

## Solenoid xylophone (`Instruments/xylophone_solenoid/xylo_solenoid.ino`)
| Byte | Action |
| --- | --- |
| `0` | Pin 9 solenoid |
| `4` | Pin 10 solenoid |
| `7` | Pin 11 solenoid |
| other | ignored |

All bytes received in one `serialEvent` fire together (chord), stay on 45 ms, then all go off.
Boot message: `Serial tester ready.`. Prints debug lines for every strike.

## Servo xylophone (`Instruments/xylophone_servo/xylophone_servo.ino`)
| Input | Action |
| --- | --- |
| digits | Accumulate a target angle |
| `m` | Move spin servo (pin 9) to accumulated angle |
| `p` | Strike: strike servo (pin 10) to 16°, wait 75 ms, back to 12° |
| `?` | Reply `Xylophone \| Servo` (no newline, no `Name:` prefix) |

Bar angles (spin servo degrees) were hard-coded in the old `Song.cs`. Preserved here for the port:

| A | B | C | D | E | F | G | A (high) | B (high) | C (high) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 22 | 40 | 55 | 64 | 75 | 92 | 103 | 112 | 127 | 143 |
