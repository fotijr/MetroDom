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
- Identifies itself with a **SysEx Identity Reply**, and later MIDI-CI. It publishes an
  **instrument profile** (note range, polyphony, latency per note, velocity curve, duty limits).
  See [references/instrument-profile.md](docs/references/instrument-profile.md).
- Owns its **own** physical safety: watchdog, max on-time per actuator, all-off on disconnect.
  It must be safe even if the Conductor crashes mid-note.
- Knows nothing about songs, tempo, other instruments, or users.

### Conductor: the only thing that knows there is a band
Headless service on a small computer that ships with the band (e.g. a Raspberry Pi-class box).
Components:

| Component          | Responsibility                                                               |
| ------------------ | ---------------------------------------------------------------------------- |
| Registry           | Discover instruments, read profiles, track health/connection                 |
| Arranger           | Map song tracks/channels to instruments, transpose, drop unplayable notes    |
| Transport & tempo  | Play/pause/seek. Tempo comes from a human: tap, MIDI clock in, live player   |
| Scheduler          | Send each note early by that instrument's latency so the sound lands on time |
| Safety governor    | Panic/all-notes-off, rate and duty limits, volume/curfew caps                |
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
   profile, never in Conductor core. *(Today `MetroDom.Core/Song.cs` violates this; see tech debt.)*
4. Everything crossing a process boundary uses an open, documented format: MIDI, SMF, JSON,
   OSC. See [core beliefs](docs/design-docs/core-beliefs.md).

## Current code map (prototype, 2016–2017)

| Path                                   | What it is today                                                        |
| -------------------------------------- | ----------------------------------------------------------------------- |
| `MetroDom.Core/Interfaces/IInstrument.cs` | Instrument interface (`PlayNote(IMidiMessage)`, `PlaySong`, `Stop`…) |
| `MetroDom.Core/Orchestra.cs`           | Holds instruments. `Start(song)` is a TODO                               |
| `MetroDom.Core/Song.cs`                | Song = key + note list. `Note` hard-codes **servo angles** (temporary hack) |
| `MetroDom.Conductor/Forms/LiveControlForm.cs` | Picks a MIDI input, discovers serial instruments, note→serial mapping commented out |
| `MetroDom.Conductor/Forms/Explorer.cs` | Loads a `.mid`, lists tracks/channels, plays to a MIDI out with channel mute |
| `MetroDom.Conductor/SerialService.cs`  | Probes COM ports with `md?`, parses `Name:`/`Id:` lines (9600 baud ASCII) |
| `Instruments/xylophone_solenoid/`      | 3 solenoids on pins 9–11, ASCII `'0' '4' '7'` triggers, 45 ms strike     |
| `Instruments/xylophone_servo/`         | Spin + strike servo; `<digits>m` moves, `p` strikes, `?` identifies      |
| `MetroDom.Tests/`                      | Two xUnit tests for `Orchestra.Start` argument handling                  |

Stack: .NET Framework 4.6.1, WinForms, Sanford.Multimedia.Midi (Windows `winmm`), so the
Conductor runs on Windows only. The legacy serial protocol is documented in
[references/legacy-serial-protocol.md](docs/references/legacy-serial-protocol.md).

## Migration path
Prototype → target is planned in
[exec-plans/active/0001-foundation-reboot.md](docs/exec-plans/active/0001-foundation-reboot.md).
