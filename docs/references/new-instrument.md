# Building a new instrument

Status: **Partly built** ([plan 0006](../exec-plans/active/0006-new-instrument-dev-loop.md)).
Steps 1–3 and 5 work today. Step 4's brain-accurate virtual port (`virtual --body`), step 5's
one-command flash and step 6's `metrodom bench` are still planned. Until they're built, each of
those steps says what to do instead.

A MetroDom instrument is a **body**: actuators on a real instrument, a driver board, and an ID
EEPROM that says what it is. Every instrument runs the same **brain** firmware, and the brain
becomes whatever the body says it is ([ADR 0006](../design-docs/0006-swappable-instrument-brains.md)).
So a new instrument needs no firmware change, just two files and the hardware.

```mermaid
flowchart LR
  new[1 instrument new] --> body[2 tune body.json] --> parts[3 parts and modules]
  body --> hear[4 hear it virtually]
  parts --> flash[5 flash and write the body] --> bench[6 bench and calibrate] --> records[7 records]
```

## 1. Start it

```bash
metrodom instrument new snare --notes 38 --plays drum-kit
```

This creates `Instruments/snare/` with three files:

| File | What | Checked by |
| --- | --- | --- |
| `instrument.toml` | Which shared modules it's built from, and how many | `python3 scripts/check_hardware.py` |
| `body.json` | Notes → outputs, pulse lengths, limits: what the brain enforces | `metrodom body check` |
| `README.md` | Wiring, power, bench results: fill it in as you build | `python3 scripts/check_docs.py` |

| Option | Default | |
| --- | --- | --- |
| `--notes` | `60` | One actuator per note: `C5-E5`, `72,74,76`, `38`. At most 16 |
| `--driver` | `pca9685` | The [body-backplane](../../hardware/modules/body-backplane/README.md). `gpio` drives brain pins directly (a bench rig) |
| `--actuator` | `striker-solenoid` | Any strike, press, pluck or damp module in [hardware/modules](../../hardware/README.md) |
| `--plays` | none | The real instrument, from `hardware/inventory/musical-instruments.csv` |
| `--name` | `MetroDom <id>` | The USB name a DAW and `metrodom devices` show |

You can also copy [Instruments/_template/](../../Instruments/_template/README.md) by hand.

## 2. Tune the body

The new body starts with safe values for a small solenoid: a 30 ms strike at full velocity,
12 ms at the softest, 60 ms between repeats, 30% duty, and polyphony 2. Edit `body.json` for
your actuators. The [body record](body-record.md) lists every field and its limits. Then check it:

```bash
metrodom body check Instruments/snare/body.json   # one body, with its table
metrodom body check                               # every Instruments/*/body.json
```

`body check` uses the firmware's own rules, so a body that passes will be accepted by a brain.
No body can ask for a pulse over 250 ms or a duty over 50%. Raising those limits takes a firmware change.

## 3. Parts and modules

```bash
python3 scripts/check_hardware.py              # instrument.toml names real modules
python3 scripts/check_hardware.py --buildable  # what you can build from stock, what's short
```

Each module's README has its wiring, assembly and bench test. A module that doesn't exist yet
starts from `hardware/modules/_template/` ([hardware/README.md](../../hardware/README.md#everyday-workflows)).

## 4. Hear it before soldering

```bash
metrodom virtual -i snare@0/38                 # terminal 1: a "MetroDom snare" port
metrodom play song.mid --arrange               # terminal 2: the band, snare included
```

The virtual port plays only the notes you list, so `arrange` and `play` treat it like the real
instrument ([virtual band](virtual-band.md)). It also takes a `body write`, after which it
describes itself as your body. It doesn't yet refuse what the brain would refuse (repeats closer
than `minIntervalMs`, the duty limit, polyphony); `virtual --body` will (plan 0006 N3).

## 5. Flash the brain and write the body

Flash the one brain firmware ([build and flash](../../Instruments/metrodom_brain/README.md#build-and-flash),
Arduino IDE for now; plan 0006 N5 makes it one command). Wire the brain to the body, then:

```bash
metrodom devices --identify                    # a "MetroDom brain" (no body yet)
metrodom body write "MetroDom brain" Instruments/snare/body.json
```

Hold the body's write button when asked. The brain checks the record, stores it, reads it back
and restarts as "MetroDom snare". From then on, any brain plugged into this body becomes the snare.

## 6. Bench and calibrate

Until `metrodom bench` exists (plan 0006 N6), do it by hand:

1. Play each note slowly from a DAW or `metrodom route`, soft and then hard. Every note sounds,
   and nothing stays on.
2. Unplug USB mid-note. Everything goes off.
3. Play one note at its fastest repeat for a minute, then feel the coil. Write down how warm it is.
4. Measure latency with an onset input (a [trigger-piezo](../../hardware/modules/trigger-piezo/module.toml)
   or a mic-to-MIDI box), then store it in the body so every Conductor schedules it right:

```bash
metrodom calibrate -o "MetroDom snare" --onsets "trigger" --onset-latency 2
metrodom body write "MetroDom snare" --calibrated
```

No onset input yet? Skip step 4. The latency stays "not measured", and a phone recording of
the strikes is enough for a first number ([calibration](calibration.md)).

## 7. Records

In the same commit:
- Fill in the instrument's README: wiring, power, the bench table.
- Write a build record `hardware/builds/MD-SNARE-001.md` ([template](../../hardware/builds/README.md)),
  and take the parts you used off `hardware/inventory/stock.csv`.
- Change `status` in `instrument.toml` from `planned` to `prototype`.
- Run `python3 scripts/check_docs.py`, `python3 scripts/check_hardware.py` and `metrodom body check`.
