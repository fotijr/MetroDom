# 0002: Conductor is a headless, cross-platform service

Status: **Proposed** (2026-11-10)

## Context
Today the Conductor is a Windows Forms app on .NET Framework 4.6.1 using Sanford.Multimedia.Midi
(Windows `winmm`). Business logic lives in form event handlers. A band delivered to someone's
house, or installed in a bar, needs a small box that boots straight into "ready", with no
Windows desktop and no monitor.

## Decision
- The Conductor is a **headless long-running service** (daemon) with no UI of its own. UIs are
  clients of the [Control API](0003-control-api.md).
- Target hardware: a small Linux computer that ships inside the band (Raspberry Pi-class). It must
  also run on a dev laptop (macOS/Windows/Linux).
- Structure: **core** (pure logic: arranger, scheduler, queue, policy; fully unit-testable with a
  fake clock and fake instruments) + **adapters** (OS MIDI I/O, HTTP/WebSocket, storage).
- The WinForms app is kept only as a dev tool until the new admin UI replaces it.

## Consequences
- Reuse: the existing C# code can move to modern .NET (8+/LTS) and keep `MetroDom.Core`'s
  ideas. Sanford MIDI must be replaced with a cross-platform MIDI library or OS adapter.
- Scheduling must be tested against a virtual clock, not wall-clock sleeps.

## Open questions
- **Runtime choice** (owner decision): modern .NET (continuity with existing code), or something
  with stronger real-time/embedded MIDI ecosystems (e.g. Rust, Node). Decide before
  [exec plan 0001](../exec-plans/active/0001-foundation-reboot.md) Phase 2.
- Does the box also act as the Wi-Fi access point for the rental scenario?
