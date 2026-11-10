# MIDI essentials for MetroDom

## Channel voice messages (status byte = type | channel 0–15)
| Message | Bytes | MetroDom meaning |
| --- | --- | --- |
| Note On | `9n note vel` | Strike `note` with strength `vel` (1–127). `vel 0` = Note Off |
| Note Off | `8n note vel` | Release (dampers, sustained actuators). Strikers may ignore |
| Control Change | `Bn cc val` | See CC table |
| Program Change | `Cn prog` | Reserved (could select articulation/mallet) |
| Pitch Bend | `En lsb msb` | Reserved |

Note numbers: middle C = 60 (C4 in the common convention). Prototype mappings used 60/64/67
(C/E/G).

## Control changes MetroDom uses
| CC | Name | Use |
| --- | --- | --- |
| 7 | Channel volume | Global velocity scale |
| 64 | Sustain | Instruments with dampers |
| 120 | All Sound Off | **Panic**: de-energize immediately |
| 121 | Reset All Controllers | Reset to defaults |
| 123 | All Notes Off | Release all notes |

## System real-time (tempo from humans)
- MIDI Clock `F8`: 24 pulses per quarter note. Start `FA`, Continue `FB`, Stop `FC`.
- The Conductor can be a clock *follower* (tempo from a drummer's module or a DAW) or
  *leader*.

## Identity (SysEx)
- Identity Request: `F0 7E <dev> 06 01 F7` (`dev` `7F` = all devices).
- Identity Reply: `F0 7E <dev> 06 02 <mfr> <family:2> <model:2> <version:4> F7`.
- MetroDom uses non-commercial manufacturer ID `7D` (`mfr` = single byte `7D`).
  `family` = MetroDom, `model` = instrument type, `version` = firmware version.

## Standard MIDI Files (SMF)
- Format 0 (single track) and 1 (multi-track) are what we load. Time is in ticks (PPQN from the
  header). Tempo meta events (`FF 51`) define the *default* tempo, which humans may override.
- Track → instrument mapping usually goes by **channel**. GM channel 10 is drums by convention.

## Transports
| Transport | Notes |
| --- | --- |
| USB-MIDI (class compliant) | Preferred for instruments. Driverless on macOS, Windows, Linux |
| DIN/TRS MIDI | 31,250 baud, ~1 ms per 3-byte message |
| RTP-MIDI (RFC 6295) | Network MIDI. Built into macOS. Libraries exist for Linux/Windows |
| Network MIDI 2.0 (UDP) | Newer MIDI Association standard. Watch OS/library support |
| BLE MIDI | Wireless, higher and variable latency. Avoid for timing-critical parts |
