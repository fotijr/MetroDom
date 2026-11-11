# 0007: Instrument discovery (MetroDom and general MIDI devices)

Status: **Proposed** (2026-11-10). Supersedes the prototype's `md?` serial probe.

## Context
The prototype auto-detected instruments by opening every serial port and sending `md?`
([legacy protocol](../references/legacy-serial-protocol.md)). The idea was right: plug in,
the band finds it. But serial probing is slow (≥1.2 s per port), can disturb unrelated
devices, and only finds MetroDom hardware. The owner also wants **general MIDI devices**
detected (e.g. a player piano, a third-party robotic instrument, controllers).

## Decision
Discovery is layered. Every layer uses an existing standard, and each adds detail:

1. **Enumerate: the OS tells us.** List MIDI ports from the OS MIDI service (CoreMIDI, ALSA,
   Windows MIDI Services/WinMM) and subscribe to hot-plug events. Every class-compliant USB-MIDI
   device appears with no probing. Network devices are found via mDNS/DNS-SD
   (`_apple-midi._udp` for RTP-MIDI, plus Network MIDI 2.0's mDNS advertisement).
2. **Identify: Universal SysEx Identity Request.** On each new output port send
   `F0 7E 7F 06 01 F7` and listen on its paired input for ~250 ms.
   - Reply with manufacturer `7D` + MetroDom family → **MetroDom instrument**. Go to step 3.
   - Reply with another manufacturer → **known general MIDI device**. Record manufacturer,
     family, model, version (map manufacturer IDs to names from the MIDI Association list).
   - No reply (many devices don't implement it) → **anonymous MIDI port**. Still usable.
3. **Describe: fetch the instrument profile.** MetroDom instruments return their
   [profile](../references/instrument-profile.md) via a MetroDom SysEx request (later:
   MIDI-CI Property Exchange, the standard way to do exactly this in MIDI 2.0).
   On MIDI 2.0 endpoints, also use **UMP Endpoint Discovery** and **MIDI-CI Discovery**.
4. **Classify and remember.**
   - MetroDom instruments are auto-added with their profile.
   - General devices are listed as *available*. The owner assigns a role: **output instrument**
     (with a profile chosen from a library or entered manually: note range, latency, etc.) or
     **input controller** (keyboard/pad routed as a control surface).
   - Assignments are remembered by a stable key: Identity Reply + USB serial/product string
     where available, falling back to port name.
   - Input-only ports (keyboards, pads) are offered as control surfaces automatically.

Hints that speed things up but are never required: USB product string (brains report the
instrument name read from their body, see [ADR 0006](0006-swappable-instrument-brains.md)) and
a pid.codes VID/PID for MetroDom brains.

## Consequences
- Plug-in to ready is well under a second per device, with no serial port scanning.
- Any DAW can also see and identify MetroDom instruments with standard tools.
- Identity Requests are harmless to other devices (devices that don't support them ignore them),
  but send them once per new port, not repeatedly.
- The registry needs a small profile library for common third-party devices. Start empty and let
  owners save profiles.

## Open questions
- MetroDom SysEx sub-ID layout for profile get/set (document in `references/` when defined).
- How far to go with MIDI 2.0 now vs when OS and library support is solid on Linux/ARM.
