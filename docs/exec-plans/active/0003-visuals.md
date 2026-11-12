# 0003: Visuals: lights, LED strips and matrix displays

Status: Active · Last updated: 2026-11-11

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
- [ ] Instrument profile declares its accent LEDs (count, note → LED index, color)
- [ ] Printed diffuser (PLA is fine at home). Brightness capped in firmware
- [ ] Fits the xylophone body from exec plan 0002 without a second power supply design

### Phase V2: Conductor drives lights
- [x] Light profile format, written up next to the [instrument profile](../../references/instrument-profile.md) (shape sketched in ADR 0011 §2): [light profile](../../references/light-profile.md)
- [x] sACN output adapter in the Conductor (UDP, unit-tested with a fake socket): `SacnOutput`, `metrodom lights`
- [x] Reactive mode: color per instrument, pulse on notes, intensity from velocity: `play --lights` ([reactive lights](../../references/light-profile.md#reactive-lights-play---lights))
- [x] Channel 16 lights track and pad scenes override reactive mode: lights track in the song, `play --lights-pad`, master CC ([cues](../../references/light-profile.md#cues-scenes-and-the-master-play---lights))
- [x] Scheduler aligns light changes with measured instrument latency: the rig is a scheduler output, sent early by its own latency
- [x] Strobe cap (≤ 3 flashes/s), brightness cap, quiet hours from the policy profile, unit-tested: policy `lights` ([policy profile](../../references/policy-profile.md#lights))
- [x] Panic → safe look (steady, no flashing), tested

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
- 2026-11-11: V2 started on the software track (roadmap M4, B6 tier 2), ahead of the V0/V1
  hardware, like the Control API and beat following before it. The light profile is its own
  schema, `metrodom.light-profile/0`: a rig is fixtures at DMX addresses with scenes, not notes
  on actuators, so sharing the instrument schema would make both worse. It keeps the instrument
  profile's habits (`latencyMs`, `midi.channel`, defaults from ADR 0011: channel 16, CC 7 master).
  Fixtures name their channels by role so scenes say "red 255" instead of slot numbers; a look
  is a whole picture (unmentioned = 0) so recalling a scene never leaves stale channels lit.
  `strobe` is forced to 0 until a venue policy can allow it, because a fixture's strobe channel
  bypasses any rate cap the Conductor could apply. The safe look defaults to warm and dim. sACN
  timing: send a change on the next 25 ms tick plus two repeats, keep-alive each second (inside
  the receivers' 2.5 s timeout), three terminated packets on stop. `metrodom lights` exists so a
  rig can be checked and lit without a song. Measured against a multicast listener on macOS.
- 2026-11-11: Reactive lights in `play --lights`, with latency alignment and panic → safe look in
  the same change: lights in a set without the panic path would break "safety beats music".
  Fixtures not assigned in the profile are shared out across the band's outputs in turn, so a
  rig with one par per robot needs no config. Color is per MIDI channel (a fixed 16-hue palette,
  drums amber), the simplest stable "which robot is this". The flash cap is enforced in the
  engine, not hoped for from settings: a fixture starts at most 3 pulses a second and ignores
  notes in between (rather than holding or blending, which would still change luminance), over a
  glow that is never dark. Timing hooks the scheduler's `Sent` (send time + that instrument's
  per-note latency − rig latency) instead of making lights a scheduler output: one line in
  `play` and right whenever the rig is no slower than the instrument; the general case is TD-28.
  Panic reaches lights through a `Session.Panicked` event raised after All Notes Off, so lights
  can never delay silencing the band.
- 2026-11-11: US English across the repo (AGENTS.md rule 7): the light profile field
  `reactive.colours` is now `reactive.colors`. Schema stays `/0` (nothing shipped yet).
- 2026-11-11: The rig is one more output of the band, not a listener on the scheduler: the
  orchestra sends it every channel (reactive pulses) and its own channel (cues), and the
  scheduler sends early by the rig's latency. A lights track then follows tempo, vamps and jumps
  for free, and a rig slower than an instrument no longer lands late; the band waits for it
  instead. Under `--lights` channel 16 is taken from every instrument, so a robot can't play a cue.
  Scenes default to `hold` (note length = scene length, natural for a DAW track); `latch` is
  there for pads. Cues are capped at 3 look changes a second (a fast cue waits, the newest
  wins) and the master slews at most full range per third of a second. That caps flashing
  without making fades jumpy. The master survives song changes; scenes don't.
- 2026-11-11: Venue light limits live in the policy profile's `lights` section (ADR 0005), not
  the light profile: the rig belongs to the band, the rules to the room. One flash rate drives
  pulses, cue changes and the master's slew. Raising it above 3 needs `strobe: true`, and 10 is
  the ceiling. Strobe channels pass only with `strobe` on and never in quiet hours. Brightness
  caps (whole day and quiet hours, the lower wins) scale every intensity channel, the safe look
  included, with a 10% floor so panic is never dark. Quiet hours go by the Conductor's local
  clock and may wrap past midnight.
- 2026-11-11 (owner): Fixtures and the matrix are wired Ethernet. Small accent nodes may use
  Wi-Fi through the Conductor's own access point (ADR 0002). Lights fail dark and never hold up
  the music.

## Open questions
- Budget for V3 fixtures.
- ESP32 or Raspberry Pi for the matrix (ESP32 is cheaper. Pi is easier for text and QR rendering).
