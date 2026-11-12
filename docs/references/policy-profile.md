# Policy profile (first cut: controls and lights)

Status: **Draft**. The versioned config file of [ADR 0005](../design-docs/0005-deployment-modes-are-policy.md).
Two sections exist so far: `controls`, the foot-controller mapping of
[ADR 0012](../design-docs/0012-humans-in-the-band.md) §4, and `lights`, what the venue lets the
lights do ([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md) §5). Roles, queue
rules and instrument limits join it as they're built (today those limits are `play` flags). Code: `src/MetroDom.Core/Policy/PolicyProfile.cs`.

## Format (`metrodom.policy/0`)

The shipped live-band profile, `policies/live-band.json` (also built in, used when `--policy` is
not given):

```json
{
  "schema": "metrodom.policy/0",
  "mode": "live-band",
  "controls": {
    "start-stop": "cc:80",
    "next": "cc:81",
    "tap": "cc:64",
    "vamp": "cc:82",
    "outro": "cc:83",
    "panic": "cc:85"
  }
}
```

- Each control is a trigger, written like `--tap-on`: `note[:N]`, `cc[:N]` (press = value
  crossing up to 64 or more; the release never fires), `pc[:N]`, optionally `@CHANNEL` (1-16).
  `any` is not allowed here, and two controls can't share a trigger.
- Leave a control out to leave it unmapped.
- Why these defaults: CC 64 is the sustain pedal most keyboards' pedals send, so a cheap pedal
  taps out of the box. CC 80-83 and 85 are General Purpose controllers with no sound meaning in
  General MIDI, which the planned `footswitch-midi` module will send.

## Actions in `play`

| Control | Waiting for start | Playing | Key |
| --- | --- | --- | --- |
| `start-stop` | Start the song | Stop it (then wait) | Enter |
| `next` | Skip this song | Next song now | N |
| `tap` | Sets the next song's tempo | Tap tempo | Space |
| `vamp` | - | Toggle vamp on this section | V |
| `outro` | - | Outro after this section | O |
| `panic` | Silence | Stop every actuator now, then wait to restart the same song | Esc |

```bash
metrodom play set/*.mid -o "MetroDom xylo=1" -o "MetroDom drums=10" --control "FS-5U" [--policy my-band.json]
```

- Several songs make a setlist. With a `start-stop` control, each song waits for the pedal
  (count off with the tap pedal first, and the song starts at that tempo). Without one, songs
  follow each other.
- Panic runs on the input thread before anything else: the scheduler drops what's pending and
  every output gets All Sound Off / All Notes Off. In a script with no way to restart, panic ends
  the set.
- `--control` can't share a port with `--tap`/`--clock-in`: map tap in the policy instead.

Measured on macOS (scripted pedals on a virtual port → `play` two songs → `virtual`): start,
next, panic, start produced song 1, song 2, silence with "Ready: demo.mid (2/2)", then song 2
again, each within a few ms of the pedal.

## Lights

What the room allows, whatever rig plays in it. Leave the section out for the safe defaults:
at most 3 flashes a second, no strobe, full brightness. Code: `src/MetroDom.Core/Lighting/LightLimits.cs`.

```json
"lights": {
  "strobe": false,
  "maxFlashesPerSecond": 3,
  "maxBrightness": 80,
  "quietHours": { "from": "23:00", "to": "07:00", "maxBrightness": 30 }
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `strobe` | Lets fixtures' own `strobe` channels through (scenes only, never in quiet hours) | false |
| `maxFlashesPerSecond` | Most look changes a second: pulses per fixture, scene changes, and the master's slew (full range per flash). Above 3 needs `strobe`; at most 10 | 3 |
| `maxBrightness` | Cap on every intensity channel, percent, 10-100 | 100 |
| `quietHours` | `from` / `to` as local `HH:mm` (may wrap past midnight, `to` not included), `maxBrightness` 10-100 (the lower cap wins) | none |

- Applied by `play --lights` (with `--policy`) and `metrodom lights --policy`. `play` prints the
  limits next to the rig.
- The caps apply to the safe look too. The 10% floor keeps it visible, so panic is never dark.
- 3 flashes a second is the photosensitive-epilepsy guideline. A venue can only go faster by
  turning strobe on, which is a deliberate choice written in its file.
- Measured on macOS with `venue.json` (2 flashes/s, 50%): `lights --scene 36 --policy` sent dimmer
  128 for a 255 scene. In `play --lights`, pulses were at least 0.6 s apart (every quarter note at
  100 BPM, eighths skipped), peaks never passed half brightness, and the end-of-set safe look
  came out at half (70 for 140).
