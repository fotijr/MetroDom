# Light profile and sACN output

Status: **Draft**. The lighting counterpart of the [instrument profile](instrument-profile.md)
([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md) §2). New to lighting? Read
[lighting essentials](lighting-essentials.md) first. Code: `src/MetroDom.Core/Lighting/`
(profile, rendering, E1.31 packets, reactive mode, cues), `src/MetroDom.Conductor/Lighting/`
(sender, the show in `play`), `metrodom lights`. Limits: TD-28 in the [tech-debt tracker](../exec-plans/tech-debt-tracker.md).

```bash
metrodom lights rig.json                         # check it, print the patch and scenes
metrodom lights rig.json --scene 36              # hold a scene until Ctrl+C
metrodom lights rig.json --safe --to 192.168.1.40   # the safe look, unicast to one WLED
metrodom play set.mid -o "MetroDom xylo=1-9,11-16" -o "MetroDom drums=10" --lights rig.json
metrodom play set.mid --lights rig.json --lights-pad "Launchpad"   # live scenes from a pad grid
```

## Format (`metrodom.light-profile/0`)

```json
{
  "schema": "metrodom.light-profile/0",
  "name": "Home rig",
  "midi": { "channel": 16, "cues": "hold" },
  "controls": { "masterCc": 7 },
  "latencyMs": 30,
  "output": { "protocol": "sacn", "priority": 100 },
  "fixtures": [
    { "id": "left-par",  "universe": 1, "address": 1, "channels": ["dimmer", "red", "green", "blue", "strobe"] },
    { "id": "right-par", "universe": 1, "address": 6, "channels": ["dimmer", "red", "green", "blue", "strobe"] },
    { "id": "strip",     "universe": 2, "address": 1, "channels": ["red", "green", "blue"], "count": 60 }
  ],
  "scenes": [
    { "note": 36, "name": "Blue wash", "set": { "*": { "dimmer": 255, "blue": 255 } } },
    { "note": 37, "name": "Warm",      "set": { "*": { "dimmer": 200, "red": 255, "green": 120 }, "strip": { "red": 80, "green": 40 } } }
  ],
  "safeLook": { "*": { "dimmer": 140, "red": 160, "green": 140, "blue": 110 } },
  "reactive": { "baseLevel": 40, "decayMs": 300, "colors": { "10": "#ffb000" }, "follow": { "right-par": [10] } }
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `name` | Shown in tables and as the sACN source name | required |
| `midi.channel` | MIDI channel the rig listens on, 1-16 | 16 |
| `midi.cues` | `hold`: a scene lasts as long as its note. `latch`: until another scene note, the same note again goes back to reactive | `hold` |
| `controls.masterCc` | Controller for the master dimmer, 0-119 | 7 |
| `latencyMs` | Command → visible change, measured like an instrument's | 0 |
| `output.destination` | One IP or host for unicast (e.g. a WLED); leave out for multicast | multicast |
| `output.priority` | sACN priority 0-200; receivers follow the highest source | 100 |
| `fixtures[]` | `id`, `universe` (1-63999, default 1), `address` (1-512), `channels` in DMX order, `count` (pixels, default 1) | |
| `scenes[]` | `note` (0-127, unique), `name`, `set` (a look) | none |
| `safeLook` | A look for panic and stopping | warm, dim, steady |
| `reactive.baseLevel` | Idle glow between notes, 0-255 | 40 |
| `reactive.decayMs` | How long a pulse fades | 300 |
| `reactive.colors` | Channel (1-16) → `#rrggbb` | a 16-hue palette, drums (10) amber |
| `reactive.follow` | Fixture id → channels it pulses with | shared out across the band |

- **Channel roles:** `dimmer`, `red`, `green`, `blue`, `white`, `amber`, `uv` are intensity (the
  master scales them). `strobe` is sent as 0 unless the venue's policy allows it (below). Any other name (`program`,
  `speed`, `pan`) is a channel the profile can set directly; it isn't scaled.
- **Looks:** fixture id → role → 0-255. `"*"` is every fixture; a fixture's own entry wins for
  the roles it names. Anything not mentioned is 0, so a scene is the whole picture.
- **Pixel strips** (`count` > 1): the channels repeat per pixel and every pixel gets the same
  value. Per-pixel effects are later.
- Checked when read: unique ids, scene notes and fixtures that exist, values 0-255, no fixture
  running past 512 or overlapping another in its universe.
- Defaults follow ADR 0011: lights on channel 16, notes 36-51 for a 4×4 pad grid, CC 7 master.
- `lights --master` sets the master by hand; in `play` the master CC does (below).

## Reactive lights (`play --lights`)

Every song gets a show with no authoring (ADR 0011 §3.1). Code:
`src/MetroDom.Core/Lighting/ReactiveLights.cs`, `src/MetroDom.Conductor/Lighting/LightShow.cs`.

- **Who follows whom:** fixtures in `reactive.follow` pulse with those channels. The others are
  shared out across the band's outputs in turn, as the band is at the start of the set: with
  `-o xylo=1-9,11-16 -o drums=10`, fixture 1 follows the xylophone, fixture 2 the drums,
  fixture 3 the xylophone again. With no band (never in `play`), a fixture follows everything.
  `play` prints the plan (`Reactive: left-par ch 1-9,11-15, right-par ch 10, …`).
- **A pulse:** a Note On lifts the fixture from the glow to `glow + (255 − glow) × velocity/127`
  in its channel's color, fading back linearly over `decayMs`. A fixture with a `dimmer` gets
  full color with the level on the dimmer; one without (a strip) gets its color scaled.
  Idle fixtures glow in the color of the first channel they follow. Channel 16 (the lights
  channel) never pulses: it is for cues.
- **On time:** the rig is one more output of the band. The orchestra sends it every channel,
  and the scheduler sends to it early by `latencyMs`, as it does for each instrument's latency,
  so light and sound land together (ADR 0004). A rig slower than every instrument makes the band
  wait for it, like a slow instrument does: `play` shows it in the lookahead. Frames go out every 20 ms
  (50 per second).
- **Flash cap:** a fixture starts at most 3 pulses a second; notes inside that window leave it
  alone (they don't restart the pulse or change its color). At 100 BPM eighth notes (300 ms)
  that means every other note. The glow is never dark, so a pulse is never dark-to-full.
- **Panic** (pedal, Esc, API): the safe look at once, steady, until the next song starts.
  Muted channels and seats don't pulse (muted notes never reach the scheduler's `Sent`).
- **End of set** (last song, Ctrl+C, SIGTERM): the safe look in full packets, then three
  terminated packets (receivers ignore data in those).
- Measured on macOS with two virtual instruments and the example rig: idle fixtures glowed blue
  and amber, each pulsed with its own instrument (every other eighth note, never more than 2 a
  second), and a panic through the Control API showed the safe look about 70 ms later and
  nothing after it.

## Cues: scenes and the master (`play --lights`)

Live and authored shows (ADR 0011 §3.2, §3.3) override reactive mode. Code:
`src/MetroDom.Core/Lighting/LightCues.cs`.

- **Lights track:** events on `midi.channel` (16) in the song are the rig's. Notes recall
  scenes, the master CC dims. They go through the scheduler like any part, so they follow the
  human tempo, vamps and jumps, and land with the sound. `--arrange` leaves the lights track as
  written (it is never handed to a robot).
- **Pad grid** (`--lights-pad PORT`): every note and controller from that input is a cue, on
  whatever channel the pad sends, effective at once. The default scene notes 36-51 are a 4×4
  grid on most controllers. It needs a port of its own (not `--control` or `--tap`'s).
- **Channel 16 belongs to the lights.** With `--lights` no instrument gets the rig's channel,
  even one assigned 1-16 or plugged in mid-set, so no robot plays a cue note. `play` says
  which outputs lost it.
- **Scene notes:** a note with a scene recalls it, other notes are ignored. In `hold` mode the
  scene lasts until its Note Off: the last held scene wins, and releasing everything returns to
  reactive. In `latch` mode a scene stays until another scene note; the same note again
  returns to reactive. All Notes Off / All Sound Off release held scenes.
- **Master** (`controls.masterCc`, 0-127 → 0-255): scales scenes and reactive lights alike.
  It stays where it was left across songs (the operator's fader is the truth). The safe look
  ignores it, so panic is never dark because a fader was down.
- **A new song starts in reactive** with no scene up, and panic clears cues.
- **Flash cap:** the look changes at most 3 times a second. A cue that comes sooner waits for
  the gap and the newest wins, so a fast lights track or pad mashing can't flash. The master
  moves at most full range per third of a second, so a toggled fader can't strobe; a fade
  slower than that is followed exactly.
- Measured on macOS with virtual instruments, the example rig and a lights track (scene at
  beat 2, release at beat 6, a second scene at beat 8, a master fade over beats 10-12, back up
  at beat 13). Every change arrived on its beat, give or take one 20 ms frame. The fade
  ramped smoothly to black and back, and the xylophone (assigned 1-16) never got a cue note.
  `--lights-pad` is unit-tested only.

## Safety

- **No strobe unless the venue says so.** A fixture's built-in strobe flashes far faster than
  the 3-per-second cap, so `strobe` channels are 0 unless the policy profile's `lights.strobe`
  is true, and never during its quiet hours (ADR 0011 §5).
- **Venue limits** (policy profile `lights`, see [policy profile](policy-profile.md#lights)):
  the flash rate for pulses, cues and the master, a brightness cap, and quiet hours with a
  lower cap. `play --policy` and `lights --policy` apply them. The brightness cap applies to the
  safe look too, but can't go below 10%, so panic is never dark.
- **Safe look, not blackout:** a profile without `safeLook` gets a warm, dim, steady look
  (dimmer 160, red 160, green 120, blue 80, white/amber 100).
- **Lights never block music:** a failed send is logged once per run of failures, never thrown.

## sACN as sent (ANSI E1.31-2018)

- UDP port 5568. Multicast to `239.255.hi.lo` for universe `hi·256 + lo` (universe 1 →
  239.255.0.1), TTL 1 so it stays on the rig's network. Loopback is on, so QLC+ on the same
  computer hears it.
- Full 638-byte data packets: start code 0 and all 512 slots. Root vector 4, framing vector 2,
  DMP vector 2, address/data type `0xA1`. Big-endian. A random CID per run. Sequence numbers per
  universe, wrapping at 255.
- **Timing:** a changed frame goes out on the next 25 ms tick and on the two after it (for lost
  packets); an unchanged one every second as keep-alive (receivers give up after 2.5 s).
- **Stopping:** three packets with the Stream_Terminated option bit (`0x40`), so receivers let
  go at once.
- Measured on macOS: a separate listener on the multicast group got 638-byte packets for
  universes 1 and 2 with the scene's values in the right slots (strobe 0), three sends 25 ms
  apart, keep-alives about 1 s apart, and three terminated packets per universe on stop
  (`--for` and SIGTERM).
