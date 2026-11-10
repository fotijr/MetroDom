# Spec: Crowd-controlled jukebox

Status: Draft · Profile: `crowd-jukebox`

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
- Big-screen "now playing / up next" view.

## Risks
- Untrusted input at scale. See [SECURITY.md](../SECURITY.md).
- Song licensing for public performance.
