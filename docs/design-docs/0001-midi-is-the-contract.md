# 0001: MIDI is the contract at the instrument boundary

Status: **Proposed** (2026-11-10)

## Context
The prototype uses two incompatible ad-hoc ASCII serial protocols at 9600 baud
([legacy protocol](../references/legacy-serial-protocol.md)). Each new instrument would need
custom host code, and nothing outside MetroDom (DAWs, keyboards, phone MIDI apps) can drive them.

## Decision
- Every instrument is a **standard MIDI device**. Preferred transport: **USB-MIDI class
  compliant** (no drivers on any OS). Also allowed: 5-pin DIN / TRS MIDI, and over the
  network RTP-MIDI (RFC 6295) or Network MIDI 2.0 (UDP).
- Playing = **Note On/Off** (velocity = strike strength). Control = standard **CC** messages
  (e.g. CC 120 All Sound Off, CC 123 All Notes Off for panic).
- Identity = **Universal SysEx Identity Request/Reply**, using the non-commercial manufacturer ID
  `0x7D` until/unless a real ID is obtained. Rich capabilities = the
  [instrument profile](../references/instrument-profile.md), later MIDI-CI Property Exchange.
- Anything MetroDom-specific (calibration, duty limits) goes in **SysEx** under `0x7D` and is
  documented in `docs/references/`.

## Consequences
- Instruments can be tested with any DAW or keyboard, with no MetroDom software needed.
- Boards need native USB (ATmega32U4 Leonardo/Micro, RP2040, Teensy, SAMD, ESP32-S3).
  The Uno's serial-only USB is not enough without hacks.
- The Conductor uses an off-the-shelf MIDI library per OS instead of serial parsing.
- The legacy serial firmware gets ported, not extended.

## Open questions
- Board standard: proposed in [ADR 0006](0006-swappable-instrument-brains.md) (Pico-footprint
  RP2040/RP2350 brains).
- MIDI 2.0 UMP from day one, or MIDI 1.0 now with 2.0 later? Leaning 1.0 now for tooling reach.
