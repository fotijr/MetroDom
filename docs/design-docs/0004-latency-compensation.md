# 0004: Latency compensation via per-instrument lookahead

Status: **Accepted** (2026-11-11, proposed 2026-11-10)

## Context
Mechanical actuators are slow and uneven. A solenoid might strike ~10 ms after the command, a
servo that must first rotate to a bar takes tens to hundreds of ms, and latency can vary by note
and velocity. Sending notes "on time" makes a band of robots sound sloppy.

## Decision
- Each instrument's [profile](../references/instrument-profile.md) declares **latency per note**
  (and optionally per velocity), measured by a calibration routine (microphone or piezo onset
  detection).
- For pre-composed material (songs from the queue) the Conductor's scheduler reads ahead and
  sends each event at `t_sound − latency(instrument, note, velocity)`.
- For **live** input (operator on a keyboard) lookahead is impossible. Instead, delay every
  instrument to match the slowest one in the active ensemble, up to a budget
  (see [RELIABILITY.md](../RELIABILITY.md)). Instruments that blow the budget are flagged.
- Tempo changes mid-song re-time the remaining events, so the scheduler works in musical time
  (ticks), converting to wall time as late as possible.

## Consequences
- Scheduler needs a monotonic high-resolution clock and must be tested with a fake clock.
- Instruments with multi-step motion (servo positioning) may declare **minimum inter-note
  intervals**. The arranger must respect or thin out notes that violate them.

- Following a human player's tempo ([ADR 0012](0012-humans-in-the-band.md)) means lookahead
  runs on **predicted** beats from a tempo tracker, not on a known timeline.

## Open questions
- ~~Calibration UX for a rental customer (automatic on boot?)~~ A quick check on boot plays a
  short test, compares against the stored latencies and flags drift. Full per-note calibration
  runs on demand (owner, 2026-11-11).
- ~~Target jitter budget?~~ ≤ 5 ms between instruments for pre-composed material (owner,
  2026-11-11), as in [RELIABILITY.md](../RELIABILITY.md).
