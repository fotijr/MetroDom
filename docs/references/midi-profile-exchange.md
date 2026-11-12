# MIDI profile exchange (discovery step 3) and MetroDom SysEx

Status: **Draft**, pending owner review. Implements step 3 of
[ADR 0007](../design-docs/0007-instrument-discovery.md): a MetroDom instrument sends its
[instrument profile](instrument-profile.md) when asked. Code:
`src/MetroDom.Core/Discovery/ProfileExchange.cs` (bytes, reassembly),
`src/MetroDom.Core/InstrumentProfileJson.cs` (the JSON), `src/MetroDom.Conductor/Midi/Identifier.cs` (asking).
MIDI-CI Property Exchange may replace this once MIDI 2.0 support is solid on Linux/ARM.

## Bytes (manufacturer SysEx)

Every MetroDom message starts `F0 7D 4D 44`: the non-commercial manufacturer ID and the "MD"
family bytes from the [Identity Reply](midi-identity.md). Anyone may use `7D`, so the Conductor
sends these **only to ports whose Identity Reply said MetroDom**.

| Message | Bytes |
| --- | --- |
| Profile Request | `F0 7D 4D 44 01 F7` |
| Profile Reply (one chunk) | `F0 7D 4D 44 02 <index> <count> <text…> F7` |
| All Off (panic) | `F0 7D 4D 44 03 F7` |

- `<text>` is the profile JSON, ASCII only (non-ASCII is escaped as `\uXXXX`), so every byte is ≤ `7F`.
- At most 128 text bytes per chunk, so any USB-MIDI SysEx buffer copes. `<index>` counts from 0,
  `<count>` is the total (1-127): profiles up to 16 KB.
- Chunks may arrive in any order. A chunk with a different `<count>` starts the answer over.
- **All Off** turns every actuator off at once, like CC 120 on every channel, in one message. It
  doesn't latch: the next Note On plays. The Conductor's panic sends it first, then CC 120/123,
  so a MetroDom instrument is silent after one message instead of up to 32. Instruments ignore
  MetroDom sub-IDs they don't know.
- Free sub-IDs after `03` are for later MetroDom messages.

## What `metrodom devices --identify` does

After the identity round, it sends one Profile Request to every port that answered as MetroDom
(all at once) and waits up to another 250 ms, or less once every profile has arrived. The table
then shows a second line per MetroDom instrument:

| Line | Meaning |
| --- | --- |
| `profile: xylo · notes 60,64,67 (3) · 9-12 ms · repeat ≥ 60 ms` | Name, playable notes, latency range, fastest repeat |
| `no profile: didn't send its profile` | Older firmware, or the reply got lost |
| `no profile: sent a profile that doesn't parse: …` | Firmware bug: the reason says which field |

`metrodom virtual` instruments answer with the profile built from their `-i` spec.

## What `play` and `route` do

Before opening outputs, every `-o` missing `@LATENCY_MS` or `/NOTES` is identified (only those
ports, one scan). A MetroDom instrument's profile fills in what the command line left out:
`@LATENCY` replaces all its latencies, `/NOTES` replaces its notes, and the minimum interval always
comes from the profile (the command line has no way to set one). Each use is reported, e.g.
`MetroDom drums: using the profile it sent (notes 36,38 (2) · 35 ms).` A MetroDom instrument
that sends no usable profile gets a warning and the command line alone. `--no-identify` skips the
scan; a fully described `-o` never triggers it. Measured: `play --arrange -o "MetroDom xylo=1"
-o "MetroDom drums=10"` against `virtual` arranged onto the instruments' own notes with a 35 ms
lookahead, and they skipped no notes.
Measured on macOS: two virtual instruments identified and described, whole command 0.19 s.

## Not yet

- Profiles are fetched by `devices --identify` and by `play`/`route` at start, not on hot-plug.
  `arrange` and `simulate` take virtual `-i` specs, not ports, so they don't ask.
- No remembered assignments or auto-adding instruments that weren't named with `-o` (step 4, TD-23).
- Firmware reading the profile from the body EEPROM. The [brain firmware](../../Instruments/metrodom_brain/README.md) serves one built from its compiled-in config (host-tested, not yet flashed).
- MIDI-CI Property Exchange.
