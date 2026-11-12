# 0002: Conductor is a headless, cross-platform C# service

Status: **Accepted** (2026-11-10). Runtime: modern C# / .NET LTS.

## Context
The 2017 Conductor was a Windows Forms app on .NET Framework using Sanford.Multimedia.Midi
(Windows `winmm`), with business logic in form event handlers. It was retired on 2026-11-10 for
a cross-platform .NET 10 CLI. A band delivered to someone's
house, or installed in a bar, needs a small box that boots straight into "ready", with no
Windows desktop and no monitor.

## Decision
- **Language/runtime: modern C# on the current .NET LTS** (net10.0 as of 2026-11. .NET 11 is
  STS, so move when the next LTS ships unless a feature is needed sooner), kept
  unless a compelling reason appears (record it as a new ADR that supersedes this one).
  Continuity with the existing code and owner familiarity win over marginal real-time gains.
- The Conductor is a **headless long-running service** (.NET Generic Host / Worker Service,
  run under systemd on Linux) with no UI of its own. UIs are clients of the
  [Control API](0003-control-api.md) (ASP.NET Core minimal APIs + WebSockets).
- Target hardware: a small Linux computer that ships inside the band (Raspberry Pi 4/5-class,
  linux-arm64). It must also run on a dev laptop (macOS/Windows/Linux).
- Structure, as separate projects:
  - **Core**: pure logic (arranger, scheduler, queue, policy). No I/O, no OS types. Uses an
    injected clock (`TimeProvider`) and `IMidiOutput` abstractions, so it is fully testable with a
    fake clock and fake instruments.
  - **Adapters**: OS MIDI I/O per platform, HTTP/WebSocket host, storage.
  - **Host**: composition root / service executable.
- Reuse over build: use **DryWetMIDI** (Melanchall, netstandard2.0, MIT) for SMF parsing and MIDI
  types in Core. Device I/O sits behind `IMidiOutput` so the platform library can change
  (DryWetMIDI devices on Windows/macOS. On Linux, the ALSA sequencer called directly from C#
  through `libasound.so.2`, which ships with every desktop distro and Raspberry Pi OS, so there is
  no extra native library to build or bundle).
- Until the service and web UI exist, the Conductor ships as the `metrodom` CLI. Nothing may
  require a specific OS to build or test.

## Consequences
- Sanford MIDI and WinForms are gone. Core depends only on DryWetMIDI's OS-independent types.
- Projects are SDK-style, `net10.0`, nullable enabled, warnings as errors, and tested with
  `dotnet test` on Linux CI.
- Scheduling must be tested against a virtual clock, not wall-clock sleeps.
- Timing-sensitive paths avoid allocations (GC pauses). Measure on the target box.

## Open questions
- ALSA adapter: measure latency and add hot-plug (sequencer announce events) on a Pi.
- ~~Does the box also act as the Wi-Fi access point for the rental scenario?~~ Yes, in every
  mode (owner, 2026-11-11): the box runs its own access point by default, so it works anywhere
  with no venue IT and predictable latency. It may also join a venue network as a client, only
  for internet (cloud sync, updates).
