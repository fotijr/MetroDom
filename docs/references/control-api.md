# Control API v0

The one front door for apps ([ADR 0003](../design-docs/0003-control-api.md)). The contract is the
OpenAPI document [`src/MetroDom.Conductor/Api/openapi.json`](../../src/MetroDom.Conductor/Api/openapi.json),
also served at `/api/v0/openapi.json`, and the stream's event schema. A test fails if a route and the spec disagree. Code:
`src/MetroDom.Conductor/Api/ControlApi.cs`. Limits: TD-27 in the
[tech-debt tracker](../exec-plans/tech-debt-tracker.md).

## Starting it

```bash
metrodom serve ~/songs                                                  # the service: LAN, library, saved tokens
metrodom play set1.mid set2.mid -o "MetroDom xylo" --api 7890          # one set, this computer only
METRODOM_API_TOKEN=… metrodom play song.mid -o 0 --api 0.0.0.0:7890    # one set, the LAN, fixed token
```

- `metrodom serve` ([Conductor service](conductor-service.md)) runs it always, on the LAN by
  default, with a library to queue from, tokens and the queue kept across restarts, and mDNS.
- `play --api` serves one set: its files are the queue (they can be reordered and removed, but
  nothing can be added: there is no library). The API ends with the set.
- `PORT` binds loopback only. A LAN address needs `HOST:PORT` and prints a warning (plain HTTP).
- The token shown at start is the **owner's**: `METRODOM_API_TOKEN`, or 128 random bits shown
  on the Conductor's screen (pairing by looking at the box, [SECURITY](../SECURITY.md)). Not a CLI
  flag, so it doesn't land in shell history or `ps`.
- `--policy FILE` picks the [policy](policy-profile.md) whose `roles`, `queue` and `sessions`
  apply. Default: live-band.

## Roles and tokens

- The owner can do everything. The owner hands out **operator** tokens (a performer, a rental
  customer for their booking) and **guest** tokens (a party, a bar) with `POST /tokens`:

  ```bash
  curl -H "$T" -X POST http://127.0.0.1:7890/api/v0/tokens -H 'Content-Type: application/json' \
    -d '{"role":"operator","hours":48,"label":"Smith party"}'
  ```

  The reply has the secret (shown once), a `pairingUrl` for the web remote, `pairingQr` (that link
  as an SVG QR code), and the holder (`id`, `role`, `label`, `issuedAt`, `expiresAt`). Asked from
  the box itself (localhost) while the API is on the LAN, the link uses the LAN address, since
  a guest's phone can't open localhost. `hours` defaults to, and may not exceed, the policy's
  `sessions.maxTokenHours`. Labels: at most 60 characters.
- Each route needs one grant (`x-metrodom-grant` in the spec); the policy's `roles` say which
  role has it. A working token without it gets `403` naming the grant. A route that declares no
  grant is refused to everyone (fail closed), and a test checks every route against the spec.
- `GET /session` works for any working token: role, label, end, the policy's mode and the grants.
  Apps use it to hide what they can't do; the Conductor enforces it either way.
- `GET /tokens` lists the tokens out now (never the secrets); `DELETE /tokens/{id}` revokes one
  at once, and its open streams close with `1008` "Your token expired or was revoked." An
  expired token is `401` like a wrong one.
- Handed-out tokens are kept only as SHA-256 hashes, compared in constant time. In `play` they
  live in memory and end with the run; `serve` saves the hashes (never the secrets) so phones stay
  paired across restarts, and drops tokens that expired while it was off. At most 1024 out,
  guests who joined with a room's QR included.
- Measured on macOS against a live set under `crowd-jukebox`: a guest token read `/session`,
  `/state`, `/queue` (200) and was refused panic, next, tap and both token routes (403); the web
  remote opened with its pairing link showed the band with no buttons and "table 4 · guest ·
  crowd-jukebox · until …"; revoking it sent the open page back to pairing; the owner link
  showed every control.

## Queue and library

- `GET /queue` is the set as an editable queue: recent history (up to 20 played songs), the song
  playing or waiting for start (`current`), then what's coming. Each song has an `id` that stays
  put while indexes move, its `title`, `addedBy` (the token's label, else its role) and `editable`.
- Only what's coming can change (`index >= editableFrom`): the song up now is skipped with
  `POST /transport/next`, never deleted, and history stays history. Edits need `queue.edit`.
- `POST /queue {"song": "<library id>", "at": 3}` queues a song from `GET /library` (at the end
  without `at`). Only ids from the library's last scan resolve, so a request can't name a file
  outside the folder. `play` has no library: `409`.
- `POST /queue/{id}/move {"to": 2}` and `DELETE /queue/{id}`: `404` for an unknown id, `409` for a
  song that has played or is up now. At most 200 songs.
- `GET /library` (grant `view`): `id` (its path under the library folder), `title`, length and
  starting BPM, by title. Rescanned at most every 5 s. Never file paths on disk.
- What happens when a queue that ran dry gets a song is the policy's `queue.autoplay`
  ([policy profile](policy-profile.md)).

## Requests and votes (jukebox)

Under `serve`, guests ask for songs and vote on them, within the policy's `queue` rules
([policy profile](policy-profile.md#queue)). The rules live in `RequestDesk`
(`src/MetroDom.Core/Setlist/RequestDesk.cs`).

- **Order of what's coming:** `pinned` songs first, then `request`s (most votes first, then the
  one asked first), then the `set` (the venue's or band's own list, and fill songs). Each song
  shows its `kind` and `votes`, and for the caller, `voted` and `mine`. Nobody's ids or votes are
  shown, only counted.
- An operator placing a song decides where it plays. Added or moved ahead of every request, it's
  **pinned** ("play this next"); anywhere after, it's part of the set. A request an operator
  moves stops riding on votes.
- `POST /requests {"song": "<library id>"}` (grant `queue.request`): `201` a new request with the
  requester's vote. `200` with `countedAsVote` when someone already requested the song.
  - `409`: requests closed or paused, the song is already queued, up now, or played within
    `songCooldownMinutes`. The message says when it can be asked for again.
  - `429` with `Retry-After`: a guest limit (`maxPerGuest` waiting, `cooldownSeconds` between
    requests, or more than 20 tries a minute, refused ones included).
  - Owners and operators skip the guest limits but not the song rules.
- `POST /queue/{id}/vote` and `DELETE /queue/{id}/vote` (grant `queue.vote`): one vote per token
  per request. You can't take back the vote on your own request. `409` for a song that isn't a
  coming request, `404` for an unknown id.
- `GET /requests` (grant `view`): `open`, `votes`, the rules, and `you` (`waiting`, `maxWaiting`,
  `nextRequestInSeconds`). `PUT /requests {"open": false}` (grant `queue.edit`) pauses requests
  and votes; the policy decides whether there are any at all. The queue's `requests.open` follows
  on the stream.
- A guest token is the guest. One invite QR handed to a table is one guest for the limits; a
  room's [join QR](#join-qr-and-big-screen) gives each phone its own.
- `play` takes no requests: `409`.

## Join QR and big screen

One QR code for a whole room, where each phone that scans it becomes its own guest, so the
request limits hold per phone, not per table.

- The owner makes a **join code**: `POST /tokens {"role":"guest","join":true,"label":"bar"}`
  (or the remote's Invite → Everyone in the room). The reply has the code (once), `joinUrl`
  (the remote with `#join=…`), `joinQr` (its QR code) and `screenUrl`. `409` if the policy gives
  guests nothing to see.
- A phone that opens the join link trades the code for its own guest token: `POST /join` with
  the code as the bearer token. The token is labeled "guest N" (what the queue shows as who asked)
  and ends with the join code, or sooner under `sessions.maxTokenHours`. A phone that already has
  a working token keeps it.
- Limits: one address may join 3 times an hour (`429` with `Retry-After`), so clearing a phone
  doesn't buy fresh request limits; on the venue's network the box sees each phone's own
  address. A join code lets in at most 300 guests (`409`).
- **The big screen** (`screenUrl`, `/screen.html#join=…`) is a page for a TV in the venue: the
  song playing with its section and progress, the next 6 songs with their votes and who asked,
  the join QR (`GET /join/qr`), whether requests are open, and the curfew. It holds the join
  code, which works as a token only for `view` routes (`/session` says `join: true`): anyone in
  the room can scan it, so it never gets more than a guest would.
- `GET /tokens` lists a join code with `join: true` and `guests` (how many are in), and each
  joined guest with `via` (the code's id). Ending the join code (`DELETE /tokens/{id}`) ends its
  guests too.
- Checked in a browser against `serve` under `crowd-jukebox`: two phones joined with curl and
  requested songs (shown as guest 1 and guest 2 with their votes on the screen), the browser
  joined as guest 3, opening the link again kept that token, a fresh page from the same address
  got "joined 3 times this hour", and the owner's Invite panel made a room QR with Open screen.

## Web remote

`play --api` and `serve` also serve a phone-sized remote at `/` (`src/MetroDom.Conductor/Api/web/`:
plain HTML, CSS and JS embedded in the binary, no framework or build step). It shows the song,
progress, section, tempo, queue, instruments and messages live from `/stream`, and has start/stop,
next, tempo ±2%, tap, vamp, outro, a volume slider (with `volume`) and a big **PANIC** button, and
(with `curfew`) a time to set tonight's curfew and a Lift button; the curfew in force shows under
the tempo.

- **Queue and library** (with `queue.edit`): ↑ ↓ ✕ on each coming song, and a searchable library
  with an Add button per song. Everyone with `view` sees the queue and who added what.
- **Requests** (with `queue.request` / `queue.vote`): guests get the library with a Request
  button ("Queued" once it's in), ▲ N on each coming request to vote or take a vote back, and
  "your request" on their own. Refusals show as messages ("try again in 5 min"). Operators get
  Pause requests / Open requests.
- **Invite** (owner): pick operator, guest or everyone in the room (a [join QR](#join-qr-and-big-screen)),
  a name and hours; the page shows the QR code to scan with a phone camera, a Copy link (and
  Share where the phone has it) button, Open screen for a join QR, and the tokens out now with
  Revoke (join codes show how many joined, with End).
- **Installable app:** a web manifest, icons and a service worker that keeps the app's own files
  (never the API) so it opens, showing "Disconnected", when the box is out of reach. Browsers only
  run service workers on HTTPS or localhost, so on the LAN over plain HTTP a phone gets a
  home-screen shortcut, not an offline app (TD-27). Checked with headless Chrome on localhost:
  worker active, shell cached, no installability errors, opened from cache with the box stopped.

- **Roles:** the page asks `/session` first and shows only the buttons the token's role may use
  (panic too), with the role, label and end under the song title.
- **Pairing:** the Conductor prints `Remote: open http://HOST:PORT/#token=…` (the owner's link);
  `POST /tokens` returns operator and guest links in the same form. The token sits in the
  URL fragment, which browsers never send to a server; the page moves it to local storage and
  strips it from the address bar. With `0.0.0.0` the link uses this computer's LAN address. With
  `METRODOM_API_TOKEN` the link has no token and the page asks for it.
- **Forget** removes the token from the device. A refused token is dropped and the page asks again.
  Opening a new pairing link in an open tab switches to it without a reload.
- The page is public (it holds no secrets); every call it makes needs the token.
- Locked down: `Content-Security-Policy` allows only its own files and this server (no inline
  script or style, no framing; its own manifest and worker; `blob:` images for invite QR codes), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
  Song titles and messages are inserted as text, never HTML.
- Checked in a browser against a live set: pairing, tap tempo while waiting, start, tempo +, vamp,
  panic, wrong and stale tokens, phone width (375 px, no sideways scroll), light and dark.

## Rules

- Every route except the spec needs `Authorization: Bearer <token>` (compared in constant time).
  Wrong, missing, revoked or expired → `401` with `WWW-Authenticate: Bearer`. A role without the
  route's grant → `403`.
- Bodies over 4 KB → `413`. Errors are `{"error": "…"}`, successes `{"message": "…"}`.
- Intents go through `LiveControl`, the same place keys, pedals and taps land, so the safety
  governor and panic path are unchanged. Panic is never refused to an owner or operator; a guest
  never has it.
- Ctrl+C and SIGTERM stop the whole set (panic, then exit), never just the API.
- Streams: browser-friendly token as a subprotocol, at most 16 open, 15 s keep-alive pings.

## Routes (under `/api/v0`)

| Method | Path | Does | Errors |
| --- | --- | --- | --- |
| GET | `/` (outside `/api/v0`) | The web remote (no token), plus `/app.js`, `/app.css`, `/sw.js`, `/manifest.webmanifest`, icons | |
| GET | `/openapi.json` | The spec (no token) | |
| GET | `/state` | `waiting`/`playing`, song, position, duration, tempo (bpm, speed, pending, source), section, following, curfew (`"Set ends 23:00"`, `"Curfew until 10:00."`, or null), volume (0-100) | |
| GET | `/queue` | History, current, coming: ids, titles, who added them, what's editable | |
| POST | `/queue` `{"song":"covers/Late Night.mid","at":3}` | Queue a library song (`queue.edit`) | `400`, `404` unknown song, `409` no library, full or not editable |
| POST | `/queue/{id}/move` `{"to":2}` | Move a coming song (`queue.edit`) | `400`, `404`, `409` |
| DELETE | `/queue/{id}` | Take a coming song out (`queue.edit`) | `404`, `409` up now or played |
| GET | `/library` | Songs that can be queued: id, title, length, BPM (empty in `play`) | |
| GET | `/instruments` | Name, channels (1-16), latency, notes, connected, `latencyCheck` (boot check verdict, [calibration](calibration.md)) | |
| POST | `/transport/start` | Start the song up next. A Start right after adding to an empty queue isn't lost: it carries into the wait | `409` if playing or nothing is queued |
| POST | `/transport/stop` | Stop the song | `409` if waiting |
| POST | `/transport/next` | Next song now (skips while waiting) | |
| POST | `/tempo` `{"bpm": 96}` | Tempo now, or for the next song while waiting (30-300) | `400` |
| POST | `/tap` | One tap (shared with keys and pedals) | |
| POST | `/sections/vamp`, `/sections/outro` | Vamp toggle / outro after this section | `409` if waiting |
| POST | `/volume` `{"percent": 70}` | The band's volume, 0-100% (`volume`): scales notes before the policy's caps, so it only ever turns down; 0 holds back new notes. In `state` as `volume` | `400` |
| POST | `/curfew` `{"at":"23:30"}` | Tonight's curfew, the next time the clock reads it (`curfew`; [policy](policy-profile.md#limits)) | `400` not HH:mm |
| DELETE | `/curfew` | Lift tonight's curfew (`curfew`); the policy's stay | `409` none set |
| POST | `/panic` | Stop every actuator; the band then waits | |
| GET | `/session` | This token: role, label, end, mode, grants (any working token) | |
| GET | `/tokens` | Operator and guest tokens out now (owner) | |
| POST | `/tokens` `{"role":"guest","hours":6,"label":"…"}` | Hand out a token: secret once, pairing link and its QR code (owner). `"join":true`: a room's join code | `400`, `409` |
| DELETE | `/tokens/{id}` | Revoke it now; its streams close; a join code takes its guests (owner) | `404` |
| POST | `/join` | Trade a join code (the bearer) for this phone's own guest token | `401`, `409` full, `429` |
| GET | `/join/qr` | The join link and QR for the join code presented (the big screen) | `401` |
| GET | `/stream` | Live state over a WebSocket (below) | `400` not a WebSocket, `503` over 16 streams |
| GET | `/events.schema.json` | JSON Schema of the stream's events (no token) | |

## Live stream (`/stream`)

A WebSocket, subprotocol `metrodom.v0`, one JSON text frame per event. The contract is
[`events.schema.json`](../../src/MetroDom.Conductor/Api/events.schema.json).

| Event | When |
| --- | --- |
| `hello` | First, with `api: "v0"` and `positionEveryMs` |
| `state` | On connect, then whenever anything but the position changes (same shape as `GET /state`, `positionSeconds` null) |
| `queue` | On connect, then whenever the queue changes: a song queued, moved or removed, or the set moved on (same shape as `GET /queue`) |
| `position` | Every 250 ms while playing: `positionSeconds`, `durationSeconds` |
| `instruments` | On connect, then when one connects or disconnects or its latency check changes |
| `message` | Lines for people: vamp/outro, panic, unplug/replug, tempo set while waiting |

- Every event has `seq`, counting up from 1 per connection.
- **Auth:** the `Authorization` header, or (browsers can't set headers on a WebSocket) the extra
  subprotocol `token.<token>`. The server answers with `metrodom.v0` only, never echoing the token.
- Clients only listen in v0; anything they send is ignored. Each stream polls the same snapshot as
  `GET /state` every 100 ms and sends only changes, so the band's threads never wait on a socket.
- When the set ends (Ctrl+C, SIGTERM, the last song of a `play` set) streams get close `1001` "The set has ended."
- Measured on macOS: a separate client with the subprotocol token saw hello, state, instruments,
  start → playing, positions, the vamp message, panic → waiting, then the close on SIGTERM.

## Example

```bash
T="Authorization: Bearer $METRODOM_API_TOKEN"
curl -H "$T" http://127.0.0.1:7890/api/v0/state
curl -H "$T" -X POST http://127.0.0.1:7890/api/v0/tempo -H 'Content-Type: application/json' -d '{"bpm":150}'
curl -H "$T" -X POST http://127.0.0.1:7890/api/v0/panic
```

Measured on macOS with a two-song set: 401 without the token, a tempo set while waiting started
song 1 at 150 BPM, a second start got 409, next moved to song 2, panic stopped it and the set
waited, and the virtual xylophone played throughout.
