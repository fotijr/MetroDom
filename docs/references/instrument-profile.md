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
    { "note": 60, "latencyMs": 9,  "minIntervalMs": 60 },
    { "note": 64, "latencyMs": 10, "minIntervalMs": 60 },
    { "note": 67, "latencyMs": 9,  "minIntervalMs": 60 }
  ],
  "polyphony": 3,
  "velocity": { "supported": false },
  "safety": { "maxOnMs": 45, "maxDutyPercent": 30, "watchdogMs": 500 }
}
```

Fields:
- `midi.identity`: what the instrument answers to a MIDI Identity Request. Family 8781 is
  MetroDom ("MD"), see [midi-identity.md](midi-identity.md).
- `notes[]`: the only notes the instrument can play. Latency (command → audible onset) is
  measured, not guessed. `minIntervalMs` is the fastest repeat of that note.
- `polyphony`: max simultaneous notes (power-supply / actuator limit).
- `safety`: limits the **firmware enforces itself**. The Conductor uses them to avoid sending
  notes that would be refused.

- `latencyMs` (optional, top level): latency for notes without their own. Default: the slowest note's.
- `minIntervalMs` (optional, top level or per note): the Conductor uses one interval per
  instrument today, the largest given, so no note is refused.
- No `notes` array means the instrument plays any note (software instruments).
- `midi.channel` (1-16) is where the [auto band](auto-band.md) puts the instrument when it isn't assigned.
- Not read yet: `midi.identity`, `polyphony`, `velocity`, `safety`, `firmwareVersion` (TD-22).

Example values above are illustrative, not measured.
