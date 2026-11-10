# Hardware interfaces

The contract between modules, like a type signature. A module's `module.toml` may only list
interface ids from the table below (enforced by `scripts/check_hardware.py`). Decided in
[ADR 0009](../docs/design-docs/0009-shared-hardware-modules.md). Status: **Proposed**.

Never change what an existing id means. A different voltage, current or connector gets a new id.

| Id | Kind | Standard | Pinout / details |
| --- | --- | --- | --- |
| `usb-midi` | data + power | USB (micro-B or USB-C, per brain board) | Class-compliant USB-MIDI. 5 V powers logic only ([ADR 0001](../docs/design-docs/0001-midi-is-the-contract.md)) |
| `qwiic` | data | JST-SH 1.0 mm 4-pin, 3.3 V I²C, 400 kHz | 1 GND · 2 3V3 · 3 SDA · 4 SCL (SparkFun Qwiic / Adafruit STEMMA QT compatible) |
| `rail-12v` | power | 12 V DC. 5.5×2.5 mm barrel up to 5 A, XT30 above 5 A | Centre positive. Fused at `power-entry`. Kill switch cuts it |
| `rail-5v` | power | 5 V DC from a buck converter on `power-entry` | For servos and logic that can't run from USB |
| `act-2p` | actuator | JST-XH 2.54 mm 2-pin, ≤ 3 A | 1 +12 V (from rail) · 2 switched low side. Flyback diode lives on the driver |
| `servo-3p` | actuator | 0.1" 3-pin servo header | GND (brown) · V+ (red) · signal (orange). PCA9685 native |
| `mount-2020` | mechanical | 2020 aluminium extrusion, M5 T-nuts | Adapter plates use a 20 mm hole grid, M5 clearance holes (5.5 mm) |

## Open questions
- Confirm 12 V (vs 24 V) as the default actuator rail (ADR 0009).
- Exact body connector pinout and power rails called out in ADR 0006 resolve here.
