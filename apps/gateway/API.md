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

## POST /characters/{id}/claim

Claim ownership of a character for a world and load it. A character is owned
by exactly one world-server at a time (see `docs/voyage-transfer.md`); this
atomically takes ownership when nobody holds it yet or the caller already
does, and creates the row (with default meters) on a brand-new UUID. Inventory
is keyed by the stable `ItemId` enum name; the meters are display points
(0..100). Called by a world-server at Hello — never by the client.

- `id` — UUID (path).
- `worldId` — the claiming world's id (body).

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
  "health": 100,
  "warmth": 100
}
```

```
409 Conflict   — owned by a different world; the join must be denied
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
