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
- The token is `METRODOM_API_TOKEN`, or 128 random bits shown on the Conductor's screen at start
  (pairing by looking at the box, [SECURITY](../SECURITY.md)). Not a CLI flag, so it doesn't land
  in shell history or `ps`.

## Rules

- Every route except the spec needs `Authorization: Bearer <token>` (compared in constant time).
  Wrong or missing → `401` with `WWW-Authenticate: Bearer`.
- Every token is an **operator** in v0. Roles and the policy's permissions come with sessions.
- Bodies over 4 KB → `413`. Errors are `{"error": "…"}`, successes `{"message": "…"}`.
- Intents go through `LiveControl`, the same place keys, pedals and taps land, so the safety
  governor and panic path are unchanged. Panic is never refused to an authorized caller.
- Ctrl+C and SIGTERM stop the whole set (panic, then exit), never just the API.
- Streams: browser-friendly token as a subprotocol, at most 16 open, 15 s keep-alive pings.

## Routes (under `/api/v0`)

| Method | Path | Does | Errors |
| --- | --- | --- | --- |
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
