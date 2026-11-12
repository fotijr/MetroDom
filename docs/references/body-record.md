# Body record (the body's ID EEPROM)

Status: **Accepted** by the owner on 2026-11-12 (binary record, hold-to-write button, `/OE` on
the brain). Implements [ADR 0006](../design-docs/0006-swappable-instrument-brains.md) §3:
a body describes itself, so any brain plugged into it becomes that instrument.
Code: firmware `Instruments/metrodom_brain/src/brain/Body.{h,cpp}`, Conductor
`src/MetroDom.Core/Bodies/`, `metrodom body`. Plan: [0005](../exec-plans/active/0005-brain-for-m1.md).

## What a brain does at boot

| On the Qwiic bus | The brain becomes |
| --- | --- |
| A 24LC32 at `0x50` with a valid record | That body: its notes, outputs, driver, limits and name |
| Nothing at `0x50` | The compiled-in bench body (`config.h`, the M0 rig) |
| An EEPROM with a blank or bad record | A **silent brain**: no notes, and a profile named `MetroDom brain (body: bad CRC)` (or whichever reason), so `devices --identify` shows what's wrong and `body write` can fix it |

A bad record never falls back to the bench body: that would pulse GPIO pins wired to something else.
The USB product name is the body's name, so a DAW lists "MetroDom xylophone", not "Pico".

## Body file (`metrodom.body/0`)

What the owner writes and keeps next to the instrument. [bodies/m1-xylophone.json](../../Instruments/metrodom_brain/bodies/m1-xylophone.json):

```json
{
  "schema": "metrodom.body/0",
  "id": "xylophone-m1",
  "name": "MetroDom xylophone",
  "driver": { "type": "pca9685", "address": "0x40" },
  "polyphony": 2,
  "safety": { "maxOnMs": 45, "maxDutyPercent": 30, "dutyWindowMs": 1000, "watchdogMs": 500 },
  "notes": [
    { "note": 72, "output": 0, "pulseMs": 30, "minPulseMs": 12, "minIntervalMs": 60 }
  ]
}
```

| Field | Rule |
| --- | --- |
| `id` | 1-32 of `a-z 0-9 -` |
| `name` | 1-48 printable ASCII. Also the USB product name |
| `driver.type` | `gpio` (Pico pins 0-28) or `pca9685` (channels 0-15, full on/off) |
| `driver.address` | PCA9685 only: `0x40`-`0x7F`, a number or a `"0x.."` string. Default `0x40` |
| `midi.channel` | Optional, 1-16. Omitted: any channel (one port per instrument) |
| `polyphony` | 1-16, default 1 |
| `safety.maxOnMs` | Required, 1-250 |
| `safety.maxDutyPercent` | Required, 1-50 |
| `safety.dutyWindowMs` | 100-10000, default 1000 |
| `safety.watchdogMs` | 200-8000, default 500. A body write has to finish inside it |
| `notes[]` | 1-16. `note` 0-127 and `output` each used once |
| `pulseMs` | Strike length at velocity 127: 1 to `maxOnMs` |
| `minPulseMs` | Optional: strike length at velocity 1, at most `pulseMs`. Between them the pulse scales with velocity. Omitted or equal: every strike is `pulseMs` |
| `minIntervalMs` | Required: at least `pulseMs`, at most 10000 |
| `latencyMs` | Optional, 0-1000. 0 = not measured |

**Ceilings.** The firmware refuses any body over 250 ms per pulse or 50% duty, and clamps a
compiled-in config to them too. Raising them means changing the firmware, on purpose.

## Bytes (version 1)

Little-endian, at EEPROM address 0. A 24LC32 holds 4 KB; the largest record is 262 bytes.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 3 | `MDB` |
| 3 | 1 | version `1` |
| 4 | 2 | record length, including the CRC |
| 6 | 1 | driver: 0 GPIO, 1 PCA9685 |
| 7 | 1 | PCA9685 address (0 for GPIO) |
| 8 | 1 | channel 0-15, `FF` = omni |
| 9 | 1 | polyphony |
| 10 | 1 | maxDutyPercent |
| 11 | 1 | note count |
| 12 | 2 | maxOnMs |
| 14 | 2 | dutyWindowMs |
| 16 | 2 | watchdogMs |
| 18 | 1 | id length |
| 19 | 1 | name length |
| 20 | 10 per note | note, output, pulseMs (2), minPulseMs (2), minIntervalMs (2), latencyMs (2) |
| … | | id, then name (ASCII, no terminator) |
| end − 2 | 2 | CRC-16/CCITT-FALSE (poly `1021`, init `FFFF`) of everything before it |

Binary, not the profile JSON stored verbatim: the brain enforces this table, and builds the
profile it sends from it, so what an instrument says and what it does can't drift apart; and
parsing JSON on the brain would be the riskiest code in the firmware. Both sides check
[bodies/m1-xylophone.hex](../../Instruments/metrodom_brain/bodies/m1-xylophone.hex): the C++ test
builds the M1 body and compares, the .NET test encodes the JSON file and compares.

## Writing a body

```bash
metrodom body check Instruments/metrodom_brain/bodies/m1-xylophone.json
```

```bash
metrodom body write "MetroDom brain" Instruments/metrodom_brain/bodies/m1-xylophone.json
```

`body check` validates with the firmware's rules and shows the table (`--hex OUT` writes the
bytes). `body write` checks the file, identifies the port (only MetroDom instruments get it),
asks you to hold the body's write button (in a terminal: hold it, then press Enter), sends the record as Body Write SysEx ([profile exchange](midi-profile-exchange.md)), and waits
for the reply. The brain turns everything off, writes the EEPROM page by page, reads it back,
answers, and restarts; the command waits up to 5 s for it to answer as the new body.

| Reply | Meaning |
| --- | --- |
| written | Stored and verified; restarting |
| bad message | A chunk out of order, not hex, or too long |
| refused: *reason* | The record failed the brain's checks (same reasons as above) |
| didn't keep it | The write button wasn't held, the EEPROM is missing, or the read-back differed. The old body keeps playing |
| can't store | This brain has no body store |
| no answer | Firmware older than 0.2 |

**Write protection is a button you hold.** USB MIDI has no login, so anything plugged into the
brain could send a Body Write. The body's EEPROM `WP` pin is pulled high (protected), and a
momentary button on the outside of the case pulls it to ground while it's held. Hold it for the
second the write takes; let go and the body is protected again, so it can't be left writable by
mistake. It's wired to the EEPROM, not read by the brain, so no firmware bug can bypass it. See
[body-backplane](../../hardware/modules/body-backplane/README.md).
`metrodom virtual` instruments accept Body Write too and then describe themselves as that body
(they still play as their `-i` spec), so the command can be tried with no hardware.

## Not yet

- Never run on a real Pico, EEPROM or PCA9685 (TD-29). The I²C parts are tested against fakes.
- No MCP23017 driver; no servo (PWM) outputs.
- `metrodom calibrate` doesn't write measured latencies into the body yet.
- No Body Read: `body write` checks what the brain reports after the restart, not the raw bytes.
