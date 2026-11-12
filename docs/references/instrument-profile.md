# Instrument profile (draft format)

Status: **Draft**. Defined by [ADR 0001](../design-docs/0001-midi-is-the-contract.md) and used
by the scheduler ([ADR 0004](../design-docs/0004-latency-compensation.md)).

Each instrument describes itself so the Conductor can arrange, schedule and protect it without
instrument-specific code. Today: a JSON file in the instrument's folder
(`Instruments/<name>/profile.json`). Instruments serve it over SysEx when asked
([profile exchange](midi-profile-exchange.md)); MIDI-CI Property Exchange may replace that later.
Parsed by `src/MetroDom.Core/InstrumentProfileJson.cs`.

```json
{
  "schema": "metrodom.instrument-profile/0",
  "id": "xylophone-solenoid",
  "name": "Xylophone (solenoid)",
  "firmwareVersion": "0.1.0",
  "midi": { "channel": 1, "identity": { "manufacturer": "7D", "family": 8781, "model": 1 } },
  "notes": [
    { "note": 60, "latencyMs": 9,  "minIntervalMs": 60, "pulseMs": 30 },
    { "note": 64, "latencyMs": 10, "minIntervalMs": 60, "pulseMs": 30 },
    { "note": 67, "latencyMs": 9,  "minIntervalMs": 60, "pulseMs": 30 }
  ],
  "polyphony": 3,
  "velocity": { "supported": false },
  "safety": { "maxOnMs": 45, "maxDutyPercent": 30, "dutyWindowMs": 1000, "watchdogMs": 500 }
}
```

Fields:
- `midi.identity`: what the instrument answers to a MIDI Identity Request. Family 8781 is
  MetroDom ("MD"), see [midi-identity.md](midi-identity.md).
- `notes[]`: the only notes the instrument can play. Latency (command → audible onset) is
  measured, not guessed. `minIntervalMs` is the fastest repeat of that note.
- `polyphony`: max actuators on at once (power-supply / actuator limit). Optional, at least 1.
- `pulseMs` (optional, per note): how long that actuator is on per strike (at velocity 127).
- `minPulseMs` (optional, per note): the strike at velocity 1, when the pulse scales with
  velocity; `velocity.supported` is then `true`. The Conductor counts `pulseMs` (the longest).
- `safety`: limits the **firmware enforces itself**. The Conductor uses them to avoid sending
  notes that would be refused. If present, `maxOnMs` (> 0) and `maxDutyPercent` (1-100) are
  required; `dutyWindowMs` (default 1000) sizes the burst: each actuator may spend
  `maxDutyPercent` of it on, draining at `maxDutyPercent`. A note without `pulseMs` counts as
  `maxOnMs` per strike. No `safety` means no duty limit (software instruments).

- `latencyMs` (optional, top level): latency for notes without their own. Default: the slowest note's.
- `minIntervalMs` (optional, top level or per note): the fastest repeat of a note. Each note
  keeps its own, as the firmware enforces it (a snare can roll faster than the kick next to it).
  The top-level value covers notes without their own; default: the slowest note's.
- No `notes` array means the instrument plays any note (software instruments).
- `midi.channel` (1-16) is where the [auto band](auto-band.md) puts the instrument when it isn't
  assigned, and the channel `-i NAME@profile.json` plays on.
- The Conductor reads profile files from the command line where a latency would go
  (`-i xylo@profile.json`, `-o "PORT@profile.json"`, [virtual band](virtual-band.md)); a file
  beats what the instrument sends.
- Measured beats declared: latencies from `metrodom calibrate` ([calibration](calibration.md))
  replace the profile's `latencyMs` values on this rig, unless the command line gives a latency
  or profile file.
- The arranger uses `polyphony` (with `pulseMs`/`maxOnMs` as how long each note holds an
  actuator) so it never hands an instrument more notes at once than it can strike.
- Not read by the Conductor yet: `midi.identity`, `velocity`, `safety.watchdogMs`, `firmwareVersion`.
  Whether an instrument is MetroDom comes from its Identity Reply, never from the profile.
  The [brain firmware](../../Instruments/metrodom_brain/README.md) enforces `polyphony`, `safety` and
  each note's `minIntervalMs` itself, and builds the profile it sends from the same table; the
  Conductor's governor mirrors those checks ([RELIABILITY](../RELIABILITY.md)).

Example values above are illustrative, not measured.
