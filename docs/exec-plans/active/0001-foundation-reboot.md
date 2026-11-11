# 0001: Foundation reboot

Status: Active · Last updated: 2026-11-11

## Goal
Go from the 2017 prototype to the smallest version of the target
[architecture](../../../ARCHITECTURE.md) that can play a real song on two physical instruments
from a Standard MIDI File, with human-controlled tempo and working panic, on a headless box.
That is the first milestone of the [live band spec](../../product-specs/live-band.md).

## Context
- Sequenced by the [roadmap](../../ROADMAP.md): build in milestone order, not phase order
- Decisions: [ADR 0001–0005](../../design-docs/index.md) (all Proposed. Owner to accept)
- Current state: [QUALITY_SCORE.md](../../QUALITY_SCORE.md), [tech debt](../tech-debt-tracker.md)

## Phases

### Phase 0: Knowledge base & hygiene
- [x] AGENTS.md table of contents + `docs/` knowledge base
- [x] Docs lint (`scripts/check_docs.py`) in GitHub Actions
- [ ] Owner reviews and accepts/edits ADRs 0001–0005
- [x] Replace Travis with GitHub Actions
- [x] Migrate to .NET 10 SDK-style projects, cross-platform. Retire WinForms for the `metrodom` CLI
- [ ] Confirm `build` workflow runs green on GitHub

### Phase 1: Instruments speak MIDI
- [ ] Accept brain/body split ([ADR 0006](../../design-docs/0006-swappable-instrument-brains.md)). Define body connector + EEPROM layout
- [ ] Generic brain firmware (Pico, Arduino C++): USB-MIDI, Note On → fixed strike pulse, watchdog, all-off
- [ ] Port solenoid xylophone to a body (PCA9685/MOSFET + ID EEPROM). Hardware side: [exec plan 0002](0002-hardware-bootstrap.md)
- [ ] SysEx Identity Reply + [instrument profile](../../references/instrument-profile.md) read from body EEPROM
- [ ] Bench-test with a stock DAW/keyboard (no MetroDom software)
- [ ] Measure and record per-note latency

### Phase 2: Headless Conductor core
- [x] SDK-style `net10.0` Core + CLI + tests, `dotnet test` on Linux/macOS/Windows CI
- [x] Core: SMF analysis (DryWetMIDI), channel routing with safe mute, panic
- [x] Linux MIDI device adapter (ALSA sequencer), round trip tested (locally; hosted CI can't load the module, TD-18)
- [x] Core: arranger (song part → instrument by fit, transpose, fold, drop, thin). `metrodom arrange`, `--arrange` on `simulate`/`play`. Limits: TD-21
- [x] Core: latency-compensating scheduler with fake-clock tests (`LatencyScheduler`, `VirtualClock`)
- [x] Virtual band: `VirtualInstrument`, `metrodom simulate` (virtual clock), `metrodom virtual` (OS ports), `demo-song`. See [virtual band](../../references/virtual-band.md)
- [x] `play` drives real ports through the scheduler, several outputs with per-output channels and latency, re-timing on tempo change
- [x] Adapters: unplug goes silent, replug reconnects with a panic first. One failing output can't block others
- [ ] Measure ALSA latency on a Pi
- [x] Discovery steps 1-2 per [ADR 0007](../../design-docs/0007-instrument-discovery.md): list ports, Identity Request, MetroDom vs general MIDI (`devices --identify`, [midi-identity](../../references/midi-identity.md))
- [x] Discovery step 3: profile fetch over MetroDom SysEx, shown by `devices --identify` ([profile exchange](../../references/midi-profile-exchange.md))
- [x] Discovery step 4a: `play`/`route` use the profile an instrument sends for whatever `-o` leaves out (`--no-identify` to skip)
- [ ] Discovery step 4b: identify on hot-plug, remember assignments, auto-add instruments (TD-23)
- [x] Safety governor + panic, tested (`SafetyGovernor` in front of every output; `--max-velocity`, `--max-rate`, `--quiet`). Limits: TD-22
- [x] Tap tempo (terminal keys) and `--tempo`
- [x] Tap from a MIDI input (foot pedal) / MIDI Clock in (`play --tap PORT [--tap-on TRIGGER]`, `--clock-in PORT`)

### Phase 2b: Play-along ([ADR 0012](../../design-docs/0012-humans-in-the-band.md))
- [x] Track roles (track names, GM drums channel, saved per-song mapping): `metrodom roles`, [song roles](../../references/song-roles.md)
- [ ] Seats: a human takes a role, arranger mutes it (generalize `--mute`)
- [ ] Count-in on a chosen instrument. SMF marker sections. Vamp / jump to outro
- [ ] Foot controller mapping in the live-band policy (start/stop, next, tap, vamp, panic)
- [ ] Tempo tracker with beat prediction, tested with recorded human timings + fake clock
- [ ] Human-leads input from drum triggers (`trigger-piezo`)

### Phase 3: Control API v0
- [ ] OpenAPI spec: transport, queue, instruments, panic
- [ ] WebSocket state stream
- [ ] Minimal web UI served by the Conductor

## Decisions log
- 2026-11-10: Adopted harness-engineering style repo knowledge base. Architecture written as
  Proposed ADRs pending owner review.
- 2026-11-10: Conductor stays modern C# / .NET LTS (ADR 0002 accepted). Hardware priorities:
  ease of programming, cost, interchangeable brains (ADR 0006). Discovery keeps the `md?`
  auto-detect intent using standard SysEx Identity + general MIDI support (ADR 0007).
- 2026-11-10: Migrated to .NET 10 (latest stable, LTS. .NET 11 still RC). Must build and test
  on any OS. WinForms app and serial discovery deleted (git history keeps them). The Explorer
  and live-control ideas live on as `metrodom inspect/play/route`.

- 2026-11-10: CLI moved from a hand-written parser to Spectre.Console(.Cli): per-command help
  and validation, typed options with short forms (`-o`, `-m`, `-i`), a port picker when
  `--output`/`--input` is omitted in a terminal, and a live status panel for `play`/`route`
  (progress, 16-channel activity, connection state, warnings). Piped/CI runs stay plain text.
  A full-screen TUI (e.g. Terminal.Gui) is deferred: per ADR 0002 it should be a Control API
  client, not a second in-process controller.

- 2026-11-10: Linux MIDI talks to the ALSA sequencer directly (P/Invoke to libasound) rather
  than RtMidi: no native library to build per architecture, and the sequencer sees USB devices,
  interfaces, soft synths and virtual ports alike.

- 2026-11-10: Virtual band for roadmap S1. Two layers: an in-process simulation on a virtual
  clock (deterministic, CI-friendly, proves the scheduler math) and OS virtual MIDI ports (proves
  the device path with stock tools). Virtual instruments mimic the generic firmware: strike on
  Note On, Note Off ignored, drop unplayable notes, refuse too-fast repeats. Result: compensation
  takes the default band's spread from 25 ms to 0 ms, and under 4 ms with ±2 ms jitter. The
  scheduler polls (`Pump` at `NextDue`) instead of owning a thread, so the same code runs on a
  fake clock in tests and on a real clock later.
- 2026-11-10: `play` moved from DryWetMIDI `Playback` to `LatencyScheduler` + `SchedulerLoop`.
  Events stay in song time; a speed factor (× the file's tempo) re-anchors at the current
  position, so tap tempo works without re-reading the file and respects tempo maps. Outputs
  became `-o PORT[=CHANNELS][@LATENCY_MS]`, repeatable. The first real-time run had beat one
  13 ms late (cold send path); a panic before starting (start from silence) plus a 250 ms
  pre-roll fixed it. Measured ≤ 1 ms between a 10 ms and a 35 ms virtual instrument.
- 2026-11-11: Arranger first cut. A part is one track on one channel. Each instrument gets at
  most one part, chosen greedily by how many of its notes it can play after the best octave
  shift; channel 10 (GM percussion) pairs only with percussion instruments and is never
  transposed. Out-of-range notes fold to the same pitch class, else drop; repeats faster than
  `MinInterval` are thinned. The arranged timeline puts each part on its instrument's first
  channel, so routing, mutes and the scheduler are unchanged. Instrument notes are given as
  `/NOTES` on the CLI until profile files and discovery exist. Pinning parts waits for roles.
- 2026-11-11: Safety governor is an `IMidiOutput` decorator per instrument, so the orchestra,
  scheduler and live routing all go through it without knowing it exists. It only ever holds
  back or turns down new notes (Note On, velocity > 0); everything else passes, so panic can't be
  blocked. A velocity cap sends a copy because the scheduler shares event objects between
  instruments. Policy limits are flags until policy profiles (ADR 0005) exist. Measured: demo song
  with `--max-rate 3 --max-velocity 50` into `virtual` never exceeded 3 strikes in any second
  and nothing louder than 50; silent quiet hours sent no notes but both panics.
- 2026-11-11: Discovery step 2. MetroDom identifies as manufacturer `7D` (non-commercial) with
  family bytes ASCII "MD"; model 0 = software instrument. Draft, needs owner review. Every
  output is asked at once and paired with the same-named input, so a scan costs one 250 ms wait,
  not one per port. Only replies count, because loopback ports echo the request. It runs only
  on `devices --identify` (ADR 0007: ask once per port, not repeatedly). Virtual ports became
  duplex so `virtual` can answer. Measured on macOS: two virtual instruments identified, the
  whole command 0.4 s.

- 2026-11-11: Discovery step 3. Profile Request `F0 7D 4D 44 01 F7`; the reply is the profile
  JSON in numbered chunks of ≤ 128 ASCII bytes (`02 <index> <count> <text>`). Draft, needs owner
  review. JSON over SysEx rather than a binary layout: the same file an instrument folder holds,
  readable in a SysEx dump, and firmware can store it verbatim in the body EEPROM. MetroDom SysEx
  goes only to ports that identified as MetroDom, since `7D` is shared. The profile round runs
  after the identity round, all ports at once, so a scan stays ≤ 2 × 250 ms. One `MinInterval`
  per instrument for now: the largest per-note value. Measured on macOS: two virtual instruments
  identified and described in 0.19 s.

- 2026-11-11: Discovery step 4a. `-o` latency became optional so "not given" differs from `@0`.
  Outputs missing `@LATENCY` or `/NOTES` are identified once at start (only those ports); the
  sent profile fills the gaps and the command line wins field by field. `@LATENCY` replaces the
  per-note latencies too, since a person who types one number means it for the whole instrument.
  This is also the first way to give the governor a minimum interval. Cost: up to 2 × 250 ms at
  start when a named port has an input that doesn't answer; `--no-identify` or a fully described
  `-o` avoids it.

- 2026-11-11: MIDI tap and clock in. Picked over discovery step 4b, whose remembered
  assignments need roles from the registry/Control API. Taps share the keyboard's `TapTempo`, so
  pedal and Space mix. `--tap-on any` (default) takes Note On or a controller's press (crossing
  64), never its release or channel mode messages, so a sustain pedal works out of the box.
  Clock in averages one beat of ticks and ignores changes under 0.5 BPM: ±1 ms USB jitter swings
  a one-beat average by ~0.3 BPM, which shouldn't re-time the band. It follows tempo only; phase
  lock belongs with the M5 tempo tracker. Stop or silence holds tempo (ADR 0012: losing the beat
  holds, never lurches). Measured: 0.600 / 0.400 / 0.800 s strike spacing at file tempo, clock
  150 and pedal 75 BPM (file 100 BPM).

- 2026-11-11: Track roles. Order: saved mapping, channel 10, track name, GM program, else
  `other`. Names beat programs because people type names and many files leave every track on
  program 1. Detection produces a fixed list of ten roles; a saved mapping may use any one word
  (draft answer to ADR 0012's vocabulary question). The mapping is `song.roles.json` beside the
  song, keyed by name, `#TRACK` or `#TRACK:CHANNEL`, so it survives the file being re-exported
  with the same track layout and stays readable in a diff. Roles live on `SongPart` so seats can
  mute by role without another pass over the file.

## Open questions
- Target box model (ADR 0002).
