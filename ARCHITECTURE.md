# Architecture

Status: **Proposed** target architecture + honest map of today's prototype.
Decisions referenced here live in [docs/design-docs/](docs/design-docs/index.md).

## The one-paragraph version

Robotic **Instruments** are ordinary MIDI devices. A headless **Conductor** service owns the
band: it loads songs (Standard MIDI Files), assigns parts to instruments, compensates for each
instrument's mechanical latency, enforces safety, and sets tempo from a human instead of
a click track. **Control surfaces** (a MIDI keyboard, the owner's phone app, a crowd's
jukebox app) talk to the Conductor through one **Control API**. A **policy profile** decides who
may do what, and that is the only difference between a bar band, a jukebox, and a rental quartet.
An optional **Cloud** adds a song catalog, bookings and remote requests, but the band always
plays offline.

## Layers

```
 ┌───────────────────────── Control surfaces ─────────────────────────┐
 │ MIDI controller │ Owner app (phone) │ Crowd jukebox app │ Admin UI  │
 └────────┬─────────────────┬──────────────────┬─────────────┬────────┘
          │ MIDI in         │   Control API (HTTP + WebSocket, OSC)    │
 ┌────────▼─────────────────▼──────────────────▼─────────────▼────────┐
 │                         Conductor (headless)                        │
 │  Session & policy ─ Queue/Setlist ─ Transport & tempo ─ Arranger    │
 │  Scheduler (latency compensation) ─ Safety governor ─ Registry      │
 └────────┬───────────────────────────────────────────────────────────┘
          │ MIDI out (USB-MIDI, DIN, RTP-MIDI / Network MIDI 2.0)
 ┌────────▼──────────┐  ┌───────────────┐  ┌───────────────┐
 │ Instrument: xylo  │  │ Instrument: … │  │ Instrument: … │   each = MCU + actuators
 │ firmware + driver │  │               │  │               │   + watchdog, self-describing
 └───────────────────┘  └───────────────┘  └───────────────┘

 Optional, never required to play:  Cloud (catalog, bookings, remote request relay)
```

### Instruments: dumb, safe, standard
- A microcontroller presents itself as a **class-compliant MIDI device** (no drivers) and maps
  notes to actuators (solenoids, servos, motors).
- Split into a swappable **brain** (Pico-footprint MCU, one generic firmware) and an
  instrument-specific **body** that describes itself ([ADR 0006](docs/design-docs/0006-swappable-instrument-brains.md)).
- Identifies itself with a **SysEx Identity Reply**, and later MIDI-CI. It publishes an
  **instrument profile** (note range, polyphony, latency per note, velocity curve, duty limits).
  See [references/instrument-profile.md](docs/references/instrument-profile.md).
- Owns its **own** physical safety: watchdog, max on-time per actuator, all-off on disconnect.
  It must be safe even if the Conductor crashes mid-note.
- Knows nothing about songs, tempo, other instruments, or users.

### Conductor: the only thing that knows there is a band
Headless **modern C# / .NET** service on a small computer that ships with the band (e.g. a Raspberry Pi-class box).
Components:

| Component          | Responsibility                                                               |
| ------------------ | ---------------------------------------------------------------------------- |
| Registry           | Discover MetroDom and general MIDI devices ([ADR 0007](docs/design-docs/0007-instrument-discovery.md)), read profiles, track health |
| Arranger           | Map song tracks/channels to instruments, transpose, drop unplayable notes    |
| Transport & tempo  | Play/pause/seek. Tempo comes from a human: tap, MIDI clock in, live player   |
| Scheduler          | Send each note early by that instrument's latency so the sound lands on time |
| Safety governor    | Panic/all-notes-off, rate and duty limits, volume/curfew caps                |
| Lights (planned)   | Light profiles, reactive mode, sACN/Art-Net output ([ADR 0011](docs/design-docs/0011-visuals-are-part-of-the-band.md)) |
| Queue / setlist    | Ordered upcoming songs. Requests and votes feed in, subject to policy        |
| Session & policy   | Roles (owner, operator, guest), deployment-mode profile, rate limits         |
| Control API        | The single front door for every non-MIDI client                              |

See [ADR 0002](docs/design-docs/0002-headless-conductor.md),
[ADR 0003](docs/design-docs/0003-control-api.md),
[ADR 0004](docs/design-docs/0004-latency-compensation.md).

### Control surfaces: thin
Apps never talk to instruments directly. They render Conductor state and send intents
("queue song X", "skip", "tempo +4", "panic"). A MIDI controller is a control surface too: live
notes are routed through the Conductor so safety and latency rules still apply.

### Cloud: optional
Song catalog and licensing, booking/rental logistics, relaying requests from patrons off the
venue Wi-Fi. The Conductor syncs with it when it can and never needs it to play.
See [ADR 0005](docs/design-docs/0005-deployment-modes-are-policy.md).

## Dependency rules (enforce in code review, later mechanically)
1. Control surfaces → Control API only. Never → instruments.
2. Conductor core logic has no UI, OS-specific MIDI, or network types. Those are adapters.
3. Instrument-specific details (servo angles, pin numbers) live in firmware or the instrument
   profile, never in Conductor core.
4. Everything crossing a process boundary uses an open, documented format: MIDI, SMF, JSON,
   OSC. See [core beliefs](docs/design-docs/core-beliefs.md).

## Current code map

Stack: **C# on .NET 10 (LTS)**, SDK-style projects, `MetroDom.slnx`. Builds and tests on Linux,
macOS and Windows. MIDI files and types via DryWetMIDI. Devices via ALSA on Linux and DryWetMIDI
on macOS/Windows. The 2016–2017 WinForms app was retired in favor of a
CLI (recover it from git history at commit `266b6b5` if needed).

| Path | What it is today |
| --- | --- |
| `src/MetroDom.Core/IMidiOutput.cs` | The instrument contract: a named thing you can send MIDI events to |
| `src/MetroDom.Core/Discovery/` | `DeviceIdentity`: MIDI Identity Request/Reply bytes, MetroDom's identity (`7D` + family "MD"). `ProfileExchange`: MetroDom SysEx profile request and chunked reply. See [midi-identity](docs/references/midi-identity.md), [midi-profile-exchange](docs/references/midi-profile-exchange.md) |
| `src/MetroDom.Core/Safety/` | `SafetyGovernor`: wraps one output; holds back notes the instrument can't play, repeats under its min interval, notes over the rate limit or in silent quiet hours; caps velocity. Note Offs and panic always pass |
| `src/MetroDom.Core/Policy/` | `PolicyProfile` (ADR 0005, first cut: controls only) and `ControlMap`: MIDI → start/stop, next, tap, vamp, outro, panic. Shipped profile in `policies/` ([policy profile](docs/references/policy-profile.md)) |
| `src/MetroDom.Core/Orchestra.cs` | Channel → output routing, mute (Note Offs always pass), panic (CC 120/123). A throwing output is reported and skipped |
| `src/MetroDom.Core/Timing/` | `IClock` (system + virtual), `SongTimeline` (SMF → timed events), `LatencyScheduler` (sends early per instrument latency, re-times on tempo change, ADR 0004), `TapTempo`, `TapTrigger` (which MIDI messages are taps), `ClockTempo` (MIDI Clock in → tempo), `SongSections` + `CountIn` (markers, count-in strikes), `SectionNavigator` (vamp, jump to outro via scheduler jumps), `BeatTracker` + `BeatFollower` + `SongBeats` + `HitLog` (human leads: predict a player's beats, steer the band onto them) |
| `src/MetroDom.Core/Simulation/` | `VirtualInstrument` (firmware stand-in with latency, jitter, note set, min interval), `BandSimulation`, `DemoSong`. See [virtual band](docs/references/virtual-band.md) |
| `src/MetroDom.Core/Arranging/` | `Arranger`: song parts (track × channel) → instruments by fit, octave transpose, fold, drop, thin by min interval. `BandMember` = profile + channel. `Roles.cs`: part roles from saved mapping, channel 10, track name or GM program ([song roles](docs/references/song-roles.md)). `Seating`: a human takes a role, its parts lose their notes |
| `src/MetroDom.Core/InstrumentProfile.cs` | The part of the [instrument profile](docs/references/instrument-profile.md) scheduling uses: latency (per note), notes, min interval. `InstrumentProfileJson.cs` reads/writes the JSON |
| `src/MetroDom.Core/SongAnalyzer.cs` | SMF → tracks, channels, note ranges, tempo, duration (arranger input) |
| `src/MetroDom.Conductor/` | `metrodom` CLI: `inspect`, `devices`, `play`, `route`, `panic`, plus `arrange`, `simulate`, `virtual`, `demo-song` for the [virtual band](docs/references/virtual-band.md). Commands on Spectre.Console.Cli (typed settings, validation, `--help` per command). `Api/` is the Control API v0 (`play --api`, `openapi.json` contract). `Cli/` holds app setup, exit codes (2 usage, 3 platform, 4 device), the port picker (prompts in a terminal, errors when piped) and the live play/route status panel |
| `src/MetroDom.Conductor/Midi/` | OS MIDI device adapters behind `IMidiBackend`: `Alsa/` (Linux sequencer via P/Invoke) and DryWetMIDI (Windows/macOS). `ResilientPorts.cs` keeps ports alive across unplug/replug. Backends also create virtual ports (ALSA, CoreMIDI). `SchedulerLoop.cs` drives the scheduler in real time for `play`. `Identifier.cs` asks every output who it is and MetroDom instruments for their profile (`devices --identify`) |
| `tests/MetroDom.Core.Tests/` | xUnit v3 tests for Core (Microsoft.Testing.Platform runner) |
| `tests/MetroDom.Conductor.Tests/` | CLI exit codes and status panel rendering, port matching, and a real ALSA round trip (runs where `/dev/snd/seq` exists; required in CI when the runner can load `snd-seq-dummy`, TD-18) |
| `Instruments/xylophone_solenoid/` | 3 solenoids on pins 9–11, ASCII `'0' '4' '7'` triggers, 45 ms strike |
| `Instruments/xylophone_servo/` | Spin + strike servo. `<digits>m` moves, `p` strikes, `?` identifies |

Repo-wide build settings: `global.json` (SDK + test runner), `Directory.Build.props`
(net10.0, nullable, warnings as errors, analyzers), `Directory.Packages.props` (central package
versions), `.editorconfig`. The firmware still speaks the
[legacy serial protocol](docs/references/legacy-serial-protocol.md). No host code drives it now.

## Migration path
Prototype → target is planned in
[exec-plans/active/0001-foundation-reboot.md](docs/exec-plans/active/0001-foundation-reboot.md).
