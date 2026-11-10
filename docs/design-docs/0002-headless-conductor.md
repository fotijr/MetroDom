# 0002: Conductor is a headless, cross-platform C# service

Status: **Accepted** (2026-11-10). Runtime: modern C# / .NET LTS.

## Context
Today the Conductor is a Windows Forms app on .NET Framework using Sanford.Multimedia.Midi
(Windows `winmm`). Business logic lives in form event handlers. A band delivered to someone's
house, or installed in a bar, needs a small box that boots straight into "ready", with no
Windows desktop and no monitor.

## Decision
- **Language/runtime: modern C# on the current .NET LTS** (net10.0 as of 2026-11), kept
  unless a compelling reason appears (record it as a new ADR that supersedes this one).
  Continuity with the existing code and owner familiarity win over marginal real-time gains.
- The Conductor is a **headless long-running service** (.NET Generic Host / Worker Service,
  run under systemd on Linux) with no UI of its own. UIs are clients of the
  [Control API](0003-control-api.md) (ASP.NET Core minimal APIs + WebSockets).
- Target hardware: a small Linux computer that ships inside the band (Raspberry Pi 4/5-class,
  linux-arm64). It must also run on a dev laptop (macOS/Windows/Linux).
- Structure, as separate projects:
  - **Core**: pure logic (arranger, scheduler, queue, policy). No I/O, no OS types. Uses an
    injected clock (`TimeProvider`) and `IMidiPort` abstractions, so it is fully testable with a
    fake clock and fake instruments.
  - **Adapters**: OS MIDI I/O per platform, HTTP/WebSocket host, storage.
  - **Host**: composition root / service executable.
- Reuse over build: use **DryWetMIDI** (Melanchall, netstandard2.0, MIT) for SMF parsing and MIDI
  types in Core. Device I/O sits behind `IMidiPort` so the platform library can change
  (DryWetMIDI devices on Windows/macOS. On Linux, ALSA via a maintained wrapper such as RtMidi,
  to be verified on a Pi in Phase 2).
- The WinForms app is kept only as a legacy dev tool until the web admin UI replaces it.

## Consequences
- `MetroDom.Core` concepts carry forward. Sanford MIDI is removed from Core (it stays in the
  legacy WinForms app only).
- New projects are SDK-style, `net10.0`, nullable enabled, warnings as errors, and tested with
  `dotnet test` on Linux CI.
- Scheduling must be tested against a virtual clock, not wall-clock sleeps.
- Timing-sensitive paths avoid allocations (GC pauses). Measure on the target box.

## Open questions
- Linux MIDI device library: confirm hot-plug and latency on a Pi before committing.
- Does the box also act as the Wi-Fi access point for the rental scenario?
