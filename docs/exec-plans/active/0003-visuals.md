# 0003: Visuals: lights, LED strips and matrix displays

Status: Active · Last updated: 2026-11-10

## Goal
A home session (and later, maybe, a gig) where every song gets a light show synced to the robots, with no extra work per
song: accent LEDs show which instrument plays each note, stage lights follow the music, and a
matrix display shows what's playing. An authored lights track or live pads can take over at
any time. Measured by: light changes land within the alignment budget in [RELIABILITY.md](../../RELIABILITY.md).

## Context
- Sequenced by the [roadmap](../../ROADMAP.md): build in milestone order, not phase order
- Decision: [ADR 0011](../../design-docs/0011-visuals-are-part-of-the-band.md)
- Standards primer (start here if lighting is new): [lighting essentials](../../references/lighting-essentials.md)
- Hardware records and modules: [hardware/README.md](../../../hardware/README.md)
- Depends on: exec plan [0001](0001-foundation-reboot.md) (Conductor scheduler, Control API) and
  [0002](0002-hardware-bootstrap.md) (brain firmware, power, cases)

Principle: **learn with off-the-shelf tools, then absorb what works into MetroDom.** Each
phase is fun on its own and doesn't wait for the next one.

## Phases

### Phase V0: Zero-code playground (learn the standards, ~$30–60)
- [ ] ESP32 + WLED + 1 m WS2812B strip on a fused 5 V supply. Try effects from the web UI
- [ ] QLC+ on the laptop: send it MIDI from a controller (or `metrodom route`), map pads to scenes
- [ ] QLC+ → sACN → WLED: a pad press changes the strip. You've now run a real lighting network
- [ ] Write down what was confusing in [lighting essentials](../../references/lighting-essentials.md)

### Phase V1: Accent LEDs on instruments (`led-accent`)
- [ ] Brain firmware drives a short strip on the body: each note's LED flashes on Note On
- [ ] Instrument profile declares its accent LEDs (count, note → LED index, colour)
- [ ] Printed diffuser (PLA is fine at home). Brightness capped in firmware
- [ ] Fits the xylophone body from exec plan 0002 without a second power supply design

### Phase V2: Conductor drives lights
- [ ] Light profile format, written up next to the [instrument profile](../../references/instrument-profile.md) (shape sketched in ADR 0011 §2)
- [ ] sACN output adapter in the Conductor (UDP, unit-tested with a fake socket)
- [ ] Reactive mode: colour per instrument, pulse on notes, intensity from velocity
- [ ] Channel 16 lights track and pad scenes override reactive mode
- [ ] Scheduler aligns light changes with measured instrument latency
- [ ] Strobe cap (≤ 3 flashes/s), brightness cap, quiet hours from the policy profile, unit-tested
- [ ] Panic → safe look (steady, no flashing), tested

### Phase V3: Stage fixtures
- [ ] Pick 2–4 budget DMX LED pars (RGB or RGBW, 3- or 5-pin XLR) and record them in equipment
- [ ] `dmx-node` next to the Conductor: sACN → DMX512, with a terminator
- [ ] Fixture channel tables written into the light profile
- [ ] (Long-term) Fixtures and DMX cables packed in a road case ([ADR 0010](../../design-docs/0010-road-ready-instruments.md))

### Phase V4: LED matrix display (`matrix-hub75`)
- [ ] One 64×32 HUB75 panel on an ESP32 or Pi: shows song title and tempo from the Control API
- [ ] Jukebox mode: QR code to join + "up next" ([crowd jukebox](../../product-specs/crowd-jukebox.md))
- [ ] Beat-synced visualizer from the note stream
- [ ] Chain panels for a bigger display, mounted on a frame

### Phase V5 (later)
- [ ] Authored-show workflow: lights track in a DAW, preview without the robots
- [ ] Bridge to xLights for pixel-heavy shows
- [ ] Fog/haze (needs venue rules) and moving heads

## Decisions log
- 2026-11-10: Visuals are three tiers (accent, stage, display) on the same MIDI timeline.
  Built light hardware speaks MIDI, bought lighting gets sACN/Art-Net/DMX. Reactive lights by
  default, live and authored cues override. Strobe capped by default, panic = safe look (ADR 0011).

## Open questions
- Budget for V3 fixtures.
- Light profile: extension of the instrument profile or its own schema?
- ESP32 or Raspberry Pi for the matrix (ESP32 is cheaper. Pi is easier for text and QR rendering).
- Wired Ethernet everywhere, or Wi-Fi allowed for small accent nodes?
