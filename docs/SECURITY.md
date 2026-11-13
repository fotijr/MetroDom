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
  once. Never "anyone on the Wi-Fi". Pairing links carry the token in the URL fragment; the
  owner's remote turns each into a QR code to scan. `serve` listens on the LAN by default (the
  box's own access point) over plain HTTP, and warns: TLS is TD-27.
- **Secrets at rest.** `serve` keeps the owner token in `state.json` (folder `0700`, file `0600`)
  so paired phones survive a restart; handed-out tokens are stored as SHA-256 hashes only. The
  file is never replaced silently; deleting it re-pairs everyone ([Conductor service](references/conductor-service.md)).
- **Say only what's needed on the LAN.** mDNS announces the service, port, API path and policy
  mode, never a token. Clients name library songs by id; only ids from the last scan resolve, and
  the API never shows file paths.
- **Guests are untrusted input.** Validate and rate-limit everything: request floods, vote
  stuffing, oversized payloads, offensive free text (avoid free text where possible). Built
  (`RequestDesk`): guests name library ids only (no free text), one vote per token per song,
  limits on songs waiting and time between requests, 20 tries a minute per token (refused ones
  count), and `429` with `Retry-After`. A guest is a token, so the owner decides how many guests
  a table gets. A room's join QR gives each phone its own guest token; one address may join 3
  times an hour and one code lets in 300. The join code itself only views (it hangs on a public
  screen) ([join QR](references/control-api.md#join-qr-and-big-screen)).
- **No live MIDI from the network for guests.** Live note input is operator-only.
- **Song files are untrusted.** Enforce size, track and event-count limits when parsing SMF.
- **Firmware updates are owner-only** and should be signed once there's a fleet (rental).
- **Body writes are owner-only by hardware.** USB MIDI has no login, so a Body Write
  ([body record](references/body-record.md)) needs someone holding the body's write button
  (wired to the EEPROM's write-protect pin, not read by firmware), and
  the Control API has no route for it. Whatever a body says, the firmware's ceilings (pulse,
  duty) still hold.
- **LAN-only by default.** Remote access goes through the optional cloud relay with its own
  auth, never by port-forwarding the Conductor.
- **Privacy:** don't collect guest personal data for the jukebox. Device-scoped anonymous IDs are
  enough for rate limiting.
- Never commit secrets. Config with secrets lives outside the repo.

## Physical security
Covered as safety in [RELIABILITY.md](RELIABILITY.md): firmware limits mean even a fully
compromised Conductor can't overheat an actuator.
