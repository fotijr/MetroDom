# Policy profile (first cut: controls)

Status: **Draft**. The versioned config file of [ADR 0005](../design-docs/0005-deployment-modes-are-policy.md).
Only the `controls` section exists so far: the foot-controller mapping of
[ADR 0012](../design-docs/0012-humans-in-the-band.md) §4. Roles, queue rules and limits join it
as they're built (today limits are `play` flags). Code: `src/MetroDom.Core/Policy/PolicyProfile.cs`.

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
