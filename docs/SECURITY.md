# Security

MetroDom is physical: a compromised controller doesn't just leak data, it moves hardware in a
room full of people. Threat model scales with deployment mode.

## Roles
| Role | Who | Can |
| --- | --- | --- |
| Owner | Band owner / rental company / venue owner | Everything, including policy, firmware updates, panic |
| Operator | Performer, or the rental customer during their booking | Play, queue, tempo, mute, panic, within the owner's policy |
| Guest | Bar patron, party guest | Browse, request, vote, within rate limits. Never panic/settings/live notes |

The active policy profile ([ADR 0005](design-docs/0005-deployment-modes-are-policy.md))
maps roles to permissions (its `roles`, [policy profile](references/policy-profile.md)). The
Conductor enforces it on every Control API route. Apps only hide buttons. Built: owner token at
start, expiring operator and guest tokens handed out by the owner, a grant per route (fail
closed), and these rules whatever a policy file says: operators always have view and panic,
guests only view, request and vote, only the owner hands out tokens.

## Rules
- **Authenticate every Control API session.** A bearer token, loopback by default ([control API](references/control-api.md)). The owner's token is shown on the box; operator and guest
  tokens are handed out by the owner, end with the booking or night, and can be revoked at
  once. Never "anyone on the Wi-Fi". Pairing links carry the token in the URL fragment; a QR code
  for them comes with the PWA (plan 0004 S3).
- **Guests are untrusted input.** Validate and rate-limit everything: request floods, vote
  stuffing, oversized payloads, offensive free text (avoid free text where possible).
- **No live MIDI from the network for guests.** Live note input is operator-only.
- **Song files are untrusted.** Enforce size, track and event-count limits when parsing SMF.
- **Firmware updates are owner-only** and should be signed once there's a fleet (rental).
- **LAN-only by default.** Remote access goes through the optional cloud relay with its own
  auth, never by port-forwarding the Conductor.
- **Privacy:** don't collect guest personal data for the jukebox. Device-scoped anonymous IDs are
  enough for rate limiting.
- Never commit secrets. Config with secrets lives outside the repo.

## Physical security
Covered as safety in [RELIABILITY.md](RELIABILITY.md): firmware limits mean even a fully
compromised Conductor can't overheat an actuator.
