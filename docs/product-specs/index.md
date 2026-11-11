# Product specs

One spec per deployment scenario. They share one architecture
([ADR 0005](../design-docs/0005-deployment-modes-are-policy.md)). Specs describe *what* and
*for whom*, and reference the policy profile that implements them.

| Spec | Policy profile | Status |
| --- | --- | --- |
| [live-band.md](live-band.md): one operator plays a live set | `live-band` | Draft. First target |
| [crowd-jukebox.md](crowd-jukebox.md): patrons request and vote | `crowd-jukebox` | Draft |
| [rental-quartet.md](rental-quartet.md): delivered band, Spotify-easy | `rental` | Draft |

**Build order:** live band first. It exercises instruments, scheduling and safety with the
fewest users. Jukebox adds the queue, roles and untrusted input. Rental adds zero-setup,
pairing and remote fleet management.

Shared open product question: **song licensing**. MIDI arrangements and public-performance
rights matter once strangers pick songs (jukebox) or someone pays (rental).
