# MIDI profile exchange (discovery step 3)

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

- `<text>` is the profile JSON, ASCII only (non-ASCII is escaped as `\uXXXX`), so every byte is ≤ `7F`.
- At most 128 text bytes per chunk, so any USB-MIDI SysEx buffer copes. `<index>` counts from 0,
  `<count>` is the total (1-127): profiles up to 16 KB.
- Chunks may arrive in any order. A chunk with a different `<count>` starts the answer over.
- Free sub-IDs after `02` are for later MetroDom messages (e.g. a SysEx all-off, TD-22).

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
Measured on macOS: two virtual instruments identified and described, whole command 0.19 s.

## Not yet

- The fetched profile isn't used: `play`/`arrange` still take instruments from `-o`/`-i` flags
  (step 4: auto-add with the profile, remember assignments, TD-23).
- Firmware side (Phase 1: read the profile from the body EEPROM and serve it).
- MIDI-CI Property Exchange.
