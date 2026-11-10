# Spec: Live bar band

Status: Draft · Profile: `live-band`

## Story
The operator (the founder) gigs at a bar with a robotic band. They play a MIDI keyboard or
pads live, trigger arranged parts from a setlist, and set the tempo with their hands, not a
click.

## Must have
- Route a live MIDI controller to any instrument (channel or keyboard-split mapping).
- Setlist of Standard MIDI Files. Start/stop/next from the controller (MIDI Start/Stop/CC) or phone.
- Tempo from a human: tap tempo, incoming MIDI Clock, nudge ±. Songs follow it in real time.
- Per-track mute/solo and instrument assignment (the prototype Explorer's channel toggles grew up).
- Panic: one button on controller *and* phone stops every actuator immediately.
- Instrument health at a glance (connected, latency calibrated, overheating).

## Nice to have
- Follow a live human drummer or bassist via audio onset detection.
- Humanize: velocity and timing variation per instrument.

## Acceptance (high level)
- Live note → sound ≤ the live latency budget in [RELIABILITY.md](../RELIABILITY.md).
- Pulling any USB cable mid-song: that instrument goes silent safely, the rest keep playing.
