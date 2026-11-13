# Policy profile

Status: **Draft**. The versioned config file of [ADR 0005](../design-docs/0005-deployment-modes-are-policy.md):
one architecture, many modes. Sections, all optional, each defaulting to the safe, closed choice:

| Section | What | Enforced by |
| --- | --- | --- |
| `controls` | Foot-controller mapping ([ADR 0012](../design-docs/0012-humans-in-the-band.md) §4) | `play --control` |
| `limits` | Velocity cap, notes per second, quiet hours for every instrument | The safety governors in `play` and `route` |
| `roles` | What operators and guests may do over the Control API | The Control API, every route ([SECURITY](../SECURITY.md)) |
| `queue` | Whether a song added to an empty queue starts by itself; the jukebox's rules for guest requests | `autoplay`, `fill` and the request rules enforced by `serve` once guests can request (plan 0004 S6) |
| `sessions` | How long handed-out tokens may last | `POST /tokens` |
| `lights` | What the venue lets the lights do ([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md) §5) | `play --lights`, `lights` |

Shipped in `policies/`: `live-band.json` (also built in, used when `--policy` is not given),
`crowd-jukebox.json` and `rental.json`. Owners copy and tweak one, never fork the code.
`--policy FILE` works on `play` and `route`. Code: `src/MetroDom.Core/Policy/PolicyProfile.cs`, `Access.cs`.

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
| (keyboard only) | Volume −/+ 10% | Volume −/+ 10% | [ ] |

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

## Limits

What every instrument may do, whatever the song or the caller. Applied by the safety governor in
front of each output ([RELIABILITY](../RELIABILITY.md), layer 2), on top of each instrument's own
profile limits.

```json
"limits": {
  "maxVelocity": 100,
  "maxNotesPerSecond": 16,
  "quietHours": { "from": "22:00", "to": "08:00", "maxVelocity": 0 }
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `maxVelocity` | Louder notes are turned down to this (1-127), never dropped | none |
| `maxNotesPerSecond` | Most strikes one instrument may play in any second; extra notes are held back | none |
| `quietHours` | Local `HH:mm` window (may wrap past midnight). `maxVelocity` 0 (the default) means no new notes; 1-127 caps them | none |
| `curfew` | Local `from`/`to` `HH:mm` window (may wrap past midnight) when no songs play: a venue's closing time | none |
| `endsAt` | A rental's end, with its UTC offset (`"2026-11-14T01:00-05:00"`). Nothing plays after it | none |
| `graceMinutes` | How long past the curfew or end a song may run to finish (0-60) | 5 |

**Curfew and end time** stop the *set*, at a song boundary, where quiet hours stop *notes*:
`play` and `serve` never start a song that can't finish by the next curfew or end time plus the
grace (its length at the tempo it starts at), and a song still playing at that moment (slowed
down, vamping) is stopped. `play` then ends the set; `serve` waits ("Curfew until 10:00.") and
needs Start again when it lifts. Past `endsAt` it never lifts. The remote and `GET /state` show
`curfew`. Use both for a venue: the curfew ends the set cleanly, quiet hours make sure nothing
sounds after it, whatever sends notes (`route`, a guest's request).

**Tonight's curfew** is set live from the remote or `POST /curfew {"at": "23:30"}` (grant
`curfew`): the next time the clock reads that, it's one more stop, on top of the policy's, so it
can only end the night sooner. It holds until `DELETE /curfew` (the remote's Lift) or a restart;
the policy's curfew and end time can't be lifted from the API. Times are the Conductor's local
clock; a daylight-saving change on the night isn't handled (TD-25).

**Flags on top:** `--max-velocity` and `--max-rate` can only tighten the policy (the lower
number wins). `--quiet` replaces the policy's window for that run, and `--curfew HH:MM-HH:MM`
(`play`, `serve`) the policy's curfew. `play` and `route` print the
limits in force, e.g. `Limits (policy rental + flags): velocity ≤ 100 · ≤ 8 notes/s per
instrument · quiet 22:00-08:00 silent.`

## Roles

Who may do what over the [Control API](control-api.md). The **owner** (the token the Conductor
shows at start) can always do everything and isn't configurable. Operators and guests get
tokens from the owner (`POST /tokens`), and the policy lists what each may do:

```json
"roles": {
  "operator": ["view", "transport", "tempo", "sections", "panic", "queue.edit", "curfew", "volume"],
  "guest": ["view", "queue.request", "queue.vote"]
}
```

| Grant | Routes |
| --- | --- |
| `view` | `GET /state`, `/queue`, `/library`, `/instruments`, `/stream` |
| `transport` | `POST /transport/start`, `/stop`, `/next` |
| `tempo` | `POST /tempo`, `/tap` |
| `sections` | `POST /sections/vamp`, `/outro` |
| `panic` | `POST /panic` |
| `queue.edit` | `POST /queue`, `POST /queue/{id}/move`, `DELETE /queue/{id}` |
| `queue.request` | `POST /requests` |
| `queue.vote` | `POST`/`DELETE /queue/{id}/vote` |
| `tokens` | `GET`/`POST /tokens`, `DELETE /tokens/{id}`: the owner's alone |
| `curfew` | `POST`/`DELETE /curfew`: set or lift tonight's curfew (it can only end the night sooner) |
| `volume` | `POST /volume`: the band's volume, under the policy's caps (it can only turn the band down from them) |

Rules that hold whatever a file says ([SECURITY](../SECURITY.md)), refused with a reason:
- An operator always has `view` and `panic`.
- A guest may only have `view`, `queue.request`, `queue.vote`: never panic, transport, tempo, volume or settings.
- Nobody but the owner has `tokens`. `roles.owner` can't be set.

Without a `roles` section: operators get `view`, `transport`, `tempo`, `sections`, `panic` (what
every token could do in API v0), `queue.edit` (a bandmate reorders the set), `curfew` (venue
staff can call it a night early) and `volume`, and guests get nothing.

## Queue

```json
"queue": { "requests": true, "votes": true, "maxPerGuest": 2, "cooldownSeconds": 300,
           "songCooldownMinutes": 45, "autoplay": true, "fill": true }
```

- `autoplay` (default false): when the queue has run dry and someone queues a song, the band
  plays it without anyone pressing start (a jukebox). Without it the band waits for start (a
  band, a rental). Never after a restart: `serve` always waits for start before its first song,
  so a box that rebooted doesn't start playing by itself.
- `requests` and `votes` (default false), `maxPerGuest` (1-20, default 1: requests one guest may
  have waiting) and `cooldownSeconds` (0-3600, default 300: between one guest's requests).
  `songCooldownMinutes` (0-1440, default 60): how long before a song that played can be asked
  for again. Enforced by `serve` ([control API](control-api.md#requests-and-votes-jukebox)).
- `fill` (default false): when the queue runs dry, `serve` queues a random library song that has
  rested past `songCooldownMinutes` (else the one that played longest ago), so the room never
  goes quiet even if guest Wi-Fi dies. Fill songs follow `autoplay` like any added song.

## Sessions

```json
"sessions": { "maxTokenHours": 72 }
```

The longest an operator or guest token may last (more than 0, at most a week; default 24). A
token request without `hours` gets this. A rental's should cover the booking; a jukebox's the night.

## Shipped profiles

| | `live-band` | `crowd-jukebox` | `rental` |
| --- | --- | --- | --- |
| Pedals | All six | Start/stop, next, panic | None |
| Limits | None | 16 notes/s, silent 01:00-10:00 | Velocity 100, 16 notes/s, silent 22:00-08:00 |
| Operator | Default | Default | Default |
| Guest | Nothing | View, request, vote | View, request |
| Requests | Closed | 2 each, 5 min apart, votes, a song again after 45 min | 1 each, 10 min apart, a song again after 60 min |
| Fill | No | Yes | No |
| Autoplay | No | Yes | No |
| Tokens last | 24 h | 12 h | 72 h |
| Lights | Defaults | Defaults | 80%, 30% from 22:00 |

The values are starting points to try, not measured choices.

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
