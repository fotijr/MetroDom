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
- `pulseMs` (optional, per note): how long that actuator is on per strike.
- `safety`: limits the **firmware enforces itself**. The Conductor uses them to avoid sending
  notes that would be refused. If present, `maxOnMs` (> 0) and `maxDutyPercent` (1-100) are
  required; `dutyWindowMs` (default 1000) sizes the burst: each actuator may spend
  `maxDutyPercent` of it on, draining at `maxDutyPercent`. A note without `pulseMs` counts as
  `maxOnMs` per strike. No `safety` means no duty limit (software instruments).

- `latencyMs` (optional, top level): latency for notes without their own. Default: the slowest note's.
- `minIntervalMs` (optional, top level or per note): the Conductor uses one interval per
  instrument today, the largest given, so no note is refused.
- No `notes` array means the instrument plays any note (software instruments).
- `midi.channel` (1-16) is where the [auto band](auto-band.md) puts the instrument when it isn't assigned.
- Not read by the Conductor yet: `midi.identity`, `velocity`, `safety.watchdogMs`, `firmwareVersion`.
  Whether an instrument is MetroDom comes from its Identity Reply, never from the profile.
  The [brain firmware](../../Instruments/metrodom_brain/README.md) enforces `polyphony`, `safety` and
  each note's `minIntervalMs` itself, and builds the profile it sends from the same table; the
  Conductor's governor mirrors those checks ([RELIABILITY](../RELIABILITY.md)).

Example values above are illustrative, not measured.
