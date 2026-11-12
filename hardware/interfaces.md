# Hardware interfaces

The contract between modules, like a type signature. A module's `module.toml` may only list
interface ids from the table below (enforced by `scripts/check_hardware.py`). Decided in
[ADR 0009](../docs/design-docs/0009-shared-hardware-modules.md). Status: **Proposed**.

Never change what an existing id means. A different voltage, current or connector gets a new id.

| Id | Kind | Standard | Pinout / details |
| --- | --- | --- | --- |
| `usb-midi` | data + power | USB (micro-B or USB-C, per brain board) | Class-compliant USB-MIDI. 5 V powers logic only ([ADR 0001](../docs/design-docs/0001-midi-is-the-contract.md)) |
| `qwiic` | data | JST-SH 1.0 mm 4-pin, 3.3 V I²C, 400 kHz | 1 GND · 2 3V3 · 3 SDA · 4 SCL (SparkFun Qwiic / Adafruit STEMMA QT compatible) |
| `rail-12v` | power | 12 V DC. 5.5×2.5 mm barrel up to 5 A, XT30 above 5 A | Center positive. Fused at `power-entry`. Kill switch cuts it |
| `rail-5v` | power | 5 V DC from a buck converter on `power-entry` | For servos and logic that can't run from USB |
| `act-2p` | actuator | JST-XH 2.54 mm 2-pin, ≤ 3 A | 1 +12 V (from rail) · 2 switched low side. Flyback diode lives on the driver |
| `servo-3p` | actuator | 0.1" 3-pin servo header | GND (brown) · V+ (red) · signal (orange). PCA9685 native |
| `mount-2020` | mechanical | 2020 aluminum extrusion, M5 T-nuts | Adapter plates use a 20 mm hole grid, M5 clearance holes (5.5 mm) |
| `case-600` | transport | (Long-term) Road case/crate, 600 × 600 or 600 × 1200 mm footprint, ≤ 1000 mm high | Frame bolts inside. Weight limits and transport locks per [ADR 0010](../docs/design-docs/0010-road-ready-instruments.md) |
| `rack-19` | transport | (Long-term) 19" rack (EIA-310), shallow road rack case | Head case: Conductor, power strip, hub/switch, master kill |
| `rail-5v-led` | power | Separate fused 5 V supply for LEDs only | Sized for LED count × 60 mA, brightness capped in software. Inject every 1–2 m |
| `led-3p` | light | JST-SM 3-pin, addressable LED (WS2812/SK6812) | 5 V · data · GND. Level-shifted data, 330 Ω series resistor at the controller |
| `hub75` | light | 16-pin IDC ribbon, HUB75 LED matrix panel | Panels chain out → in. Power via separate screw terminals on `rail-5v-led` |
| `dmx-xlr` | light | DMX512 on 5-pin XLR (3-pin adapters for budget fixtures) | Daisy chain, 120 Ω terminator at the end, DMX cable only ([primer](../docs/references/lighting-essentials.md)) |
| `ethernet` | data | RJ45, 100 Mbit+, wired | sACN/Art-Net to light nodes. Through a network switch next to the Conductor |
| `mains-iec` | power | IEC C14 panel inlet, fused, feeding a certified enclosed PSU | The only mains connection on a case. No DIY mains wiring |

## Open questions
- Confirm 12 V (vs 24 V) as the default actuator rail (ADR 0009).
- Exact body connector pinout and power rails called out in ADR 0006 resolve here.
