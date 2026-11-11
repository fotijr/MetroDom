# MetroDom

**A band of robots playing real instruments, live, in the room.**

Live music leans more and more on click tracks and backing tracks. MetroDom goes the other way:
every sound comes from a real instrument being struck, plucked or pressed by a robot, and the
timing follows a person, not a grid. You set the tempo, tap it with your foot, or sit down at
the drums and let the robots play the rest of the band around you.

It's open source and built on open standards, MIDI above all, so any instrument, keyboard, DAW or
app that speaks MIDI can join in.

> **Where it stands:** a 2016–2017 prototype being rebuilt. The software runs today against
> *virtual* instruments; the first physical robot (a solenoid on a xylophone) is next.
> [docs/QUALITY_SCORE.md](docs/QUALITY_SCORE.md) has the honest component-by-component status.

## What it can do today

All of this runs on Linux, macOS or Windows, with no hardware needed.

- **Play a song across a band.** Give it a Standard MIDI File and some instruments; it sends each
  part to the right instrument.
- **Keep slow and fast instruments in time.** A solenoid is quicker than a mallet arm, so each
  instrument gets its notes early by its own latency and everything is *heard* together.
- **Arrange songs for robots.** Robots have few notes. The arranger gives each instrument the part
  it covers best, moving octaves, folding or dropping what doesn't fit, and thinning notes that
  repeat faster than the instrument can.
- **Let a human set the tempo.** Start at any tempo, tap it on the keyboard or a MIDI foot pedal,
  nudge it up or down, or follow MIDI Clock from a drum machine.
- **Follow a drummer.** Play hits into any MIDI input (pads, an e-kit) and the band locks onto
  your tempo and beat, starts when you count it in, and holds steady through fills and breaks.
- **Play along.** Take a seat (`--seat guitar=Dom`) and the band leaves that part out. Parts are
  tagged drums, bass, melody and so on from track names and General MIDI, and you can correct and
  save them per song.
- **Count in and shape the song live.** A count-in on a real instrument (a woodblock, not a
  click), then **vamp** a section until you're ready, or **jump to the outro**.
- **Run a set hands-free.** Play a setlist from a MIDI foot controller: start/stop, next song, tap
  tempo, vamp, outro and panic, mapped in a policy file you can edit.
- **Be driven by an app.** A small HTTP API (`--api`) to start, stop, skip, set the tempo, vamp
  and panic from a phone or script, with a token and an OpenAPI description.
- **Find instruments by itself.** It asks each MIDI port who it is and fetches a MetroDom
  instrument's profile (its notes, latency and limits) without any setup.
- **Stay safe.** Every instrument sits behind a safety governor: velocity caps, note-rate limits,
  quiet hours, and a panic that always gets through. Unplugging an instrument goes silent;
  plugging it back in reconnects.
- **Rehearse without hardware.** Virtual instruments behave like the real firmware, either on a
  simulated clock (instant, repeatable) or as OS MIDI ports a DAW can play.

## What's planned

From the [roadmap](docs/ROADMAP.md), roughly in order. Each step is small, cheap and makes music.

- **One note, then a xylophone.** The first solenoid played over USB-MIDI by any keyboard or
  DAW, then a three-note xylophone on a swappable "brain" that reads its own profile.
- **A two-piece band.** Xylophone plus snare and woodblock from the owner's drum kit, playing a
  song in time, with a human playing guitar over it.
- **A home-built foot controller.** Stomp switches on the same brain as the instruments.
- **Lights.** LED strips and accents that flash with the notes, driven from the same timeline.
- **A home band.** A full xylophone and drum pieces, setlists, a phone panic button, and regular
  play-along sessions.
- **Follow the drummer on a real kit.** Piezo triggers on an acoustic kit feeding the beat
  follower, tuned on the owner's own recorded drumming.
- **New instruments, one at a time.** Electric guitar, a piano octave, acoustic guitar, and
  research into trumpet, kick drum and stage lighting.
- **A party jukebox.** A matrix display and a QR code so guests can queue songs.
- **One day, maybe, gigs.** Road cases and a fast setup, if MetroDom ever leaves the house.

## Try it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). On Linux, MIDI devices need ALSA
(`libasound2`, preinstalled on desktops and Raspberry Pi OS) and membership of the `audio` group.

```bash
dotnet test
dotnet run --project src/MetroDom.Conductor -- simulate            # a demo song on a virtual band
dotnet run --project src/MetroDom.Conductor -- inspect path/to/song.mid
```

To hear it through real MIDI ports, start virtual instruments in one terminal and play to them
from another (`metrodom virtual`, then `metrodom play`). See
[docs/references/virtual-band.md](docs/references/virtual-band.md).

## Learn more

- [docs/PRODUCT_SENSE.md](docs/PRODUCT_SENSE.md): why MetroDom exists and who it's for
- [docs/ROADMAP.md](docs/ROADMAP.md): what gets built next, and why
- [ARCHITECTURE.md](ARCHITECTURE.md): instruments, Conductor, Control API
- [docs/design-docs/](docs/design-docs/index.md): design decisions
- [Instruments/](Instruments/README.md) and [hardware/](hardware/README.md): firmware, parts and builds
- [AGENTS.md](AGENTS.md): map for coding agents (humans welcome)

## Repository

| Path | Contents |
| --- | --- |
| `src/MetroDom.Core/` | C# library: scheduling, arranging, safety, discovery. Pure logic, no devices |
| `src/MetroDom.Conductor/` | The `metrodom` command-line Conductor and OS MIDI adapters |
| `tests/` | xUnit tests |
| `Instruments/` | Firmware per instrument |
| `hardware/` | Parts, inventory, shared modules, build records |
| `docs/` | Knowledge base |

License: MIT ([LICENSE](LICENSE)).
