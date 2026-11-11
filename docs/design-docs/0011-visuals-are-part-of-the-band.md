# 0011: Visuals are part of the band: lights on the same MIDI timeline

Status: **Proposed** (2026-11-10)

## Context
Stage lights, LED strips and LED matrix displays make a robotic band far more fun to watch, and
lights can do something no backing track can: **show the audience which robot is playing which
note.** That supports the product's main point ([PRODUCT_SENSE.md](../PRODUCT_SENSE.md)): this
music is real and physical.

The owner knows MIDI, not lighting. The lighting world has its own standards (DMX512, Art-Net,
sACN; see [lighting essentials](../references/lighting-essentials.md)). We want lights driven
from the same song files, controllers and tempo as the instruments, without inventing protocols.

## Decision

### 1. Three tiers of visuals
| Tier | What | Driven by | Built or bought |
| --- | --- | --- | --- |
| **Accent** | LEDs on each instrument that flash when an actuator fires | The instrument's own brain, from the same Note On. No extra protocol | Built: `led-accent` module |
| **Stage** | Pars, washes, LED strips, fog: the "look" of the show | Conductor → sACN/Art-Net → fixtures, WLED nodes, DMX node | Bought fixtures + built LED nodes |
| **Display** | LED matrix: now playing, jukebox QR code, tempo, visualizer | Control API state stream ([ADR 0003](0003-control-api.md)), plus beat/notes for effects | Built: `matrix-hub75` module |

### 2. A light rig is just another instrument
Lights get a **light profile**, the lighting counterpart of the
[instrument profile](../references/instrument-profile.md). It maps MIDI to looks:
- **Notes → scenes/cues.** Default notes 36–51 (a 4×4 pad grid on most controllers) recall 16 looks.
- **CCs → live controls.** CC 7 master dimmer, CC 1 effect speed, others per rig.
- **Latency** is measured like any instrument, so the scheduler ([ADR 0004](0004-latency-compensation.md))
  times light changes to land *with* the sound, not before it.

The arranger then treats a lights part like any other part. Default MIDI channel for lights:
**16** (configurable per song and rig).

### 3. Three ways to make a light show, simplest first
1. **Reactive (no authoring):** the Conductor turns the note stream into light: each instrument
   has a colour, its fixtures pulse with its notes, intensity follows velocity and density.
   Every song gets a show for free.
2. **Live:** the operator's pad controller triggers scenes on channel 16 during the gig.
3. **Authored:** a "Lights" track in the Standard MIDI File, written in any DAW. Notes are cues,
   CCs are fades. The show lives in the song file and plays back in time with the human tempo.

Reactive is the default and is overridden by live or authored cues.

### 4. Standards at each boundary
- MetroDom-built light hardware (accent LEDs) speaks **MIDI** like any instrument
  ([ADR 0001](0001-midi-is-the-contract.md)).
- Off-the-shelf lighting speaks **sACN** (preferred) or **Art-Net** from the Conductor, through a
  network switch next to the Conductor (in the [rig head](0010-road-ready-instruments.md) if the band ever travels). A node converts to **DMX512**
  for fixtures. WLED controllers take sACN directly.
- The Conductor gains a light output adapter (MIDI timeline in → light profile → sACN out). No
  custom lighting protocol.
- Bridging to existing software is allowed: QLC+ or xLights can be fed MIDI from the Conductor
  for complex shows.

### 5. Safety and policy
- **Strobe and fast flashing are capped at 3 flashes per second by default** (photosensitive
  epilepsy guideline). Higher rates only if the venue's policy profile enables strobe
  ([ADR 0005](0005-deployment-modes-are-policy.md)).
- Brightness caps and quiet hours come from the policy profile.
- **Panic does not black out the stage.** Panic stops actuators. Lights go to a steady,
  non-flashing "safe look" so nobody trips in the dark.
- LED power runs on its own fused 5 V supply, never shared with actuator rails.
- Lights never block music: a dead light node is logged, songs keep playing.

## Consequences
- The rig needs an Ethernet switch, and visuals become the first network-dependent part.
- Accent LEDs add a 5 V LED supply and a strip to every body: cheap, but it's more current to budget.
- Song files may gain a lights track. Files without one still get reactive lights.
- A new reference doc and a new profile format (light profile) to maintain.

## Alternatives considered
- **QLC+ as the whole lighting engine:** fastest start and great for learning (exec plan phase
  V0), but a second app with its own state at every gig. Kept as a bridge, not the core.
- **MIDI-to-DMX hardware boxes:** exist, but sACN from the Conductor is cheaper and needs no
  extra box per universe.
- **Video projection:** possible later as a Control API client. Out of scope for now.

## Open questions
- Light profile format: extend the instrument profile with a `kind: light` or separate schema?
- Exact reactive mapping (colour per instrument, decay times) needs to be tried in a real room.
- How many fixtures and which ones for a bar-sized rig? Budget first, see exec plan 0003.
