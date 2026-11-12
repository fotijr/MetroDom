// The body this brain drives, compiled in until bodies carry their own EEPROM (ADR 0006 §3).
//
// M0 bench rig (docs/ROADMAP.md): one solenoid on a logic-level MOSFET, gate on GPIO 15,
// flyback diode across the coil, current-limited bench supply. Note 60 (middle C) strikes it.
// Edit the table for your wiring; the profile the brain sends is built from these values.
#pragma once

#include "src/brain/Brain.h"

namespace config {

// note, GPIO, pulse ms (20-45 for a small solenoid), min interval ms, measured latency ms (0 = not yet)
constexpr metrodom::Actuator kActuators[] = {
    {60, 15, 30, 60, 0},
};

inline metrodom::BrainConfig make() {
  metrodom::BrainConfig c;
  c.id = "bench-solenoid";
  c.name = "Bench solenoid";
  c.actuators = kActuators;
  c.actuatorCount = sizeof kActuators / sizeof kActuators[0];
  c.polyphony = 1;
  c.maxOnMs = 45;
  c.maxDutyPercent = 30;
  c.watchdogMs = 500;
  return c;
}

}  // namespace config
