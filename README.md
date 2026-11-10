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

## Repository
| Path | Contents |
| --- | --- |
| `MetroDom.Core/` | C# library: instrument interface and song model (prototype) |
| `MetroDom.Conductor/` | Windows Forms app: MIDI input, MIDI file explorer, serial discovery (prototype) |
| `MetroDom.Tests/` | xUnit tests |
| `Instruments/` | Arduino firmware per instrument |
| `docs/` | Knowledge base |

License: MIT ([LICENSE](LICENSE)).
