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
metrodom play demo.mid -o "MetroDom xylo=1-9,11-16" -o "MetroDom drums=10"   # terminal 2
```

Instrument spec: `NAME[=CHANNELS]@LATENCY_MS[/NOTES]`. Channels are 1-16 with ranges (`1-9,11-16`),
omitted means all. Notes are numbers or names (middle C = 60 = C4) with ranges: `/60,64,67`,
`/C4-C6`, `/36,38,76`; omitted means any note. `play -o` takes the same `=CHANNELS@LATENCY/NOTES`
after the port name. Leave out `@LATENCY` or `/NOTES` and `play` asks the instrument for its
profile ([profile exchange](midi-profile-exchange.md)) and uses what it sends; what you do write
wins. `--no-identify` skips the question. Without any `-o`, `play` uses every MetroDom
instrument it finds, each on the first channel of its spec, and picks up ones started mid-set at
the next song ([auto band](auto-band.md)).

A [profile file](instrument-profile.md) can stand where the latency goes: `-i xylo@Instruments/xylophone/profile.json`
or `-o "General MIDI kit=10@kit-profile.json"`. It gives notes, latencies, minimum interval,
polyphony and safety limits, as if the instrument had sent it (and beats what it does send). `-i`
takes the profile's `midi.channel` unless `=CHANNELS` says otherwise; a missing or invalid file is
a usage error naming it. Default band (roadmap S1): `xylo` on every channel except 10 at 10 ms,
`drums` on 10 at 35 ms.

## A body's brain, in software (`--body`)

```bash
metrodom virtual --body Instruments/snare/body.json            # a "MetroDom snare" port
metrodom simulate song.mid --body Instruments/snare/body.json  # what it plays and refuses, instantly
metrodom simulate song.mid -i xylo=1@10 --body Instruments/snare/body.json --arrange
```

`--body FILE` (repeatable, on `virtual` and `simulate`) stands up a virtual brain running that
[body](body-record.md). It decides exactly as the firmware does (`BrainModel`): its notes only,
its `midi.channel` if it has one, a pulse scaled by velocity, and refusals for a note still
striking, a repeat under `minIntervalMs`, a spent duty budget, or too many at once. Accent LEDs
flash in the note's color. Panic ends strikes, but their intervals still count. The same
[parity vectors](../../Instruments/metrodom_brain/test/vectors/README.md) run against the firmware and
the model, so they can't drift apart.

- `virtual --body` names the port after the body (`"name"`, the USB name a real brain shows), so
  `play -o "MetroDom snare"` and `devices --identify` treat it like the real instrument. Each
  strike prints its pulse and an LED swatch; each refusal prints why. It describes itself with
  the profile the brain would send.
- `simulate --body` adds the body to the band. Omni bodies hear every channel; with `--arrange`
  they get their own channel or the first free one, as the [auto band](auto-band.md) picks. The
  band table shows what `play`'s governor would hold back. A second section, **Played straight
  to each brain**, reruns the song with no governor (as from a DAW) and counts what each brain
  refuses, by reason: that's where to look when tuning `pulseMs`, `minIntervalMs` and the duty.
- Without `-i`, only the bodies play.

`virtual --onsets` adds a `MetroDom onsets` port that sends a Note On whenever a strike would be
heard, standing in for a piezo or mic ([calibration](calibration.md)). `--drift MS` makes every
instrument strike that much later than its profile says, to try the boot check, and `--jitter MS`
scatters strikes randomly.

## Arranging a song onto the band

Real robots have few notes, so a song's channels rarely line up with them. `--arrange` (on
`simulate` and `play`, or `metrodom arrange` to just look) hands each instrument the part it
covers best, on its first channel. Channel 10 instruments only take drum parts.

```bash
metrodom arrange song.mid -i xylo=1@10/60,64,67 -i drums=10@35/36,38,76
metrodom play song.mid --arrange -o "MetroDom xylo=1" -o "MetroDom drums=10"   # notes from the instruments
```

Rules (`Arranger`): a part is one track on one channel. Greedy pairing, most playable notes
first, one part per instrument. Pitched parts move by whole octaves to fit, then leftover notes
fold to the nearest playable note of the same name (`--no-fold` drops them), then repeats faster
than the instrument's minimum interval are thinned. Drum hits the kit lacks play on a stand-in
(closed hi-hat → pedal or open hat, then ride; crash → another cymbal; a tom → the nearest tom
the kit has; `DrumMap`), and stand-ins count when choosing the kit's part. With `polyphony`, no
more notes sound at once than the instrument has actuators: a note holds one for its `pulseMs`
(else `safety.maxOnMs`, else its length). Chords keep the top voice, then the bass; drum hits
keep kick, snare, hi-hat/ride, crashes, toms, in that order. Only notes are kept. Every change
is counted in the table ("on a stand-in", "over polyphony N"). Played to note-limited `virtual` instruments, an arranged song skips nothing.

## What a virtual instrument does

- Note On → one strike, **heard** at arrival + latency (± `--jitter`, seeded). Note Off is
  ignored: the strike pulse ends by itself, so nothing can stick on.
- Drops notes it has no actuator for (`InstrumentProfile.Notes`) and refuses repeats faster
  than `MinInterval`, both reported. `virtual` only listens on its channels. A `--body`
  instrument refuses everything a brain would instead (busy, interval, duty, polyphony).
- Counts panic (CC 120/123), so tests can check panic reached every instrument.

## What the simulation measures

Notes go through a `SafetyGovernor` per instrument (profile rules only, no policy limits), as in
`play`, so the report's **Held** column is what `play` would hold back, with the reasons under the
table ("4 too soon", "2 not playable"). A note that still gets past the governor and is dropped
or refused by the instrument shows in red: the governor and the firmware disagree.

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

From MIDI (one at a time):
- `--tap PORT` taps from an input: a foot pedal, pad or drum trigger, alongside the Space key
  (same `TapTempo`). `--tap-on` picks what counts: `any` (default: Note On, or a controller going
  from below 64 to 64+), `note[:N]`, `cc[:N]`, `pc[:N]`, optionally `@CHANNEL`. A pedal's press
  is one tap, its release none. Channel mode messages (CC 120+) never tap.
- `--clock-in PORT` follows MIDI Clock (`ClockTempo`): average of the last 24 ticks (one beat),
  first tempo after half a beat, changes under 0.5 BPM ignored so USB jitter doesn't keep
  re-timing. Stop (`FC`) or a 250 ms gap holds the current tempo. Start/Continue are ignored.

```bash
metrodom play song.mid -o "MetroDom xylo" --tap "FS-5U" --tap-on cc:64
metrodom play song.mid -o "MetroDom xylo" --clock-in "Drum Machine"
```

Measured on macOS (`play` → `virtual`, demo song at 100 BPM, generator on a virtual port):
strike spacing 0.600 s at file tempo, 0.400 s following a 150 BPM clock, 0.800 s from a pedal
at 75 BPM.

Measured on macOS (`play` → `virtual`, demo song, 10 ms + 35 ms): drums sent ~24 ms before the
xylophone, heard within 1 ms of each other (the log's resolution) on every beat, at 100 and 150 BPM.

## Count-in and song sections

- `--count-in BARS` (1-8) strikes a real instrument before the song: `--click CHANNEL/NOTE`,
  default `10/76` (woodblock on the drums channel). Bar ones at velocity 110, other beats 80, at
  the file's starting tempo and meter (6/8 counts eighths). The strikes go through the scheduler
  and governor like any note: latency-compensated, follow tap tempo, dropped if that instrument
  can't play the note.
- Sections come from SMF **marker** events (`SongSections`); music before the first marker is
  "Start". `inspect` lists them; the built-in demo has Verse, Chorus, Outro.
- In `play`: **V** toggles **vamp** (repeat the current section until pressed again), **O** goes
  to the **outro** (last section named outro/ending/end/coda/tag, else the last) when the current
  section ends. Both act at section boundaries. The status view shows `Section Chorus (2/3) · vamping`.
- How (`LatencyScheduler.ArmJump`): a jump is armed, then committed 50 ms (song time) before the
  song clock reaches it, replacing everything not yet sent with the song from the target on.
  Nothing already sent is taken back, notes held across the jump get a Note Off, and latency
  compensation is unchanged. Disarming before the commit cancels it.

Measured on macOS (`play --count-in 1` → `virtual`, demo song): four woodblock strikes 0.6 s
apart, the first accented, then the song on the next beat. Vamp and outro are tested on the
virtual clock; they need keys in a terminal (foot controller mapping is next, Phase 2b).

## Following a player without a drum kit

The band can follow hits from any MIDI input ([beat following](beat-following.md)): a pad, an
e-kit, or a script that sends notes to a virtual port.

```bash
metrodom beats "pads" --on note:38 --record groove.csv   # see what the tracker hears, save the hits
metrodom simulate song.mid --follow groove.csv --notes    # replay them on the virtual clock
metrodom play song.mid -o "MetroDom xylo" --follow "pads" # count the band in with 4 hits
```

## Not yet (see [tech debt](../exec-plans/tech-debt-tracker.md))

- Clock in follows tempo, not phase: the band can drift against the clock source by up to the
  0.5 BPM threshold, and doesn't start/stop with it (TD-20). The keys are covered only by unit
  tests of `TapTempo` and `SetSpeed`, not by driving a terminal.
- `route` (live input) ignores latency: ADR 0004's "delay to the slowest" isn't built.
- Latency comes from the command line, not profile files or SysEx discovery. Virtual instruments
  do answer MIDI Identity Requests as MetroDom model 0 ([midi-identity](midi-identity.md)), so
  `metrodom devices --identify` finds them and reads their profile ([profile exchange](midi-profile-exchange.md)).
  They accept a Body Write like a brain (`metrodom body write "MetroDom xylo" FILE`) and from then
  on describe themselves as that body, though they still play as before: a written body doesn't
  restart a virtual brain as that body; start it with `--body` instead ([body record](body-record.md)).
- `--drift` applies to `-i` instruments only, not `--body` ones (they take latency from the body).
