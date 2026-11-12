# Latency calibration (`metrodom calibrate`, `--onsets`)

[ADR 0004](../design-docs/0004-latency-compensation.md): latency is measured, not guessed. A full
calibration measures every note on demand; a quick check before each set flags drift and changes
nothing. Code: `src/MetroDom.Core/Calibration/` (`LatencyProbe`, `LatencyCheck`,
`CalibrationBook`), `src/MetroDom.Conductor/Commands/Calibrator.cs` and `CalibrateCommand.cs`.
Limits: TD-30 in the [tech-debt tracker](../exec-plans/tech-debt-tracker.md).

```bash
metrodom virtual --onsets                                               # virtual band + a "MetroDom onsets" port
metrodom calibrate -o "MetroDom xylo" -o "MetroDom drums=10" --onsets "MetroDom onsets"
metrodom calibrate --onsets "trigger" --onsets-on note@10 --onset-latency 2  # a piezo on a trigger module
metrodom play set.mid --onsets "trigger"                                # quick check, then the set
metrodom serve ~/songs --onsets "trigger"                               # quick check at boot
```

## Onsets are MIDI

The Conductor hears the band through a MIDI input that sends a Note On for every onset
([ADR 0001](../design-docs/0001-midi-is-the-contract.md)): a piezo on a drum trigger module, a
mic into a trigger-to-MIDI box, or `virtual --onsets`. Any Note On counts unless `--onsets-on`
narrows it (same syntax as `--tap-on`). `--onset-latency MS` takes the input's own delay (trigger
scan, detection) off every measurement. There's no audio onset detection in the Conductor.

## How a run measures

- One instrument at a time, one strike at a time, on the output's first channel at velocity 100,
  through its safety governor. Round after round: every note once, then again (`--repeats`,
  default 5; 3 for the quick check).
- An onset belongs to the latest strike if it arrives within the window after it (`--window`,
  default 500 ms; raise it for servos). The first onset counts. Later ones in the same window are
  **double** onsets (bounce, retrigger), and onsets outside any window are **strays** (noise,
  crosstalk). Both are counted and ignored.
- Strikes are spaced by window + 150 ms settle, and wider if the note's `minIntervalMs` or duty
  limit (`pulseMs` × 100 / `maxDutyPercent`) needs it, so the firmware never refuses a probe.
  There's a 300 ms lead-in before the first strike, and anything heard then is a stray.
- A note's latency is the **median** of its heard strikes, so one bad onset can't move it.
  "Spread" is slowest minus fastest. The instrument's overall latency is the median of its notes.
- Notes: every note in the profile; for an instrument that plays any note, one note (snare on
  channel 10, middle C elsewhere). The quick check uses the lowest, middle and highest.

## Where results go (`metrodom.calibrations/0`)

`calibrate` saves to `$METRODOM_CALIBRATIONS`, else `metrodom/calibrations.json` next to the
[remembered assignments](auto-band.md) (`--calibrations FILE` on every output command). It's keyed
by output port name, because a measurement belongs to that instrument on this rig, with its own
mic and cables. Notes never heard keep their old latency. A run that heard nothing saves nothing
and exits 1.

```json
{ "schema": "metrodom.calibrations/0",
  "calibrations": [ { "port": "MetroDom xylo", "measuredAt": "2026-11-12T04:31:21-05:00",
                      "latencyMs": 13.18, "notes": [ { "note": 60, "latencyMs": 13.5 } ] } ] }
```

`play`, `serve` and `route` (and devices joining mid-set) use it: **measured beats declared**
(what the instrument's profile says), and the command line (`@LATENCY`, `@PROFILE.json`) beats
both. A line says so: `MetroDom xylo: using the latencies calibrated 2026-11-12 04:31 (13.2 ms).`

To carry a measurement to other Conductors, write it into the instrument:
`metrodom body write PORT --calibrated` puts this file's latencies for that port into the body's
record, so the profile the brain sends declares them ([body record](body-record.md)). This rig's
file still wins here.

## The quick check (`play`/`serve --onsets`, `calibrate --quick`)

It runs before the first song and before any other input opens, so the onset input may share a
port with `--follow` or `--tap`. Verdict per instrument:

| Status | Meaning |
| --- | --- |
| `ok` | Every note heard within 5 ms (the alignment budget) of the latency the band plays with |
| `drifted` | A note is off by more than 5 ms: "Run metrodom calibrate" |
| `partial` | Some notes never heard: an actuator or the onset input may be failing |
| `silent` | Nothing heard: unpowered, unplugged, or out of the onset input's reach |

Problems go to the messages (terminal, remote, `/stream`). Each instrument's verdict is in
`GET /instruments` as `latencyCheck`, and the remote shows it ("drifted +8.5 ms, recalibrate").
Nothing is changed and the set goes on. A missing onset input skips the check with a message.

## Measured (macOS, virtual band)

- `virtual -i xylo=1@12/60,64,67 -i drums=10@35/36,38 --onsets --jitter 1`, then `calibrate
  --repeats 3`: xylo 12.5–13.5 ms per note, drums 35.5–35.9 ms. That's the jitter plus about
  0.5–1 ms of CoreMIDI transport, both ways. The first strike of the run was once 20 ms late
  (warm-up) and the median ignored it.
- Restarted with `--drift 8`, then `play --onsets`: both instruments flagged `drifted` (+9.5 and
  +8.1 ms) in 10 s, then the set played.
