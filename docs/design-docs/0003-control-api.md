# 0003: One Control API for every app

Status: **Accepted** (2026-11-11, proposed 2026-11-10)

## Context
Three very different products (bar band, jukebox, rental) each need a control app. Writing three
backends, or letting apps talk to instruments, would fork the system and bypass safety.

## Decision
- The Conductor exposes **one Control API** on the local network:
  - **HTTP + JSON** for commands and queries (queue, library, settings, session).
  - **WebSocket** for live state (now playing, position, queue changes, instrument health).
  - **OSC** (optional adapter) for live-performance controllers and tools like TouchOSC.
  - **MIDI in** for keyboards and pads (live playing, tap tempo, transport via MIDI Machine
    Control / Start-Stop).
- The API is described in a machine-readable spec (OpenAPI for HTTP, a JSON schema for events)
  checked into the repo. Clients are generated or validated against it.
- Every request carries a **session with a role** (owner, operator, guest). The active
  [policy profile](0005-deployment-modes-are-policy.md) decides what each role can do.
- Discovery on LAN via **mDNS/DNS-SD** so apps find the band without typing IPs.

## Consequences
- Phone apps, a web admin page and a jukebox kiosk are all thin clients of the same API.
- New products are mostly new policy + new UI, not new backend.
- Safety lives below the API, so no client can bypass it.

## Open questions
- ~~Pairing/auth flow for the rental customer?~~ A QR code carrying the Wi-Fi details and a
  per-booking token that expires when the rental ends (owner, 2026-11-11). The owner's token is
  separate and never printed. See [SECURITY.md](../SECURITY.md). Today's v0 is still one bearer
  token ([control API](../references/control-api.md), TD-27).
- ~~Web app (PWA) first vs native apps?~~ PWA first, served by the Conductor (owner,
  2026-11-11): no app stores, any phone on the LAN, one codebase. Native only if a feature
  needs it.
