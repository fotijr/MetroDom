# Control API v0

The one front door for apps ([ADR 0003](../design-docs/0003-control-api.md)). The contract is the
OpenAPI document [`src/MetroDom.Conductor/Api/openapi.json`](../../src/MetroDom.Conductor/Api/openapi.json),
also served at `/api/v0/openapi.json`, and the stream's event schema. A test fails if a route and the spec disagree. Code:
`src/MetroDom.Conductor/Api/ControlApi.cs`. Limits: TD-27 in the
[tech-debt tracker](../exec-plans/tech-debt-tracker.md).

## Starting it

```bash
metrodom play set1.mid set2.mid -o "MetroDom xylo" --api 7890          # this computer only
METRODOM_API_TOKEN=… metrodom play song.mid -o 0 --api 0.0.0.0:7890    # the LAN, fixed token
```

- v0 lives inside a running `play` set: the API controls that set (its songs are the queue).
- `PORT` binds loopback only. A LAN address needs `HOST:PORT` and prints a warning (plain HTTP).
- The token shown at start is the **owner's**: `METRODOM_API_TOKEN`, or 128 random bits shown
  on the Conductor's screen (pairing by looking at the box, [SECURITY](../SECURITY.md)). Not a CLI
  flag, so it doesn't land in shell history or `ps`.
- `--policy FILE` picks the [policy](policy-profile.md) whose `roles` and `sessions` apply.
  Default: live-band.

## Roles and tokens

- The owner can do everything. The owner hands out **operator** tokens (a performer, a rental
  customer for their booking) and **guest** tokens (a party, a bar) with `POST /tokens`:

  ```bash
  curl -H "$T" -X POST http://127.0.0.1:7890/api/v0/tokens -H 'Content-Type: application/json' \
    -d '{"role":"operator","hours":48,"label":"Smith party"}'
  ```

  The reply has the secret (shown once), a `pairingUrl` for the web remote, and the holder (`id`,
  `role`, `label`, `issuedAt`, `expiresAt`). `hours` defaults to, and may not exceed, the policy's
  `sessions.maxTokenHours`. Labels: at most 60 characters.
- Each route needs one grant (`x-metrodom-grant` in the spec); the policy's `roles` say which
  role has it. A working token without it gets `403` naming the grant. A route that declares no
  grant is refused to everyone (fail closed), and a test checks every route against the spec.
- `GET /session` works for any working token: role, label, end, the policy's mode and the grants.
  Apps use it to hide what they can't do; the Conductor enforces it either way.
- `GET /tokens` lists the tokens out now (never the secrets); `DELETE /tokens/{id}` revokes one
  at once, and its open streams close with `1008` "Your token expired or was revoked." An
  expired token is `401` like a wrong one.
- Handed-out tokens are kept only as SHA-256 hashes, compared in constant time, in memory: they
  end with the `play` run (persisting them comes with the service, plan 0004 S3). At most 256 out.
- Measured on macOS against a live set under `crowd-jukebox`: a guest token read `/session`,
  `/state`, `/queue` (200) and was refused panic, next, tap and both token routes (403); the web
  remote opened with its pairing link showed the band with no buttons and "table 4 · guest ·
  crowd-jukebox · until …"; revoking it sent the open page back to pairing; the owner link
  showed every control.

## Web remote

`play --api` also serves a phone-sized remote at `/` (`src/MetroDom.Conductor/Api/web/`: plain
HTML, CSS and JS embedded in the binary, no framework or build step). It shows the song, progress,
section, tempo, setlist, instruments and messages live from `/stream`, and has start/stop, next,
tempo ±2%, tap, vamp, outro and a big **PANIC** button.

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
  script or style, no framing), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
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
| GET | `/` (outside `/api/v0`) | The web remote (no token), plus `/app.js`, `/app.css` | |
| GET | `/openapi.json` | The spec (no token) | |
| GET | `/state` | `waiting`/`playing`, song, position, duration, tempo (bpm, speed, pending, source), section, following | |
| GET | `/queue` | Setlist titles and the current index | |
| GET | `/instruments` | Name, channels (1-16), latency, notes, connected | |
| POST | `/transport/start` | Start the song up next | `409` if playing |
| POST | `/transport/stop` | Stop the song | `409` if waiting |
| POST | `/transport/next` | Next song now (skips while waiting) | |
| POST | `/tempo` `{"bpm": 96}` | Tempo now, or for the next song while waiting (30-300) | `400` |
| POST | `/tap` | One tap (shared with keys and pedals) | |
| POST | `/sections/vamp`, `/sections/outro` | Vamp toggle / outro after this section | `409` if waiting |
| POST | `/panic` | Stop every actuator; the band then waits | |
| GET | `/session` | This token: role, label, end, mode, grants (any working token) | |
| GET | `/tokens` | Operator and guest tokens out now (owner) | |
| POST | `/tokens` `{"role":"guest","hours":6,"label":"…"}` | Hand out a token: secret once, pairing link (owner) | `400` |
| DELETE | `/tokens/{id}` | Revoke it now; its streams close (owner) | `404` |
| GET | `/stream` | Live state over a WebSocket (below) | `400` not a WebSocket, `503` over 16 streams |
| GET | `/events.schema.json` | JSON Schema of the stream's events (no token) | |

## Live stream (`/stream`)

A WebSocket, subprotocol `metrodom.v0`, one JSON text frame per event. The contract is
[`events.schema.json`](../../src/MetroDom.Conductor/Api/events.schema.json).

| Event | When |
| --- | --- |
| `hello` | First, with `api: "v0"` and `positionEveryMs` |
| `state` | On connect, then whenever anything but the position changes (same shape as `GET /state`, `positionSeconds` null) |
| `position` | Every 250 ms while playing: `positionSeconds`, `durationSeconds` |
| `instruments` | On connect, then when one connects or disconnects |
| `message` | Lines for people: vamp/outro, panic, unplug/replug, tempo set while waiting |

- Every event has `seq`, counting up from 1 per connection.
- **Auth:** the `Authorization` header, or (browsers can't set headers on a WebSocket) the extra
  subprotocol `token.<token>`. The server answers with `metrodom.v0` only, never echoing the token.
- Clients only listen in v0; anything they send is ignored. Each stream polls the same snapshot as
  `GET /state` every 100 ms and sends only changes, so the band's threads never wait on a socket.
- When the set ends (Ctrl+C, SIGTERM, last song) streams get close `1001` "The set has ended."
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
