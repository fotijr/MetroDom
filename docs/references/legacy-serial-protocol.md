# Legacy serial protocol (2017 prototype)

Status: **Deprecated** by [ADR 0001](../design-docs/0001-midi-is-the-contract.md). Documented
so the existing code can be understood and ported. Do not extend it.

All links: USB serial, 9600 baud, 8N1, ASCII.

## Host (`MetroDom.Conductor/SerialService.cs`)
- Probes every COM port by sending `md?`.
- Expects newline-terminated lines: `Name:<text>` and `Id:<guid>`. Other lines are logged.
- Discovery waits ≥1.2 s per port. See bugs TD-01/TD-02 in the
  [tech-debt tracker](../exec-plans/tech-debt-tracker.md).
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

Bar angles are hard-coded in `MetroDom.Core/Song.cs` (`Note.A` = 22 … `Note.CHigh` = 143).
