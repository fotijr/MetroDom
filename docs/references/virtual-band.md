# Virtual band: prototyping without hardware

Status: **Built** (roadmap [S1](../ROADMAP.md), [exec plan 0001](../exec-plans/active/0001-foundation-reboot.md) Phase 2).
Code: `src/MetroDom.Core/Simulation/`, `src/MetroDom.Core/Timing/`, `metrodom simulate|virtual|demo-song`.

The Conductor runs ahead of the hardware by playing to **virtual instruments**: software
stand-ins that behave like the generic firmware ([ADR 0006](../design-docs/0006-swappable-instrument-brains.md))
and have a declared latency, like a real [instrument profile](instrument-profile.md).

## Two ways to use it

| | `metrodom simulate` | `metrodom virtual` |
| --- | --- | --- |
| What | Whole band in-process on a **virtual clock** | Instruments as real **OS MIDI ports** |
| Speed | Instant, same result every run | Real time |
| Proves | Scheduler, alignment, routing, panic | The CLI and devices path, DAWs, keyboards |
| Where | Any OS, CI | Linux (ALSA), macOS (CoreMIDI). Windows: use loopMIDI |

```bash
metrodom simulate                                   # built-in demo, default band, compensation on
metrodom simulate --no-compensation                 # see the 25 ms smear compensation fixes
metrodom simulate song.mid -i xylo=1@10 -i drums=10@35 --jitter 2 --notes
metrodom demo-song demo.mid                         # the demo as a file for play/DAWs
metrodom virtual                                    # terminal 1: ports "MetroDom xylo", "MetroDom drums"
metrodom play demo.mid -o "MetroDom xylo"           # terminal 2
```

Instrument spec: `NAME[=CHANNELS]@LATENCY_MS`. Channels are 1-16 with ranges (`1-9,11-16`),
omitted means all. Default band (roadmap S1): `xylo` on every channel except 10 at 10 ms,
`drums` on 10 at 35 ms.

## What a virtual instrument does

- Note On → one strike, **heard** at arrival + latency (± `--jitter`, seeded). Note Off is
  ignored: the strike pulse ends by itself, so nothing can stick on.
- Drops notes it has no actuator for (`InstrumentProfile.Notes`) and refuses repeats faster
  than `MinInterval`, both reported. `virtual` only listens on its channels.
- Counts panic (CC 120/123), so tests can check panic reached every instrument.

## What the simulation measures

`LatencyScheduler` sends each event to each instrument at
`songTime + lookahead − latency(instrument, note)`, where lookahead is the slowest latency in the
band ([ADR 0004](../design-docs/0004-latency-compensation.md)). `BandSimulation` reports, per
instrument, how far each strike landed from where it should be heard, and the **worst spread**
between instruments meant to sound together. The proposed budget is ≤ 5 ms.

Demo song, default band: compensation on → 0 ms. Off → 25 ms. On with ±2 ms jitter → under 4 ms.

## Not yet (see [tech debt](../exec-plans/tech-debt-tracker.md))

- `metrodom play` still uses DryWetMIDI playback, not the scheduler: no compensation on real ports yet.
- Tempo is fixed when the song loads. Tap tempo and live tempo changes don't re-time yet (TD-20).
- Latency comes from the command line, not profile files or SysEx discovery.
