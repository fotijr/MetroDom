# 0010: Road-ready instruments: cases, transport and fast setup

Status: **Proposed** (2026-11-10)

## Context
Every mode moves hardware. The [live band](../product-specs/live-band.md) loads into a truck
and sets up in a bar, often alone and in a hurry. The [rental quartet](../product-specs/rental-quartet.md)
is delivered to customers who "plug in power, join Wi-Fi". Only the
[jukebox](../product-specs/crowd-jukebox.md) stays put. An instrument that plays well on the bench
but takes an hour to assemble, or comes out of the truck out of adjustment, fails the product.

## Decision

### 1. The case is the stand
Each instrument lives permanently in its own road case or crate. Its `frame-2020` bolts inside
the case. Setup is: wheel it in, take the lid off (or open the front), plug in, play. No
mechanism is assembled at the venue. Small instruments may share a case.

### 2. One footprint grid, sized for doors and vehicles
- Case footprints are **600 × 600 mm** or **600 × 1200 mm** (≈ 23.6" × 23.6" / 47.2").
  600 mm fits through any doorway, and two fit side by side between pickup wheel wells.
- Height ≤ 1000 mm so cases stack two high in a van and roll under a bar counter.
- Instrument frames are designed to the **inside** of these cases, not the other way round.

### 3. Weight limits
| Case weight | Requirement |
| --- | --- |
| ≤ 23 kg (50 lb) | Two handles. One person can lift it into a vehicle |
| 23–45 kg | Lockable casters + two handles. Rolls; two people lift |
| > 45 kg | Avoid. Split the instrument across cases |

### 4. Two connections per case, on a panel
Every case exposes at most **power** and **data** on a recessed, strain-relieved panel. Nothing
outside the case plugs into a board directly.
- Power: `mains-iec` inlet feeding a **certified, enclosed** power supply inside the case.
  No DIY mains wiring, ever. Low-voltage rails are made inside the case.
- Data: USB today (`usb-midi`), network later (see open questions).

### 5. A head case runs the rig
A 19" rack case (`rack-19`, an open standard) holds the Conductor box, a power strip with
breaker, the USB hub or network switch, and a **master kill switch** for every actuator rail.
Instruments cable to the head case, not to each other.

### 6. Survive the drive
- **Transport mode:** solenoid plungers and mallets are restrained (printed locks or foam) so
  nothing hammers in the truck. Setup includes removing them. A missing lock is a checklist item.
- Fasteners that see vibration use nyloc nuts or threadlocker.
- Printed parts that travel are PETG or ASA, never PLA (softens in a hot vehicle). See
  [fabrication](../../hardware/fabrication/README.md).
- Internal connectors are strain relieved so a bump can't pull a lead off a driver.

### 7. Setup is timed and checked
- Target: **one person, truck to first note in ≤ 20 minutes** for a live-band rig. Measure it.
- Each case carries a printed setup card and its serial label (the same serial as its
  [build record](../../hardware/builds/README.md)).
- After power-up, each instrument gets a self-check: firmware test sweep, and the Conductor
  confirms every expected instrument is present. Latency is re-checked when temperature
  differs a lot from calibration.

## Consequences
- Cases add cost and weight to every instrument, and the 600 mm grid constrains instrument
  design (a full xylophone may need 600 × 1200, or two cases).
- New planned modules: `case-road` (the case, panel, handles, casters, transport locks) and
  `rig-head` (the rack case). New interfaces: `case-600`, `rack-19`, `mains-iec`.
- Pneumatic instruments must also carry their air: a compressor in its own case, with noise in
  a bar as a real constraint.

## Alternatives considered
- **Soft bags and a folding stand per instrument:** cheaper and lighter, but every gig means
  re-assembly and re-adjustment, which defeats fast setup.
- **One big rig on a single cart:** fastest setup, but too heavy, won't fit all vehicles, and
  can't be split across rental customers.

## Open questions
- **Cable distance.** Passive USB is limited to ~5 m. If a stage spreads instruments further:
  active USB extenders, a hub per case, DIN MIDI (15 m), or network MIDI (RTP-MIDI / an ESP32
  brain, [ADR 0006](0006-swappable-instrument-brains.md)). Affects the brain choice.
- **Which vehicle?** Its cargo dimensions should confirm or change the 600 mm grid. Record it in
  [equipment.csv](../../hardware/inventory/equipment.csv) once known.
- **Buy cases or build crates?** Plywood crates with aluminium edging are cheaper and good
  beginner projects. Bought road cases are tougher.
- **Venue power.** A whole rig should run from one ordinary outlet circuit. Budget the sum of
  power-supply ratings.
