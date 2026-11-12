# 0005: Deployment modes are policy, not forks; local-first

Status: **Accepted** (2026-11-11, proposed 2026-11-10)

## Context
MetroDom may be a bar band run by one person, a crowd-controlled jukebox, or a quartet rented
out for a night. Bars have bad Wi-Fi. Houses have unknown networks. Building separate products
would triple maintenance.

## Decision
- A **policy profile** (versioned config file) defines a deployment mode:
  roles and permissions, queue rules (requests on/off, votes, max requests per guest, cooldowns),
  content filters (allowed library, explicit flag), limits (max volume/velocity, curfew hours),
  and which control surfaces are enabled.
- Shipped profiles: `live-band`, `crowd-jukebox`, `rental`. Owners can tweak, not fork.
- **Local-first:** Conductor + instruments + LAN apps are fully functional with no internet.
  The **Cloud** is optional. It adds catalog sync, bookings, remote request relay, and fleet
  telemetry, and the Conductor queues sync until it's online.

## Consequences
- Every new feature asks "which role, under which profile?" instead of "which app?".
- The song library must be cached on the box. Licensing of songs (MIDI arrangements and the
  public-performance rights for the underlying works) is a product concern for the jukebox and
  rental modes. Track in [product specs](../product-specs/index.md).

## Open questions
- ~~Remote management of rental units without exposing them to the internet?~~ Outbound-only
  check-in (owner, 2026-11-11): when online, a unit polls the optional Cloud, pulls signed
  updates and pushes health telemetry. No inbound ports, no remote shell.
