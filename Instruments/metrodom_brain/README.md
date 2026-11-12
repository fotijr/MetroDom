# MetroDom brain (generic firmware)

One firmware for every instrument's brain ([ADR 0006](../../docs/design-docs/0006-swappable-instrument-brains.md)).
A Pico running it is a class-compliant USB-MIDI device: a stock DAW or keyboard plays it with
no MetroDom software, and `metrodom devices --identify` sees a **MetroDom brain** and reads its
profile.

Status: **first cut, host-tested, not yet flashed.** The logic runs in `make -C test` on a
laptop. The sketch has only been compiled against stub headers, because this machine has no
Pico toolchain yet; the first real build is plan 0002 H1 ("Pico blinks an LED"). Limits: TD-29
in the [tech-debt tracker](../../docs/exec-plans/tech-debt-tracker.md).

## What it does

| MIDI in | What happens |
| --- | --- |
| Note On (velocity > 0), a note in the table, any channel | A fixed pulse on that output: `pulseMs`, never more than `maxOnMs` |
| Note Off, Note On velocity 0 | Nothing (the pulse is already fixed) |
| CC 120 / CC 123 on **any** channel, System Reset, MetroDom All Off `F0 7D 4D 44 03 F7` | Every output off now (panic) |
| Identity Request `F0 7E 7F 06 01 F7` | `F0 7E 7F 06 02 7D 4D 44 01 00 <version> F7`: MetroDom, model 1 = brain ([midi-identity](../../docs/references/midi-identity.md)) |
| Profile Request `F0 7D 4D 44 01 F7` | Its [instrument profile](../../docs/references/instrument-profile.md) in 128-byte chunks ([profile exchange](../../docs/references/midi-profile-exchange.md)) |

A note is **refused** (never queued) when:
- its actuator is still striking, or struck less than `minIntervalMs` ago;
- the actuator's thermal budget is spent: a leaky bucket holding `maxDutyPercent` of a 1 s window
  (30% = 300 ms of on-time), draining at `maxDutyPercent`. A burst is fine, sustained hammering
  is thinned to the duty cycle;
- `polyphony` actuators are already on;
- USB isn't connected.

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
| `config.h` | The body: notes, GPIO pins, pulse, limits. Edit this for your wiring |
| `metrodom_brain.ino` | Arduino layer only: TinyUSB MIDI, GPIO, watchdog, USB mount state |
| `src/brain/Brain.{h,cpp}` | Every decision, platform-free |
| `test/` | Host tests (`make -C test`, any C++17 compiler; runs in CI) |

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
   install **Raspberry Pi Pico/RP2040/RP2350**, and the **MIDI Library** (FortySevenEffects).
2. Tools > Board: your Pico. Tools > USB Stack: **Adafruit TinyUSB**.
3. Open `metrodom_brain.ino`, Upload (hold BOOTSEL while plugging in the first time).
4. Check: `metrodom devices --identify` lists "Bench solenoid" as a MetroDom brain with its
   profile; a DAW on any channel plays note 60.

## Not yet (TD-29)

Body EEPROM (the config is compiled in), PCA9685 / MCP23017 drivers, velocity → pulse length,
a USB product string per body, rewriting the profile over SysEx,
measured latency in the table.
