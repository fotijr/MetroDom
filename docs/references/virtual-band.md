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
metrodom play demo.mid -o "MetroDom xylo=1-9,11-16@10" -o "MetroDom drums=10@35"   # terminal 2
```

Instrument spec: `NAME[=CHANNELS]@LATENCY_MS[/NOTES]`. Channels are 1-16 with ranges (`1-9,11-16`),
omitted means all. Notes are numbers or names (middle C = 60 = C4) with ranges: `/60,64,67`,
`/C4-C6`, `/36,38,76`; omitted means any note. `play -o` takes the same `=CHANNELS@LATENCY/NOTES`
after the port name. Default band (roadmap S1): `xylo` on every channel except 10 at 10 ms,
`drums` on 10 at 35 ms.

## Arranging a song onto the band

Real robots have few notes, so a song's channels rarely line up with them. `--arrange` (on
`simulate` and `play`, or `metrodom arrange` to just look) hands each instrument the part it
covers best, on its first channel. Channel 10 instruments only take drum parts.

```bash
metrodom arrange song.mid -i xylo=1@10/60,64,67 -i drums=10@35/36,38,76
metrodom play song.mid --arrange -o "MetroDom xylo=1@10/60,64,67" -o "MetroDom drums=10@35/36,38"
```

Rules (`Arranger`): a part is one track on one channel. Greedy pairing, most playable notes
first, one part per instrument. Pitched parts move by whole octaves to fit, then leftover notes
fold to the nearest playable note of the same name (`--no-fold` drops them), then repeats faster
than the instrument's minimum interval are thinned. Only notes are kept. Every change is counted
in the table. Played to note-limited `virtual` instruments, an arranged song skips nothing.

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

## Real time: `metrodom play` uses the same scheduler

`play` takes several outputs, each `PORT[=CHANNELS][@LATENCY_MS]`, and drives them through
`LatencyScheduler` on a real clock (`SchedulerLoop`: sleeps, then spins the last 2 ms; 16 ms on
Windows). It panics every output first (start from silence, and warms the send path so beat one
isn't late), then starts after a 250 ms pre-roll.

Tempo comes from a human: `--tempo BPM` to start, then in the terminal **Space/T** taps
(`TapTempo`: average of the last 4 intervals, resets after 2 s, ignores double-triggers),
**+/-** nudge 2%, **0** returns to the file's tempo. Events stay in song time and are re-timed
from the current position on every change ([ADR 0004](../design-docs/0004-latency-compensation.md)).
Latency is mechanical, so it is never scaled.

Measured on macOS (`play` → `virtual`, demo song, 10 ms + 35 ms): drums sent ~24 ms before the
xylophone, heard within 1 ms of each other (the log's resolution) on every beat, at 100 and 150 BPM.

## Not yet (see [tech debt](../exec-plans/tech-debt-tracker.md))

- Tap tempo from a MIDI pedal or MIDI Clock in. The keys are covered only by unit tests of `TapTempo` and `SetSpeed`, not by driving a terminal (TD-20).
- `route` (live input) ignores latency: ADR 0004's "delay to the slowest" isn't built.
- Latency comes from the command line, not profile files or SysEx discovery. Virtual instruments
  do answer MIDI Identity Requests as MetroDom model 0 ([midi-identity](midi-identity.md)), so
  `metrodom devices --identify` finds them.
