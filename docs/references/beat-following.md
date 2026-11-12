# Beat following (human leads)

How the band follows a human player's beat ([ADR 0012](../design-docs/0012-humans-in-the-band.md)
§2, roadmap M5). Code: `src/MetroDom.Core/Timing/BeatTracker.cs`, `BeatFollower.cs`, `HitLog.cs`,
`src/MetroDom.Core/Simulation/FollowSimulation.cs`. Status and limits:
[QUALITY_SCORE](../QUALITY_SCORE.md), TD-26 in the [tech-debt tracker](../exec-plans/tech-debt-tracker.md).

## Pieces

| Piece | Does |
| --- | --- |
| `BeatTracker` | Hits in, predicted beats out. States: **listening** → **locked** → **holding** (lost the player) |
| `BeatFollower` | Steers the `LatencyScheduler` so the song's beats are *heard* on the predicted beats |
| `SongBeats` | The song's beat grid in song time (time signature denominator, follows tempo/meter changes, count-in) |
| `HitLog` | Hits as text, for recordings and repeatable tests |
| `FollowSimulation` | The band (a click per beat) following hits on a virtual clock; reports band − player per beat |

## Tracker

- **Lock:** 4 hits whose intervals are all within 20% of their median. If the song tempo is
  known, the pulse is whichever of ½×, 1×, 2× the median is nearest it (in log tempo), inside
  40-240 BPM. So eighths on a hi-hat at a 120 BPM song follow 120, not 240.
- **Locked:** a hit within 0.2 beat of a predicted beat counts. It moves the phase 50% of the
  error and the tempo 20% of the error, capped at **4% per beat**. Hits near the off-beat (±0.1)
  and second hits on a counted beat change nothing. Other hits (fills, flams) go to the lock
  candidates; 4 steady ones on a new phase relock.
- **Holding:** no counted hit for 2.5 beats (+ tolerance). Tempo and grid carry on; the next hit
  on the grid is counted at once, so a drummer coming back after a break is picked up on beat one.
- All tunable in `BeatTrackerOptions`. Not thread-safe: one input thread or a lock.

## Follower

- Speed = player's tempo ÷ the song's local beat length × (1 + correction).
- Correction = 0.5 × phase error in beats, capped at **±8%**. The phase error is measured on the
  first beat whose slowest note hasn't been sent (the band can't move what's inside the lookahead),
  against the nearest predicted beat, so it's never more than half a beat either way.
- Re-aimed on every counted hit and every 50 ms between (catches holding, lets corrections decay).
  Changes under 0.1% are skipped.
- **Aligned start:** when the tracker is locked, the song starts at the player's tempo with its
  first beat on one of their predicted beats, at least lookahead + 100 ms ahead.

## CLI

| Command | Does |
| --- | --- |
| `play SONG… --follow PORT [--follow-on TRIGGER]` | Follow a player. Each song waits for a count-in (4 steady hits) or Enter / the start pedal. Excludes `--tap` and `--clock-in`. Trigger syntax as `--tap-on`: `note:38` (snare), `note@10` (any drum) |
| `beats PORT [--on TRIGGER] [--tempo BPM] [--record FILE]` | Show what the tracker hears per hit (tempo, state, ms from prediction); record hits |
| `simulate [SONG] --follow FILE [--notes]` | The band follows recorded hits on a virtual clock; median, p95 and worst band − player after 4 beats |

## Hit file

```
seconds,note,velocity
0.0000,38,100
0.6012,38,96
```

One hit per line, seconds as the first comma-separated field (others ignored). Blank lines, `#`
comments and a header line are skipped. `beats --record` writes times relative to the first hit.

## Measured

Virtual clock (`BeatFollowerTests`, modeled player, 30 ms band latency):

| Player | Band − player |
| --- | --- |
| Steady, no jitter | 0 ms |
| 100 BPM, 12 ms σ jitter | median ≤ 15 ms, p95 ≤ 35 ms |
| Speeding up 100 → 124 BPM over 48 beats, 8 ms σ | p95 ≤ 40 ms |
| Stops for 8 beats | Holds 100 BPM exactly, back within 2 ms on return |
| Comes back 90 ms late | Within 10 ms after 12 beats, beat lengths never change > 9% |
| Latency 5 vs 80 ms | Same accuracy (±3 ms) |

A steady ramp leaves a constant lag (about 18 ms for +0.3 BPM per beat): the tracker's tempo
loop is first order. macOS, real CoreMIDI ports (`play --follow` → `virtual`-style listener),
92 BPM with 10 ms σ jitter: counted in on the 4th hit, started at 91.8 BPM on the player's beat,
median 16.9 ms, worst 26.4 ms.
