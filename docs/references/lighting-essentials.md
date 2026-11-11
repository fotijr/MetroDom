# Lighting essentials (for MIDI people)

Facts an agent or newcomer needs to plan MetroDom visuals. Decisions live in
[ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md).

## The one-line mental model
**DMX is MIDI for lights.** A lighting controller sends a stream of numbers; each fixture listens
to a few of them. The difference: MIDI sends *events* ("note 60 on"), DMX sends the *whole state*
of every channel, continuously, about 40 times a second.

| MIDI | Lighting equivalent |
| --- | --- |
| MIDI cable / port | DMX line ("universe") |
| 16 channels | 512 channels per universe, each a value 0–255 |
| Device listens on a channel | Fixture has a **start address** and uses N channels from there |
| Patch / program | Fixture **mode** ("personality"): which channel does what |
| Note On / CC | Scene/cue recall, or a channel value (dimmer, red, green, strobe…) |
| RTP-MIDI (network MIDI) | **Art-Net** or **sACN**: DMX universes over Ethernet |
| DAW | Lighting console or sequencer (QLC+, xLights) |

## DMX512 (fixtures: pars, washes, moving heads, fog machines)
- Physical: RS-485, 250 kbit/s, daisy-chained fixture to fixture. Put a 120 Ω terminator in the
  last fixture's DMX-out. About 32 fixtures per chain before a splitter is needed.
- Connector: 5-pin XLR is the standard. Many cheap fixtures use 3-pin XLR. **Use real DMX
  cable**, not microphone cable (different impedance; it works short, then fails long).
- A full universe refreshes at ~44 Hz, so a value change can take up to ~23 ms to go out.
- Example: an RGB par in "7-channel mode" at address 1 might use 1 dimmer · 2 red · 3 green
  · 4 blue · 5 strobe · 6 program · 7 speed. The next fixture starts at address 8. The fixture's
  manual holds the channel table for each mode.

## Art-Net and sACN (DMX over the network)
- **Art-Net** (Artistic Licence, free to use): DMX universes in UDP packets, port 6454.
- **sACN** (ANSI E1.31): same idea, UDP multicast on port 5568, priority per source. The
  newer, cleaner standard. Prefer it when both are offered.
- An **Art-Net/sACN node** turns network packets into a physical DMX line. LED controllers
  like WLED accept sACN/Art-Net directly, so LED strips look like DMX fixtures.
- Prefer wired Ethernet at a venue. Bar Wi-Fi is crowded and unreliable.

## MIDI-to-lights routes
- **MIDI mapping:** most lighting software (QLC+ and others) maps incoming Note/CC to scenes,
  faders and buttons. Zero code; good first step.
- **MIDI Show Control (MSC):** a SysEx command set (GO, STOP, cue number) in the MIDI spec, for
  theatre consoles. Useful if we ever drive a real console.
- **MIDI Clock / timecode** syncs chases and effects to tempo.

## Addressable LEDs (strips, rings, matrices you build)
- **WS2812B / SK6812 ("NeoPixel"):** 5 V, one data wire, each LED individually coloured.
  Cheap, everywhere. Timing-sensitive data line.
- **APA102 / SK9822 ("DotStar"):** data + clock wires. Faster refresh, more robust over distance.
- Power is the hard part: up to ~60 mA per LED at full white (60 LEDs ≈ 3.6 A at 5 V). Inject
  power every 1–2 m, fuse each run, cap brightness in software.
- From a 3.3 V board (Pico, ESP32) use a level shifter (e.g. 74AHCT125), ~330 Ω series resistor
  on data, and a large capacitor (≈ 1000 µF) across the strip's supply.
- **WLED:** open-source ESP32 firmware for addressable LEDs: effects, web UI, accepts
  sACN/Art-Net/DDP. The fastest way to get a strip or matrix working.

## LED matrix panels (text, QR codes, visualizers)
- **HUB75** panels (e.g. 64×32 pixels, P4 = 4 mm pitch): cheap, bright, chainable, 16-pin ribbon.
  Driven by an ESP32 (DMA library) or a Raspberry Pi (rpi-rgb-led-matrix). Several amps at 5 V each.
- A WS2812 grid also works as a coarse matrix and plugs into WLED.

## Software worth knowing
- **QLC+**: free lighting console. MIDI in, DMX/Art-Net/sACN out. Fixture library.
- **xLights**: free sequencer from the holiday-lights world. Sequences pixels to music, outputs sACN/Art-Net.
- **WLED**: see above.

## Safety
- **Photosensitive epilepsy:** keep flashing at or below **3 flashes per second** (WCAG 2.3.1,
  broadcast guidelines). Strobe is opt-in per venue policy.
- LEDs at full white are blinding at close range and hot in enclosures. Cap brightness.
- Commercial fixtures are mains-powered, certified units. Never open or modify their mains side.
