# Spec: Crowd-controlled jukebox

Status: Draft, first cut built (plan 0004 S6) · Profile: `crowd-jukebox`

Built: guest join by QR (one room QR, a guest token per phone, at most 3 joins an hour per
address), a big-screen "now playing / up next" page with the join QR, the library to request from, requests ordered by votes, de-duplication
(a repeat request is a vote), per-guest and per-song cooldowns, a flood limit, owner pin, remove,
skip and pause requests, policy velocity cap, quiet hours and curfew (from the policy,
`--curfew`, or set for tonight from the remote), and fill when the queue runs dry
([requests and votes](../references/control-api.md#requests-and-votes-jukebox)). Not yet: venue
filters beyond the library folder, the LED-matrix QR, paid priority (TD-27).

## Story
A venue installs the band. Patrons scan a QR code, browse the venue-approved library, and request
or upvote the next song. The venue owner sets rules and can override anything.

## Must have
- Guest join without accounts (QR → web app). Rate limits per guest/device.
- Library filtered by the venue (allow-lists, explicit flag, max length).
- Queue with requests, votes, de-duplication, cooldown per song.
- Owner overrides: skip, remove, pin, pause requests, volume/velocity cap, curfew.
- Graceful degradation: if guest Wi-Fi dies, the queue keeps playing (an auto-fill playlist).

## Nice to have
- Paid priority requests (needs payments + cloud; out of scope until rental works).
- Big-screen "now playing / up next" view (built: `/screen.html`), plus an LED matrix with the join QR code ([ADR 0011](../design-docs/0011-visuals-are-part-of-the-band.md)).

## Risks
- Untrusted input at scale. See [SECURITY.md](../SECURITY.md).
- Song licensing for public performance.
