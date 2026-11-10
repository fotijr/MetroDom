# Reliability & physical safety

A missed note is a bug. A solenoid left energized is a fire risk. Safety outranks music.

## Timing budgets (proposed, to be validated by measurement)
| Path | Budget | Notes |
| --- | --- | --- |
| Inter-instrument alignment, pre-composed song | ≤ 5 ms | Achieved by lookahead ([ADR 0004](design-docs/0004-latency-compensation.md)) |
| Live controller → sound | ≤ 20 ms target, 30 ms max | Includes slowest-instrument alignment |
| Control API command → effect (play/skip) | ≤ 250 ms | Humans tolerate this for UI actions |
| Panic → all actuators off | ≤ 50 ms | Must not wait on queue, network or UI |

Transport reference points: a 3-byte MIDI message takes ~1 ms on DIN MIDI (31,250 baud) and
~3 ms on the legacy 9600-baud serial link. USB-MIDI is effectively sub-millisecond per message
but is polled (1 ms full-speed frames).

## Safety layers (defense in depth)
1. **Firmware (always on, never trusts the host)**
   - Max on-time per actuator (e.g. solenoid strike pulse is a fixed short pulse no matter how
     long Note On lasts).
   - Duty-cycle / thermal limit per actuator. Refuse or thin notes when exceeded.
   - Watchdog: no host traffic or USB disconnect for N ms means all actuators off.
   - All-off on boot and on reset.
2. **Conductor safety governor**
   - Panic (CC 120/123 + MetroDom SysEx all-off) to every instrument, bypassing queues.
   - Rate limits, velocity caps and quiet hours from the active policy profile.
   - Rejects notes outside an instrument's declared range or minimum inter-note interval.
3. **Control surfaces**
   - Panic always visible to owner/operator roles. Never available to guests.

## Failure modes to design for
| Failure | Expected behavior |
| --- | --- |
| Instrument USB unplugged mid-song | Firmware watchdog silences it. Conductor marks it offline, song continues on others |
| Conductor crash | Firmware watchdogs silence everything. Service supervisor restarts the Conductor. Resume is optional |
| Wi-Fi / internet loss | Playback and queue continue. Apps reconnect and resync state over WebSocket |
| Power brownout | Instruments boot to all-off. Conductor boots to "ready, not playing" |
| Untrusted song file (huge, malformed) | Parsed in isolation with limits. Rejected without affecting the current song |

## Testing expectations
- Scheduler and governor are unit-tested with a **fake clock** and **fake instruments**.
- Each firmware ships with a bench test sketch (the prototype has
  `Instruments/xylophone_servo/xylophone_servo - tester.ino` as an early example).
- Latency is measured, not assumed. Record measurements in the instrument's README.
