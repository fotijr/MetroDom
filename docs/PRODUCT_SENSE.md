# Product sense

## Why MetroDom exists
Live music increasingly runs on click tracks and backing tracks. The show is locked to a grid
and half of what you hear was recorded. MetroDom goes the other way: **every sound comes from a
real, physical instrument being struck, plucked or bowed in the room**, and the timing follows a
human, not a sequencer. Robots do the playing. People decide what's played and how it feels, and people can **play
along**: the owner sits down at the drums or picks up a guitar, and the robots play the rest
of the band around them.

It is open source and built on open standards (MIDI above all) so anyone can add an instrument,
write a control app, or run their own band.

## Priorities
1. **Now: a working band at home.** Instruments that play, a band that plays songs together,
   and the owner playing along ([roadmap](ROADMAP.md)).
2. **Long-term: taking it out.** Gigs, portability ([ADR 0010](design-docs/0010-road-ready-instruments.md)),
   the jukebox and rental scenarios. MetroDom may stay a home project, so these are kept
   *possible* by the architecture, not built early.

## Who it's for: three deployment scenarios
The architecture has to serve all three without forking. All three start as a home setup. See
[product-specs/](product-specs/index.md) for full specs.

| Scenario | Who controls it | Shape of control | Spec |
| --- | --- | --- | --- |
| **Bar band** | One operator (the founder) | Live: MIDI keyboard, tap tempo, setlist, improvisation | [live-band.md](product-specs/live-band.md) |
| **Crowd jukebox** | Venue owner sets rules; patrons request and vote | Queue: request, vote, skip, with moderation | [crowd-jukebox.md](product-specs/crowd-jukebox.md) |
| **Rental quartet** | A customer, for one night, with no training | "As easy as Spotify": browse, play, pause, volume | [rental-quartet.md](product-specs/rental-quartet.md) |

## Product principles
1. **Real sound, live feel.** Prefer human-driven tempo and expression over a perfect grid.
   Slight imperfection is a feature. Missed or late notes are bugs.
2. **Zero-setup for non-experts.** The rental customer plugs in power, joins Wi-Fi, opens an
   app. No drivers, no MIDI knowledge, no laptop.
3. **Never embarrassing.** A crashed app or flaky Wi-Fi must not leave a solenoid buzzing or the
   band silent. Degrade gracefully: keep playing the queue, allow panic from any owner device.
4. **Open by default.** MIDI files in, MIDI messages out, documented API. No lock-in for songs or
   instruments.
5. **Humans are welcome in the band.** Any player can take a part (drums, guitar, bass…) and the
   robots leave it out and play the rest. The band either keeps time for them or follows them
   ([ADR 0012](design-docs/0012-humans-in-the-band.md)).
6. **The owner is always in charge.** Whatever the crowd does, the owner/operator can override,
   skip, mute, or stop.

## Non-goals (for now)
- Audio synthesis or sample playback. If it isn't acoustic, it isn't MetroDom.
- Recording/production workflows (a DAW can already do that over MIDI).
- Robotic singing.
