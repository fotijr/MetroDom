# Conductor service (`metrodom serve`)

The Conductor as the box's long-running program (plan 0004 S3, [ADR 0002](../design-docs/0002-headless-conductor.md)):
one set that never runs out, a song library to queue from, the [Control API](control-api.md)
and web remote always on, tokens and the queue kept across restarts, and the box advertised on the
LAN. Outputs, safety, policy, controllers, tempo inputs and lights are `play`'s, with the same
flags. Code: `src/MetroDom.Conductor/Commands/ServeCommand.cs`, `src/MetroDom.Conductor/Service/`,
the queue in `src/MetroDom.Core/Setlist/SetQueue.cs`. Limits: TD-27 in the
[tech-debt tracker](../exec-plans/tech-debt-tracker.md).

```bash
metrodom serve ~/songs                                              # discovered band, LAN, live-band policy
metrodom serve ~/songs --policy policies/crowd-jukebox.json -o "MetroDom xylo" -o "MetroDom drums"
metrodom serve ~/songs --api 7890 --no-mdns                         # this computer only
```

## What it does at start

1. Reads the state folder (`--state DIR`, default `$XDG_STATE_HOME/metrodom` or
   `~/.local/state/metrodom`, `%LOCALAPPDATA%\MetroDom` on Windows). First run: a new owner token.
2. Scans the library folder: every `.mid`/`.midi` under it, subfolders included, hidden files and
   links skipped, at most 5000. Files that aren't readable MIDI are named once and left out.
3. Restores the songs that were still to play (those still in the library).
4. Opens the band as `play` does, starts the API on `0.0.0.0:7890` (or `--api`), prints the
   owner's pairing link and, in a terminal, its QR code.
5. With `--onsets PORT`, runs the [latency quick check](calibration.md) on every instrument and
   flags drift (in the remote and the messages); it never blocks the set.
6. Advertises itself over mDNS unless the API is loopback-only or `--no-mdns`.
7. Waits for start before the first song, whatever the policy: a box that just booted (or
   rebooted) never starts the band by itself. With an empty queue it waits for songs.

## The set

- The queue is the set: history (up to 20 played songs), the song up now, and what's coming.
  Owners and operators with `queue.edit` add from the library, reorder and remove what's coming
  from the remote or the API ([control API](control-api.md#queue-and-library)).
- Guests with a jukebox policy request and vote ([requests and votes](control-api.md#requests-and-votes-jukebox)).
  What's coming plays pinned songs, then requests by votes, then the set.
- Songs play one after another as in `play`. When the last one ends, the band waits for more,
  unless the policy has `queue.fill`, which queues a rested library song (added by "fill").
- A song added to a queue that ran dry starts by itself only with the policy's `queue.autoplay`
  (the crowd-jukebox profile); otherwise it waits for start (Enter, the start pedal, or Start on
  the remote). Stop and panic always wait for someone to start again, and so does the first song
  after boot, even one queued (or filled) into an empty queue.
- After a restart the songs still to play come back in order, but as set songs: requests lose
  their votes and guests' limits start fresh (TD-27).
- A queued song whose file was removed is skipped with a message.

## State file (`state.json`, `metrodom.service-state/0`)

```json
{
  "schema": "metrodom.service-state/0",
  "ownerToken": "…",
  "tokens": [{ "id": "f9fee4d9", "role": "guest", "label": "table 4",
               "issuedAt": "…", "expiresAt": "…", "sha256": "…" }],
  "queue": ["covers/Late Night.mid", "Ballad.mid"]
}
```

- Saved whenever a token is handed out or revoked and whenever the queue changes, written whole to
  a temporary file and moved into place. Folder `0700`, file `0600` on Linux and macOS.
- The owner token is kept so phones paired before a restart still work; handed-out tokens are
  hashes only, and ones that expired while the box was off don't come back. `METRODOM_API_TOKEN`
  still replaces the owner token for a run.
- A file that can't be read is an error, never silently replaced (it holds the owner token).
  Delete it to start fresh: every phone pairs again.

## On the LAN (mDNS)

A small responder (`Service/Mdns.cs`, RFC 6762/6763) on the IPv4 group, sharing port 5353 with
Bonjour or Avahi:

| Record | Value |
| --- | --- |
| `_metrodom._tcp.local` and `_http._tcp.local` PTR | `MetroDom on <machine>` |
| SRV | port 7890, target `metrodom.local` |
| TXT | `path=/`, `api=/api/v0`, `v=0`, `mode=<policy>` (never tokens) |
| `metrodom.local` A | this computer's LAN IPv4 addresses; NSEC for everything else (no AAAA wait) |

It announces twice at start and says goodbye (TTL 0) at stop. When it can't bind, the service
runs on and people use the printed link. Measured on macOS: `dns-sd -B _metrodom._tcp` found it,
`dns-sd -L` resolved `metrodom.local:7890` with the TXT, and `curl http://metrodom.local:7890/`
answered 200.

## Checked end to end (macOS, virtual band)

- Live-band policy: songs queued over the API waited for Start on the remote, played in order,
  next skipped; the invite link asked from localhost carried the LAN address.
- Stopped mid-set and restarted under crowd-jukebox: the same owner link worked, the guest token
  still worked (and got 403 on `POST /queue`), the two songs still to play came back and the band
  waited for start; once the queue ran dry, a song added from the remote started by itself (and
  quiet hours held its notes back).
- In a browser: adding from the library, ↑ ↓ ✕ on coming songs, an invite QR code and the tokens
  list with the token from before the restart.
