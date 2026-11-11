# 0012: Humans in the band: play-along

Status: **Proposed** (2026-11-10)

## Context
The owner wants to **play with the robots**: jump on the drum set while MetroDom plays the
other parts, or play guitar over MetroDom drums and bass. That's the clearest expression of
[core belief 2](core-beliefs.md) ("humans own time") and of what makes MetroDom different from a
backing track: the band listens and adapts.

Play-along raises three problems the robot-only band doesn't have:
1. **Which part is the human's?** The robots must leave it out.
2. **Who keeps time?** Robots need lookahead ([ADR 0004](0004-latency-compensation.md)), but a
   human doesn't announce the next beat in advance.
3. **The human's hands are busy.** Control and cues must work hands-free, without a click track.

## Decision

### 1. Seats: the human takes a part, the arranger leaves it out
- A session has **seats**: `{ player, role }`, e.g. `{ "Dom", "drums" }`. Roles are the same names
  the arranger uses for parts (drums, bass, guitar, keys, melody…).
- Song tracks are tagged with roles: from track names, General MIDI program/channel 10 (drums),
  or an explicit mapping saved per song.
- When a seat is filled, the arranger **mutes the matching parts** (a "minus-one" mix) and the
  rest of the band plays as usual. This generalizes today's `metrodom play --mute`.

### 2. Two tempo modes, simplest first
| Mode | Who keeps time | How | When |
| --- | --- | --- | --- |
| **Band leads** | The Conductor | Song tempo, tap tempo or foot-pedal nudges. The human follows, like joining any band | First. Works with any instrument the human plays |
| **Human leads** | The human player | The Conductor tracks the player's beats and the band follows | Later. Drums first: a drummer is the natural timekeeper |

**Human leads** inputs, easiest first:
1. Foot pedal tap tempo and nudge (works for any instrument).
2. **Drum triggers**: clip-on piezo triggers on the owner's acoustic kit, turned into MIDI notes
   by a `trigger-piezo` module (same piezo approach as `latency-jig`). Clean, low-latency beat events.
3. Audio onset / beat tracking from a microphone (research; needed for guitar-led tempo).

**Following needs prediction.** Robots must fire *before* the beat they're aiming for, so the
Conductor's tempo tracker **predicts the next beats** from the player's recent ones, corrects
phase gradually, and caps tempo change per beat so the band never lurches. If the tracker
loses confidence (a fill, a pause), it holds the last stable tempo.

### 3. Cues without a click track
- **Count-in on a real instrument** (e.g. robot woodblock or hi-hat clicks, 1 or 2 bars). MetroDom
  doesn't synthesize audio ([non-goals](../PRODUCT_SENSE.md)), so no in-ear click is generated.
- **Visual cues** through the lights ([ADR 0011](0011-visuals-are-part-of-the-band.md)): beat
  pulse, bar count, "next section" on the matrix display.
- Song sections come from **SMF marker meta events** (a MIDI standard): verse, chorus, outro.

### 4. Hands-free control
A MIDI foot controller (bought, or a `footswitch-midi` module: a brain running the generic
firmware with stomp switches) sends standard CC/notes:
start/stop · next song · tap tempo · **vamp** (repeat the current section until released)
· jump to outro · **panic**. Mappings live in the live-band policy profile
([ADR 0005](0005-deployment-modes-are-policy.md)).

### 5. Safety and balance
- Moving actuators within reach of a human player are **guarded**. A robot never plays the same
  drum or string a human is playing.
- Panic is reachable by foot.
- Human drums are loud. Per-instrument velocity scaling and placement keep the robots audible.
  Mic'ing and amplifying the acoustic robots is allowed; synthesizing them is not.

## Consequences
- The arranger gains roles and seats. The scheduler gains a tempo tracker with prediction,
  tested with recorded human beat timings and a fake clock.
- Song files benefit from role-named tracks and section markers. Files without them still work
  (band-leads mode, whole-song playback).
- Two new planned hardware modules: `trigger-piezo` and `footswitch-midi`.
- "Guitar over MD drums **and bass**" needs a bass voice. None is owned yet: a bass guitar would
  reuse the guitar picker modules, or the piano's low octave could stand in.

## Alternatives considered
- **Human always follows a fixed tempo:** simple, but that's just a click track with extra steps.
  Kept only as the band-leads starting point, with live tempo control on top.
- **Audio beat tracking from day one:** works for any instrument, but it's the hardest and least
  reliable input. Triggers and a foot pedal come first.

## Open questions
- Role vocabulary: a fixed list or free-form tags? Draft: fixed list for detection, any one word in a saved mapping ([song roles](../references/song-roles.md)).
- How fast should the band follow a tempo change (beats to converge) without sounding nervous?
- Can a robot share the drum kit with the human (e.g. robot on a cymbal while the human plays
  the rest), or are kits strictly one or the other?
