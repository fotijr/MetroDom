# References

Protocol and format facts agents need while coding. Keep each file factual and terse.

| Reference | Use when |
| --- | --- |
| [midi-essentials.md](midi-essentials.md) | Writing anything that sends/parses MIDI or SMF |
| [instrument-profile.md](instrument-profile.md) | Building an instrument or the Conductor registry |
| [midi-identity.md](midi-identity.md) | Identity Request/Reply bytes, MetroDom's identity, `devices --identify` |
| [midi-profile-exchange.md](midi-profile-exchange.md) | MetroDom SysEx profile request/reply, discovery step 3 |
| [auto-band.md](auto-band.md) | `play` without `-o`, hot-plug, `devices --assign/--forget`, `metrodom.assignments/0` (discovery step 4) |
| [control-api.md](control-api.md) | Control API v0: `play --api`, token, routes, the `openapi.json` contract |
| [policy-profile.md](policy-profile.md) | `metrodom.policy/0`, foot-controller mapping, `play --control`, setlists |
| [beat-following.md](beat-following.md) | Human leads: `BeatTracker`, `BeatFollower`, `play --follow`, `beats`, `simulate --follow`, hit files |
| [song-roles.md](song-roles.md) | Part roles (drums, bass, melody…), detection order, `song.roles.json`, `metrodom roles`, seats (`--seat`) |
| [virtual-band.md](virtual-band.md) | Prototyping without hardware: virtual instruments, `simulate`, `virtual` |
| [legacy-serial-protocol.md](legacy-serial-protocol.md) | Touching or porting the 2017 firmware |
| [lighting-essentials.md](lighting-essentials.md) | Planning lights: DMX, Art-Net/sACN, LED strips, matrices, MIDI-to-light |
| [light-profile.md](light-profile.md) | `metrodom.light-profile/0`, fixtures and scenes, sACN packets and timing, `metrodom lights` |
| [glossary.md](glossary.md) | A term in the docs is unclear |

External specs (authoritative): MIDI 1.0 & 2.0 specs at midi.org · RTP-MIDI: RFC 6295 ·
OSC 1.0: opensoundcontrol.stanford.edu · DNS-SD: RFC 6763 · DMX512: ANSI E1.11 · sACN: ANSI E1.31.
