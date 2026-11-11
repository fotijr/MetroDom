# MetroDom

Open-source robotics that play **real, physical instruments** live. No click tracks, no backing
tracks. Built on open standards, MIDI first.

One system, three ways to use it:
- **Bar band**: one operator plays a robotic band live from a MIDI keyboard.
- **Crowd jukebox**: patrons request and vote on the next song from their phones.
- **Rental quartet**: a band delivered to your house, controlled all night from an app as easy
  as Spotify.

> Status: a 2016–2017 prototype being dusted off. The target architecture is proposed and mostly
> unbuilt. See [docs/QUALITY_SCORE.md](docs/QUALITY_SCORE.md).

## Where to read next
- [docs/PRODUCT_SENSE.md](docs/PRODUCT_SENSE.md): the why and the three scenarios
- [ARCHITECTURE.md](ARCHITECTURE.md): instruments, Conductor, Control API, cloud
- [docs/design-docs/](docs/design-docs/index.md): design decisions
- [Instruments/](Instruments/README.md): firmware and hardware
- [AGENTS.md](AGENTS.md): map for coding agents (humans welcome)

## Build
Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Linux, macOS or Windows. On Linux,
MIDI devices need ALSA (`libasound2`, preinstalled on desktops and Raspberry Pi OS) and membership
of the `audio` group.

```bash
dotnet test
dotnet run --project src/MetroDom.Conductor -- inspect path/to/song.mid
```

## Repository
| Path | Contents |
| --- | --- |
| `src/MetroDom.Core/` | C# library: MIDI routing, panic, song analysis |
| `src/MetroDom.Conductor/` | `metrodom` command-line Conductor |
| `tests/` | xUnit tests |
| `Instruments/` | Arduino firmware per instrument |
| `docs/` | Knowledge base |

License: MIT ([LICENSE](LICENSE)).
