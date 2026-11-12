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
   - Built in the generic brain ([metrodom_brain](../Instruments/metrodom_brain/README.md), host-tested, not
     yet flashed): fixed pulse capped at `maxOnMs`, per-actuator minimum interval and a leaky-bucket
     duty budget (refuse, never queue), polyphony cap, panic on CC 120/123 on any channel and System
     Reset, all off when USB unmounts, hardware watchdog restart with all off at boot. Ceilings no
     body can raise (250 ms pulse, 50% duty). An output that doesn't confirm "off" (a PCA9685 that
     stopped answering) blocks every new strike and is retried each loop. A body EEPROM with a bad
     record boots a silent brain, never the bench pin map. On a PCA9685 body the brain holds the
     chip's `/OE` low only while it's healthy: a fault, lost USB, a body write or a reset (the pin
     floats and a pull-up wins) disables every channel at once. A Body Write turns everything off first
     and nothing strikes until the restart ([body record](references/body-record.md)). The legacy
     sketches have none of this (TD-03).
2. **Conductor safety governor** (`SafetyGovernor`, one in front of every output in `play` and `route`)
   - Panic (CC 120/123 + MetroDom SysEx all-off) to every instrument, bypassing queues. Built:
     instruments that answered as MetroDom get the one-message all-off `F0 7D 4D 44 03 F7` first,
     then everyone gets CC 120/123 on all 16 channels. `metrodom panic` and the panic on replug
     send CCs only (no identity to go on; the brain boots all-off anyway).
   - Rate limits, velocity caps and quiet hours from the active policy profile. Built as flags
     until policy profiles exist: `--max-rate N` (strikes per second per output, extra notes held
     back), `--max-velocity N` (louder notes turned down, never dropped), `--quiet 23:00-07:00[@VEL]`
     (local time, may cross midnight; no new notes, or capped at VEL).
   - Rejects notes outside an instrument's declared range or minimum inter-note interval. Built:
     notes and each note's own min interval from the profile the instrument sends (or
     `-o PORT.../NOTES`), and no repeat while the actuator is still in its pulse.
   - Holds back what the firmware would refuse for heat or power, from the profile's `safety`
     and `polyphony`: the same per-actuator duty bucket as the brain, and at most `polyphony`
     actuators inside their pulse at once. A note without `pulseMs` is assumed to cost `maxOnMs`.
   - Only new notes are ever held back or changed. Note Off, panic and every other message pass,
     so the governor can never leave an actuator on. Held-back counts show in the status panel
     and are printed when the run ends.
3. **Lights**
   - Flashing capped at 3 per second unless the venue policy enables strobe (photosensitive epilepsy).
     Built for reactive lights: a fixture starts at most 3 pulses a second, over an idle glow
     that is never dark. Cues (lights track, pad) change the look at most 3 times a second (a
     faster cue waits), and the master slews at most full range per third of a second. The
     policy's `lights` section may lower that rate, or raise it (up to 10) only with `strobe`
     on; `strobe` channels are 0 otherwise and always in quiet hours.
   - Panic → steady safe look, sent at once from the panic path after the actuators are silent.
     The end of a set also shows the safe look before the sACN stream terminates. The master
     fader doesn't dim it. Built in `LightShow` (`play --lights`).
   - No robot plays a cue: under `--lights` the rig's channel (16) is taken from every instrument.
   - Brightness cap and quiet hours from the policy (`lights.maxBrightness`, `lights.quietHours`),
     never below 10%, so the safe look stays visible. Built in `LightLimits`.
   - LEDs on their own fused 5 V supply with software brightness caps.
4. **Humans near robots**
   - Actuators within a player's reach are guarded. A robot never plays the drum or string a human is playing.
   - Panic reachable by foot during play-along.
5. **Control surfaces**
   - Panic always visible to owner/operator roles. Never available to guests.

## Failure modes to design for
| Failure | Expected behavior |
| --- | --- |
| Instrument USB unplugged mid-song | Firmware watchdog silences it. Conductor drops that port's events (found within ~1 s, or at once if a send fails), song continues on others. On replug it reconnects by port name and sends panic before resuming. Built in `ResilientPorts.cs` (firmware watchdog still TD-03) |
| Conductor crash | Firmware watchdogs silence everything. Service supervisor restarts the Conductor. Resume is optional |
| Wi-Fi / internet loss | Playback and queue continue. Apps reconnect and resync state over WebSocket |
| Power brownout | Instruments boot to all-off. Conductor boots to "ready, not playing": `serve` restores the queue but always waits for start before its first song, even under a policy with `queue.autoplay` |
| (Long-term) Transport: vibration, heat, bumped connectors | Mechanisms locked for travel, strain-relieved leads, PETG/ASA prints. Self-check sweep after setup flags anything out of adjustment ([ADR 0010](design-docs/0010-road-ready-instruments.md)) |
| Light node or fixture fails | Logged and shown in health. Music keeps playing. Panic sends lights to a steady safe look, not blackout ([ADR 0011](design-docs/0011-visuals-are-part-of-the-band.md)) |
| Tempo tracker loses a human player (fill, pause, wrong hit) | Hold the last stable tempo, never lurch. Operator can switch to band-leads by foot ([ADR 0012](design-docs/0012-humans-in-the-band.md)) |
| Untrusted song file (huge, malformed) | Parsed in isolation with limits. Rejected without affecting the current song |

## Testing expectations
- Scheduler and governor are unit-tested with a **fake clock** and **fake instruments**.
- Each firmware ships with a bench test sketch (the prototype has
  `Instruments/xylophone_servo/xylophone_servo - tester.ino` as an early example).
- Latency is measured, not assumed: `metrodom calibrate` ([calibration](references/calibration.md))
  measures it on the rig, and `--onsets` checks it before each set. Record bench measurements in
  the instrument's README.
