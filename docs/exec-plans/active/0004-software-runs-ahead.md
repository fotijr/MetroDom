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
- [x] Policy file carries roles, permissions, queue rules and limits; flags become overrides
- [x] Control API sessions with roles: owner token, per-booking guest tokens that expire
- [x] Shipped `crowd-jukebox` and `rental` profiles next to `live-band`
- [x] Web remote shows only what the token's role may use

### Phase S3: Conductor service and PWA (TD-27)
- [x] Long-running service with library and editable queue, mDNS (`metrodom serve`,
      [Conductor service](../../references/conductor-service.md))
- [x] Installable PWA with queue editing and a QR join code (offline install needs HTTPS on the
      LAN: TD-27)

### Phase S4: Arranger (TD-21)
- [x] `profile.json` files, polyphony, drum-note substitution, per-piece drum seats

### Phase S5: Calibration flow (ADR 0004)
- [x] Quick check on boot and full on-demand calibration against simulated onsets
      ([calibration](../../references/calibration.md))

### Phase S6: Crowd jukebox mode (M7)
- [x] Guest requests, votes, cooldowns, rate limits on untrusted input
      ([requests and votes](../../references/control-api.md#requests-and-votes-jukebox))
- [x] One room QR, a guest token per phone, and a big-screen now playing / up next page
      ([join QR and big screen](../../references/control-api.md#join-qr-and-big-screen))

## Decisions log
- 2026-11-12: Safety first, chosen by the owner. The all-off is its own MetroDom SysEx message
  (sub-ID `03`) and goes out **before** the 32 CCs, so a MetroDom instrument is silent after one
  message instead of up to 32. It only goes to ports whose Identity Reply said MetroDom (the
  same rule as the profile request); everyone else gets CCs only. `metrodom panic` and a replug
  still send CCs only: they have no identity to go on, and the brain turns itself off at boot.
- 2026-11-12: `metrodom panic` now sends the all-off too, after the CCs: CCs to every chosen
  port at once (nothing waits), then one identify round, then the all-off to ports that answer as
  MetroDom. `--all` panics every output. The replug panic stays CCs only (boot is all-off; the
  same port name may be another device, TD-17). `simulate` runs notes through the governor, so
  it reports what `play` holds back instead of what the firmware would refuse.
- 2026-11-12: The governor mirrors the firmware's duty bucket exactly (capacity
  `dutyWindowMs × maxDutyPercent`, each strike costs its pulse, drains at `maxDutyPercent`) so
  the Conductor holds back what the brain would refuse, and the status panel can say why. Without
  a per-note `pulseMs`, a strike is assumed to cost `maxOnMs`: safety beats music. Without
  `safety`, no duty limit (a software instrument has no coil).
- 2026-11-12: S2 shape. The policy stays `metrodom.policy/0` and gains optional `limits`,
  `roles`, `queue` and `sessions`; a file without them means what it meant before. Flags can only
  tighten numeric limits (lower wins) so a policy written for a room isn't loosened by accident;
  `--quiet` replaces the window because two windows can't be merged sensibly. The owner isn't a
  configurable role, and three rules hold whatever a file says (operators always view and panic,
  guests only view/request/vote, only the owner hands out tokens), so a typo can't take panic
  away or give it to a crowd. Every route declares one grant and an undeclared route is refused,
  checked against the spec's `x-metrodom-grant` by a test. Handed-out tokens are hashed, in
  memory, and revocation closes their streams; persisting them waits for the S3 service. Queue
  rules are parsed now so the shipped profiles are complete, and enforced when requests exist.
  The enum is `Grant`, not `Permission` (the .NET analyzer reserves that suffix); files and docs
  still say permissions.

- 2026-11-12: S3 shape. `serve` is `play`'s set loop (`PlayCommand.RunSet`) driven by a
  `SetQueue` that never runs out, not a second engine: outputs, safety, policy, pedals and lights
  stay one code path (ADR 0005). Only what's coming can be edited; the song up now is skipped
  with next, never deleted from under the band. Songs are named by their path under the library
  folder (stable across restarts, and only ids from the last scan resolve). A service always
  waits for start before its first song, so a reboot never starts the band, and `queue.autoplay`
  (crowd-jukebox only) applies only once the queue has run dry and someone adds a song. The
  owner token is kept at rest (`0600`) because re-pairing every phone after a power blip is
  worse than a file only the box's user can read; handed-out tokens stay hashes. mDNS is a small
  built-in responder rather than a dependency or a call to the OS (`dns-sd`, Avahi), so it works
  the same on macOS, Linux and Windows; it answers AAAA with NSEC because macOS otherwise waited
  5 s per lookup. QR codes come from QRCoder (MIT), drawn as SVG for the remote and half blocks
  for the terminal. The default operator gains `queue.edit` (a bandmate reorders the set).
  `CA1711` is suppressed for `SetQueue`: it is a queue, and that's the word the API uses.

- 2026-11-12: S4 shape. A profile file goes where the latency goes (`-i NAME@FILE.json`,
  `-o PORT@FILE.json`) rather than a new flag, so one spec still says everything about one
  instrument; it beats what the instrument sends, for kits that can't send one or brains on old
  firmware. Drum stand-ins are a hand-picked General MIDI table (`DrumMap`), best first, and
  count toward which part a kit gets; notes with no stand-in are still dropped. Polyphony is
  enforced in the arranger as well as the firmware and governor, so the table shows what will
  really sound: a note holds an actuator for its pulse (or its length on a software
  instrument), chords keep the top voice then the bass, drums keep kick, snare, hats/ride,
  crashes, toms. Piece seats are `--seat ROLE=PLAYER/PIECES`; several may share a role when
  their notes don't overlap, which is how two people share one kit.

- 2026-11-12: S5 shape. Onsets reach the Conductor as MIDI Note Ons (a trigger module, a
  mic-to-MIDI box, `virtual --onsets`), not raw audio: MIDI stays the contract, and an audio
  detector can come later as one more source. One strike in the air at a time, windows that
  don't overlap and the median per note keep matching simple and robust to noise. The probe is
  a state machine driven by the caller's clock, so the same code runs in tests on a virtual
  clock and on real ports. Measurements live on the Conductor (`calibrations.json`, by port),
  not in the instrument, because they include this rig's mic and cables. Measured beats declared,
  and the command line beats both. The boot check only flags (ADR 0004); applying drift by
  itself is left open below.

- 2026-11-12: S6 shape. Requests live in the one queue, not beside it: what's coming is pinned,
  then requests by votes, then the set, so every client draws one list and the set loop didn't
  change. An operator placing a song pins it ahead of requests or drops it into the set by where
  it lands, which keeps `move` meaning "put it here". The guest rules are one Core class on the
  monotonic clock (`RequestDesk`), with the token as the guest (no accounts, no device
  fingerprinting, SECURITY.md privacy). A request for a song already requested becomes a vote,
  so duplicates never queue. Refused tries count toward the flood limit. Policy gained
  `songCooldownMinutes` and `fill` (the spec's per-song cooldown and auto-fill playlist). Found
  and fixed on the way: a service that booted with an empty queue would autoplay the first song
  added under a jukebox policy. It now waits for start, as RELIABILITY says.
- 2026-11-12: Join QR. A QR on a public screen can't hold a guest token: everyone who scanned it
  would be one guest for the limits. So the owner makes a join code (a guest token marked as a
  door), and each phone trades it for its own guest token. The code itself only works for
  `view`, which is what the big screen needs and nothing a scanner couldn't get by joining.
  Rejoining for fresh limits is held back by address (3 an hour), not by device fingerprinting
  (SECURITY.md privacy). Ending the code ends its guests: closing time is one tap. No new grant
  and no new token type: it rides on `POST /tokens` and the owner's `tokens` grant.

## Open questions
- Should the boot check apply what it measured when an instrument drifted (rental boxes that
  "self-calibrate"), or keep flagging until someone runs `calibrate`? Flagging only for now.
- The shipped jukebox and rental values (limits, quiet hours, request rules, token lives) are
  guesses to try at the first party, not decisions.
- TLS on the LAN, so phones can install the remote as an offline app: a self-signed certificate
  phones must trust, a real one for a domain the box doesn't own, or accept the home-screen
  shortcut. Needs a decision before the rental mode ships.
