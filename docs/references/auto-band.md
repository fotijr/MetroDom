# Auto band and remembered assignments

ADR 0007 step 4: plug instruments in and play, without naming them. Code:
`src/MetroDom.Conductor/Midi/AutoBand.cs` (planner, file store, hot-plug watcher),
`src/MetroDom.Core/Discovery/Assignments.cs` (the assignment book), `Session` (joining between
songs). Limits: TD-23 in the [tech-debt tracker](../exec-plans/tech-debt-tracker.md).

```bash
metrodom play set1.mid set2.mid                      # band = what's plugged in
metrodom devices --assign "Roland=1-4"               # remember a general device's part
metrodom devices --assign "drums=10@35/36,38"        # or override an instrument's channel
metrodom devices --forget Roland
```

## Who joins

`play` (and `route`) without `-o` asks every output who it is ([identity](midi-identity.md),
[profile](midi-profile-exchange.md), ≤ 2 × 250 ms) and builds the band:

| Device | What happens |
| --- | --- |
| Remembered (assigned) | Plays its assignment. Latency and notes the owner left out come from the profile it sent |
| MetroDom instrument with a profile | Joins on its own, on one channel: its profile's `midi.channel`, else the first free one |
| MetroDom instrument without a profile | Offered (old firmware or a broken profile) |
| General MIDI device, no reply, output only | Offered: `Available: … Give it a part with metrodom devices --assign …` |

- Assignments claim their channels first, so a guessed channel never takes the owner's.
- Auto-added instruments play their channel as written in the song. `--arrange` hands each
  the part it plays best instead.
- Nothing joins on its own → the old prompt (terminal) or "missing --output" (headless).
- `-o` names the band exactly: no discovery, no hot-plug. `--no-identify` also turns discovery off.

## Hot-plug during a set

`play` lists outputs every second. A new port is asked who it is once. Assigned devices and
MetroDom instruments are queued and **join at the next song**, never mid-song, so the
scheduler plans their latency from the first note (lookahead grows, e.g. 10 → 35 ms). Others are
announced as available. A queued device unplugged before its song is dropped. A band member's
unplug/replug is still the reconnecting output's job (panic on reconnect), not re-identified.
Messages go to the live panel, the Control API stream, and stdout when headless. A failed port
listing is reported only when it fails twice in a row (one fails normally mid-unplug).

## The file (`metrodom.assignments/0`)

Default: `$METRODOM_ASSIGNMENTS`, else `metrodom/assignments.json` in the user's app data folder
(`~/Library/Application Support` on macOS, `~/.config` on Linux, `%APPDATA%` on Windows).
`--assignments FILE` on `play`, `route` and `devices` overrides both. A broken file is reported
and treated as empty. Writes replace the file in one step.

```json
{
  "schema": "metrodom.assignments/0",
  "assignments": [
    { "key": "7D:224D:0002 MetroDom drums", "port": "MetroDom drums", "channels": [10], "latencyMs": 35, "notes": [36, 38] },
    { "key": "Synth", "port": "Synth", "channels": [1, 2, 3] }
  ]
}
```

- `key`: manufacturer, family and model from the Identity Reply (hex), then the port name. The
  firmware version is left out, so updating firmware keeps the role. A device that doesn't
  answer is keyed by port name alone; such an entry still matches if it later learns to answer.
- `channels` are 1-16; `latencyMs` and `notes` only when the owner set them.
- `devices --assign` needs the device plugged in (to read its identity). `devices` lists what's
  remembered under the ports.

Measured on macOS with virtual instruments: a three-song set found the xylophone (channel 1 from
its profile); drums started during song 1 joined at song 2 on channel 10 and played every hit at
+35 ms; an assigned xylophone played its `1-3@12`; drums plugged in and out within song 1 never
joined.
