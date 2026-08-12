# Gateway API

Base URL in dev: `http://localhost:5041` (see `Properties/launchSettings.json`).
Requires `ASHFALL_DB` (Postgres connection string) for character storage.

## GET /health

Liveness plus the protocol version the gateway was built against.

```json
200 OK
{ "status": "ok", "proto": 6 }
```

## GET /worlds

Stub world registry (Phase 3 will make it live).

```json
200 OK
[ { "id": "continent-a", "host": "127.0.0.1", "port": 9050 } ]
```

## GET /characters/{id}

Load a character by its device UUID. Inventory is keyed by the stable `ItemId`
enum name; the three meters are display points (0..100). Read-only: it does
not claim ownership, so it never gates whether a world-server may load this
character live. A world-server joining a player must use
`POST /characters/{id}/claim` (or `/voyage/claim`) instead.

- `id` — UUID (path).

```json
200 OK
{
  "inventory": { "Wood": 12, "Stone": 3 },
  "hunger": 87,
  "stamina": 100,
  "health": 100
}
```

```
404 Not Found   — no character stored for this UUID yet (start fresh)
```

## POST /characters/{id}/claim

Claims exclusive ownership of a character for a normal (non-voyage) join and
returns its stored state, creating the row with defaults on a character's
very first join. Atomic: only one world-server can hold the claim at a time,
which is what stops the same character being loaded live on two world-servers
at once. A claim held more than 300s without a matching `/release` is treated
as stale and may be overridden — the backstop for a world-server that crashed
without ever releasing.

- `id` — UUID (path).

```json
POST /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff/claim
{ "worldId": "continent-a" }
```

```json
200 OK
{
  "inventory": { "Wood": 12, "Stone": 3 },
  "hunger": 87,
  "stamina": 100,
  "health": 100
}
```

```
409 Conflict   — already claimed by another world and not stale; reject the join
```

## POST /characters/{id}/release

Releases this world's claim on a clean disconnect, so the character is
immediately rejoinable elsewhere instead of waiting out the staleness window.
Only clears the claim when `worldId` still matches the current owner — a stale
release can never clobber a newer claim.

- `id` — UUID (path).

```json
POST /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff/release
{ "worldId": "continent-a" }
```

```
200 OK
```

## PUT /characters/{id}

Upsert a character, creating it on first save. Body is the same shape as the
GET response.

- `id` — UUID (path).

```json
PUT /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff
{
  "inventory": { "Wood": 12, "Stone": 3 },
  "hunger": 87,
  "stamina": 100,
  "health": 100
}
```

```
200 OK
```
