# MetroDom brain (generic firmware)

One firmware for every instrument's brain ([ADR 0006](../../docs/design-docs/0006-swappable-instrument-brains.md)).
A Pico running it is a class-compliant USB-MIDI device: a stock DAW or keyboard plays it with
no MetroDom software, and `metrodom devices --identify` sees a **MetroDom brain** and reads its
profile.

Status: **version 0.1, host-tested, not yet flashed.** It stays 0.1 until a brain runs on real
hardware. The logic runs in `make -C test` on a
laptop. The sketch has only been compiled against stub headers, because this machine has no
Pico toolchain yet; the first real build is plan 0002 H1 ("Pico blinks an LED"). Limits: TD-29
in the [tech-debt tracker](../../docs/exec-plans/tech-debt-tracker.md).

At boot the brain reads the body's ID EEPROM and becomes that instrument
([body record](../../docs/references/body-record.md)). No EEPROM: the bench body in `config.h`.
An EEPROM with a bad record: a silent brain whose profile name says why.

## What it does

| MIDI in | What happens |
| --- | --- |
| Note On (velocity > 0), a note in the table, any channel | A pulse on that output: `pulseMs`, or scaled by velocity between `minPulseMs` and `pulseMs` when the body gives both; never more than `maxOnMs` |
| Note Off, Note On velocity 0 | Nothing (the pulse length is decided at the Note On) |
| CC 120 / CC 123 on **any** channel, System Reset, MetroDom All Off `F0 7D 4D 44 03 F7` | Every output off now (panic) |
| Identity Request `F0 7E 7F 06 01 F7` | `F0 7E 7F 06 02 7D 4D 44 01 00 <version> F7`: MetroDom, model 1 = brain ([midi-identity](../../docs/references/midi-identity.md)) |
| Profile Request `F0 7D 4D 44 01 F7` | Its [instrument profile](../../docs/references/instrument-profile.md) in 128-byte chunks ([profile exchange](../../docs/references/midi-profile-exchange.md)) |
| Body Write `F0 7D 4D 44 04 …` | Checks the record, turns everything off, writes the EEPROM, reads it back, answers `05`, restarts as that body |
| Body Read `F0 7D 4D 44 06 F7` | The record it booted from, as `07` chunks; "none" for the bench body or a silent brain |

A body with accent LEDs ([led-accent](../../hardware/modules/led-accent/README.md), WS2812B on
GPIO 7) gets each strike's LEDs flashed for `flashMs`, in the note's color scaled by velocity and
the body's brightness (never over 25%). Refused notes don't flash; every LED goes off with the
actuators. See [body record](../../docs/references/body-record.md#accent-leds).

A note is **refused** (never queued) when:
- its actuator is still striking, or struck less than `minIntervalMs` ago;
- the actuator's thermal budget is spent: a leaky bucket holding `maxDutyPercent` of a 1 s window
  (30% = 300 ms of on-time), draining at `maxDutyPercent`. A burst is fine, sustained hammering
  is thinned to the duty cycle;
- `polyphony` actuators are already on;
- an output didn't confirm it turned off (a PCA9685 that stopped answering): nothing strikes until
  it does, and the off is retried every loop;
- a new body has just been written (the brain is about to restart);
- USB isn't connected.

Ceilings no body can raise: a pulse over 250 ms or a duty over 50%. Raising them is a firmware change.

Always off: at boot (before USB starts), on panic, when USB is unplugged, and when the loop
hangs: the hardware watchdog (`watchdogMs`) restarts the brain, and boot turns everything off.

The profile the brain sends is **built from the same table it enforces** (including each
note's `pulseMs` and the `dutyWindowMs`), so they can't disagree, and the Conductor's governor
holds back what the brain would refuse. The bench profile is committed as [bench-profile.json](bench-profile.json); the host
test checks the firmware generates exactly that, and a .NET test parses it with the
Conductor's reader.

## Files

| File | What |
| --- | --- |
| `config.h` | The bench body, used when no EEPROM answers: notes, GPIO pins, pulse, limits |
| `bodies/` | The M1 test fixtures (with and without accent LEDs) both test suites check. Real instruments keep theirs in `Instruments/<id>/body.json` |
| `metrodom_brain.ino` | Arduino layer only: TinyUSB MIDI, Wire, GPIO, watchdog, USB mount state, choosing the body |
| `src/brain/Brain.{h,cpp}` | Every decision, platform-free |
| `src/brain/Body.{h,cpp}` | The body record (versions 1 and 2): parse, check, encode, CRC |
| `src/brain/Devices.{h,cpp}` | PCA9685 and 24LC32 over an I²C interface (Wire on the Pico, a fake in tests) |
| `test/` | Host tests (`make -C test`, any C++17 compiler; runs in CI) |
| `test/vectors/` | [Parity vectors](test/vectors/README.md): timed MIDI in, expected strikes out. The host test and the Conductor's `BrainModel` (behind `virtual --body`, `simulate --body`) both run them. Add one when you change what the brain decides |

## M0 bench wiring (default `config.h`)

One solenoid, note 60 (middle C), 30 ms pulse, 60 ms minimum interval, 30% duty, 45 ms max.

```
Pico GPIO 15 ──[ 220 Ω ]──┬── gate   logic-level N-MOSFET (e.g. IRLZ44N)
                          └─[ 10 kΩ ]── GND   (pull-down: gate low while the Pico boots)
bench supply + ──┬── solenoid ──┬── drain
                 └── diode ─────┘   flyback diode, cathode (stripe) to +
bench supply − ─── source ─── Pico GND   (common ground)
```

Current-limit the bench supply. Never power the coil from the Pico.

## Build and flash

1. Arduino IDE: add the board URL
   `https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json`,
   install **Raspberry Pi Pico/RP2040/RP2350**, the **MIDI Library** (FortySevenEffects) and **Adafruit NeoPixel** (accent LEDs).
2. Tools > Board: your Pico. Tools > USB Stack: **Adafruit TinyUSB**.
3. Open `metrodom_brain.ino`, Upload (hold BOOTSEL while plugging in the first time).
4. Check: `metrodom devices --identify` lists "Bench solenoid" as a MetroDom brain with its
   profile; a DAW on any channel plays note 60.

## A real body (M1)

Wire the [body-backplane](../../hardware/modules/body-backplane/README.md) (EEPROM at `0x50`,
PCA9685 at `0x40` with `/OE` on GPIO 6, Qwiic on GPIO 4/5), then run this and hold the body's
write button when it asks:

```bash
metrodom body write "MetroDom brain" Instruments/xylophone_solenoid/body.json
```

The brain restarts as "MetroDom xylophone".

## Not yet (TD-29)

Never flashed or run against a real EEPROM, PCA9685 or LED strip. No MCP23017 driver or servo outputs.
No silence timeout (USB unmount
is the only host-loss signal).
