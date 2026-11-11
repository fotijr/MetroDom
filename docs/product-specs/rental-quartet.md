# Spec: Rental quartet ("as easy as Spotify")

Status: Draft · Profile: `rental`

## Story
Someone books a four-instrument robotic quartet for a party. It's delivered, plugged into power,
and they control it all night from their phone. No MIDI knowledge, no laptop, no setup call.

## Must have
- Power on → ready in < 2 minutes. Instruments auto-discovered and self-calibrated.
- Pairing: scan a QR code on the box → owner session on the phone (no account needed on-site).
- Spotify-like app: browse library, play/pause/skip, queue, volume, "party mode" (let guests
  request = jukebox rules inside rental).
- Works without the customer's Wi-Fi (box can host its own network) and without internet.
- Hard limits set by the rental company: max volume, quiet hours, rental end time.
- Customer-safe: no setting in the app can damage hardware.

## Nice to have
- Booking, delivery logistics and payment in the cloud.
- Remote health check and support by the rental operator.

## Risks
- Physical safety in a home (pinch points, heat, kids). See [RELIABILITY.md](../RELIABILITY.md).
- Firmware/software updates across a fleet of units.
