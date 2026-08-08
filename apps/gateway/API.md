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
enum name; the three meters are display points (0..100).

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

## POST /characters/{id}/claim

Claims ownership of a character for a normal (non-voyage) join. A world-server
calls this **before** `GET /characters/{id}` on every plain connect — it is the
join-path counterpart to `/voyage/claim`, which already enforces ownership for
transfers. Succeeds when no world owns the character yet, or the calling world
already does (a reconnect); creates the row on a brand-new character.

- `id` — UUID (path).

```json
POST /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff/claim
{ "worldId": "continent-a" }
```

```
200 OK          — claimed; the caller may now load the character
409 Conflict    — another world currently owns this character live
```
