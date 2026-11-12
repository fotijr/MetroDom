# Light profile and sACN output

Status: **Draft**. The lighting counterpart of the [instrument profile](instrument-profile.md)
([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md) §2). New to lighting? Read
[lighting essentials](lighting-essentials.md) first. Code: `src/MetroDom.Core/Lighting/`
(profile, rendering, E1.31 packets), `src/MetroDom.Conductor/Lighting/SacnOutput.cs` (sender),
`metrodom lights`. Limits: TD-28 in the [tech-debt tracker](../exec-plans/tech-debt-tracker.md).

```bash
metrodom lights rig.json                         # check it, print the patch and scenes
metrodom lights rig.json --scene 36              # hold a scene until Ctrl+C
metrodom lights rig.json --safe --to 192.168.1.40   # the safe look, unicast to one WLED
```

## Format (`metrodom.light-profile/0`)

```json
{
  "schema": "metrodom.light-profile/0",
  "name": "Home rig",
  "midi": { "channel": 16 },
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
  "safeLook": { "*": { "dimmer": 140, "red": 160, "green": 140, "blue": 110 } }
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `name` | Shown in tables and as the sACN source name | required |
| `midi.channel` | MIDI channel the rig listens on, 1-16 | 16 |
| `controls.masterCc` | Controller for the master dimmer, 0-119 | 7 |
| `latencyMs` | Command → visible change, measured like an instrument's | 0 |
| `output.destination` | One IP or host for unicast (e.g. a WLED); leave out for multicast | multicast |
| `output.priority` | sACN priority 0-200; receivers follow the highest source | 100 |
| `fixtures[]` | `id`, `universe` (1-63999, default 1), `address` (1-512), `channels` in DMX order, `count` (pixels, default 1) | |
| `scenes[]` | `note` (0-127, unique), `name`, `set` (a look) | none |
| `safeLook` | A look for panic and stopping | warm, dim, steady |

- **Channel roles:** `dimmer`, `red`, `green`, `blue`, `white`, `amber`, `uv` are intensity (the
  master scales them). `strobe` is always sent as 0 (below). Any other name (`program`,
  `speed`, `pan`) is a channel the profile can set directly; it isn't scaled.
- **Looks:** fixture id → role → 0-255. `"*"` is every fixture; a fixture's own entry wins for
  the roles it names. Anything not mentioned is 0, so a scene is the whole picture.
- **Pixel strips** (`count` > 1): the channels repeat per pixel and every pixel gets the same
  value. Per-pixel effects are later.
- Checked when read: unique ids, scene notes and fixtures that exist, values 0-255, no fixture
  running past 512 or overlapping another in its universe.
- Defaults follow ADR 0011: lights on channel 16, notes 36-51 for a 4×4 pad grid, CC 7 master.
- Not used yet: `midi.channel`, `controls.masterCc` and `latencyMs` are parsed for the reactive
  and live modes that come next (plan 0003 V2). `lights --master` sets the master by hand.

## Safety

- **No strobe.** A fixture's built-in strobe flashes far faster than the 3-per-second cap and
  no venue policy can allow it yet, so `strobe` channels are always 0 (ADR 0011 §5).
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
