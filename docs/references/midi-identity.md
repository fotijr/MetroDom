# MIDI identity (discovery step 2)

Status: **Draft**, pending owner review. Implements step 2 of
[ADR 0007](../design-docs/0007-instrument-discovery.md). Code: `src/MetroDom.Core/Discovery/DeviceIdentity.cs`
(bytes), `src/MetroDom.Conductor/Midi/Identifier.cs` (asking ports).

## Bytes (MIDI 1.0 Universal Non-Real-Time SysEx, General Information)

| Message | Bytes |
| --- | --- |
| Identity Request | `F0 7E <dev> 06 01 F7`. MetroDom sends `<dev>` = `7F` (all devices) |
| Identity Reply | `F0 7E <dev> 06 02 <mfr> <fam LSB> <fam MSB> <model LSB> <model MSB> <v1> <v2> <v3> <v4> F7` |

`<mfr>` is one byte, or three when the first is `00` (e.g. `00 20 29`). Family and model are
14-bit, low 7 bits first. Every data byte is ≤ `7F`.

## MetroDom's identity

| Field | Value | Why |
| --- | --- | --- |
| Manufacturer | `7D` | The MIDI Association's ID for non-commercial / educational use. Anyone may use it, so the family is what marks MetroDom |
| Family | `4D 44` (ASCII "MD", value 8781) | Readable in a SysEx dump. Change only with an ADR |
| Model | `00 00` = software instrument (`metrodom virtual`). `01 00` = a brain running the generic firmware ([metrodom_brain](../../Instruments/metrodom_brain/README.md)); the body it drives is in its profile | |
| Version | 4 bytes, firmware (or Conductor) `major.minor.build.revision` | |

Example, `metrodom virtual` v1.0: `F0 7E 7F 06 02 7D 4D 44 00 00 01 00 00 00 F7`.

## What `metrodom devices --identify` does

Opens every output and the input with the **same name**, sends one request to each (all at
once), and waits up to 250 ms, or less if every port has answered. Then it closes them.

| Result | Meaning |
| --- | --- |
| MetroDom instrument | `7D` + family "MD". Then its profile is fetched ([step 3](midi-profile-exchange.md)) |
| Manufacturer · family · model · version | A general MIDI device that answered. A few manufacturer IDs have names; others show as hex |
| no reply (still usable) | Has an input but didn't answer, as many devices don't |
| output only | No input with the same name, e.g. a software synth |
| couldn't ask | Opening the port failed (busy, unplugged during the scan) |

Loopback ports (CoreMIDI virtual devices, the IAC bus) echo the request back; only a reply
counts. `metrodom virtual` instruments answer as model 0.

## Not yet

- Keys from USB serial or product strings (assignments use identity + port name, [auto-band](auto-band.md)).
- MIDI 2.0 UMP Endpoint Discovery, mDNS network MIDI.
- Model numbers beyond the brain (the profile says which body, so one may be enough).
