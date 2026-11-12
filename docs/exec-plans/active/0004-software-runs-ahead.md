# 0004: Software runs ahead (while hardware waits)

Status: Active · Last updated: 2026-11-12

## Goal
Every piece of Conductor work that needs no hardware is built and tested against the virtual band
before the first solenoid is powered, in the order the owner chose on 2026-11-12. Measured by:
each phase closes its tech-debt rows and raises its row in [QUALITY_SCORE.md](../../QUALITY_SCORE.md).

## Context
- [Roadmap](../../ROADMAP.md) rule 3: software runs ahead.
- Decisions this builds on: ADRs [0001](../../design-docs/0001-midi-is-the-contract.md),
  [0003](../../design-docs/0003-control-api.md), [0004](../../design-docs/0004-latency-compensation.md),
  [0005](../../design-docs/0005-deployment-modes-are-policy.md),
  [0012](../../design-docs/0012-humans-in-the-band.md) (accepted 2026-11-11).
- Safety rules: [RELIABILITY.md](../../RELIABILITY.md).

## Phases

### Phase S1: Safety before M0 (TD-22, part of TD-29)
- [x] MetroDom SysEx all-off `F0 7D 4D 44 03 F7` specified ([profile exchange](../../references/midi-profile-exchange.md))
- [x] Brain firmware honors it (host-tested)
- [x] Conductor panic sends it first to every output that answered as MetroDom, then CC 120/123
- [x] Profile gains `pulseMs` per note and `safety.dutyWindowMs`; the brain sends them
- [x] Governor reads `polyphony`, `safety.maxOnMs`, `safety.maxDutyPercent`, `safety.dutyWindowMs`
      and holds back notes the firmware would refuse (duty-cycle and polyphony)

### Phase S2: Policy profiles and roles (ADR 0005, TD-25, part of TD-27)
- [ ] Policy file carries roles, permissions, queue rules and limits; flags become overrides
- [ ] Control API sessions with roles: owner token, per-booking guest tokens that expire

### Phase S3: Conductor service and PWA (TD-27)
- [ ] Long-running service with library and editable queue, mDNS
- [ ] Installable PWA with queue editing and a QR join code

### Phase S4: Arranger (TD-21)
- [ ] `profile.json` files, polyphony, drum-note substitution, per-piece drum seats

### Phase S5: Calibration flow (ADR 0004)
- [ ] Quick check on boot and full on-demand calibration against simulated onsets

### Phase S6: Crowd jukebox mode (M7)
- [ ] Guest requests, votes, cooldowns, rate limits on untrusted input

## Decisions log
- 2026-11-12: Safety first, chosen by the owner. The all-off is its own MetroDom SysEx message
  (sub-ID `03`) and goes out **before** the 32 CCs, so a MetroDom instrument is silent after one
  message instead of up to 32. It only goes to ports whose Identity Reply said MetroDom (the
  same rule as the profile request); everyone else gets CCs only. `metrodom panic` and a replug
  still send CCs only: they have no identity to go on, and the brain turns itself off at boot.
- 2026-11-12: The governor mirrors the firmware's duty bucket exactly (capacity
  `dutyWindowMs × maxDutyPercent`, each strike costs its pulse, drains at `maxDutyPercent`) so
  the Conductor holds back what the brain would refuse, and the status panel can say why. Without
  a per-note `pulseMs`, a strike is assumed to cost `maxOnMs`: safety beats music. Without
  `safety`, no duty limit (a software instrument has no coil).

## Open questions
- None yet.
