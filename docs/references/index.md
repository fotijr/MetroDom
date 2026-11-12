# References

Protocol and format facts agents need while coding. Keep each file factual and terse.

| Reference | Use when |
| --- | --- |
| [midi-essentials.md](midi-essentials.md) | Writing anything that sends/parses MIDI or SMF |
| [instrument-profile.md](instrument-profile.md) | Building an instrument or the Conductor registry |
| [midi-identity.md](midi-identity.md) | Identity Request/Reply bytes, MetroDom's identity, `devices --identify` |
| [midi-profile-exchange.md](midi-profile-exchange.md) | MetroDom SysEx profile request/reply, discovery step 3, Body Write |
| [body-record.md](body-record.md) | The body's ID EEPROM: `metrodom.body/0` files, the byte layout, `metrodom body check/write`, what a brain boots as |
| [auto-band.md](auto-band.md) | `play` without `-o`, hot-plug, `devices --assign/--forget`, `metrodom.assignments/0` (discovery step 4) |
| [control-api.md](control-api.md) | Control API v0: tokens and roles, routes, queue and library, the web remote, the `openapi.json` contract |
| [conductor-service.md](conductor-service.md) | `metrodom serve`: library, editable queue, `state.json`, mDNS, restart behavior |
| [policy-profile.md](policy-profile.md) | `metrodom.policy/0`, foot-controller mapping, `play --control`, limits, roles, queue rules, sessions |
| [beat-following.md](beat-following.md) | Human leads: `BeatTracker`, `BeatFollower`, `play --follow`, `beats`, `simulate --follow`, hit files |
| [song-roles.md](song-roles.md) | Part roles (drums, bass, melody…), detection order, `song.roles.json`, `metrodom roles`, seats (`--seat`) |
| [calibration.md](calibration.md) | Measuring latency: `metrodom calibrate`, onset inputs, the boot check (`--onsets`), `metrodom.calibrations/0` |
| [virtual-band.md](virtual-band.md) | Prototyping without hardware: virtual instruments, `simulate`, `virtual` |
| [legacy-serial-protocol.md](legacy-serial-protocol.md) | Touching or porting the 2017 firmware |
| [lighting-essentials.md](lighting-essentials.md) | Planning lights: DMX, Art-Net/sACN, LED strips, matrices, MIDI-to-light |
| [light-profile.md](light-profile.md) | `metrodom.light-profile/0`, fixtures and scenes, sACN packets and timing, `metrodom lights` |
| [glossary.md](glossary.md) | A term in the docs is unclear |

External specs (authoritative): MIDI 1.0 & 2.0 specs at midi.org · RTP-MIDI: RFC 6295 ·
OSC 1.0: opensoundcontrol.stanford.edu · DNS-SD: RFC 6763 · DMX512: ANSI E1.11 · sACN: ANSI E1.31.
