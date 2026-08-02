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

## GET /characters/{id}?worldId={worldId}

Load a character by its device UUID, atomically claiming ownership for
`worldId`. A character is owned by exactly one world-server at a time — this
call is that claim, not just a read. Inventory is keyed by the stable `ItemId`
enum name; the three meters are display points (0..100).

- `id` — UUID (path).
- `worldId` — the calling world-server's id (query, required).

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
409 Conflict    — owned by a different world-server right now; refuse the join
```

## PUT /characters/{id}

Upsert a character, creating it on first save, and release ownership (this is
always a leave). Body is the same shape as the GET response.

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
