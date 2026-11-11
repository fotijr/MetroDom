# Spec: Live bar band

Status: Draft · Profile: `live-band`

## Story
The operator (the founder) gigs at a bar with a robotic band. They play a MIDI keyboard or
pads live, trigger arranged parts from a setlist, and set the tempo with their hands, not a
click.

First at home: the same setup played in the living room is the near-term goal. Gigging is
long-term ([roadmap](../ROADMAP.md)).

## Must have
- Route a live MIDI controller to any instrument (channel or keyboard-split mapping).
- Setlist of Standard MIDI Files. Start/stop/next from the controller (MIDI Start/Stop/CC) or phone.
- Tempo from a human: tap tempo, incoming MIDI Clock, nudge ±. Songs follow it in real time.
- Per-track mute/solo and instrument assignment (`metrodom play --mute` is the seed).
- Panic: one button on controller *and* phone stops every actuator immediately.
- Instrument health at a glance (connected, latency calibrated, overheating).
- **Play along** ([ADR 0012](../design-docs/0012-humans-in-the-band.md)): the operator takes a seat
  (e.g. drums or guitar), the robots leave that part out and play the rest. Count-in on a real
  instrument, visual beat/section cues, foot-pedal control (start/stop, next, tap, vamp, panic).

## Nice to have
- Light show synced to the band: reactive by default, pads trigger scenes live ([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md)).
- **Human leads:** the band follows the operator's drumming via drum triggers, later any
  instrument via audio onset detection.
- Bass voice for "guitar + MD drums and bass" (no bass instrument owned yet).
- Humanize: velocity and timing variation per instrument.

## Acceptance (high level)
- Live note → sound ≤ the live latency budget in [RELIABILITY.md](../RELIABILITY.md).
- Operator plays guitar or drums through a full song with the robots (band-leads mode)
  without touching a laptop.
- Pulling any USB cable mid-song: that instrument goes silent safely, the rest keep playing.
