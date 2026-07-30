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

Load a character by its device UUID **and atomically claim ownership** for
`worldId` — a character is owned by exactly one world-server at a time
(`docs/voyage-transfer.md`), and this holds for an ordinary join, not just a
voyage arrival. Inventory is keyed by the stable `ItemId` enum name; the three
meters are display points (0..100).

- `id` — UUID (path).
- `worldId` — the calling world-server's own id (query string), the same
  value it passes as `fromWorldId` to `/voyage`.

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
409 Conflict    — a different world currently owns this character; it must
                  arrive here via voyage, never be loaded on two worlds at once
```

## PUT /characters/{id}?worldId={worldId}

Upsert a character, creating it on first save. Body is the same shape as the
GET response. Also releases `worldId`'s ownership claim back to unowned as
part of the same write — but only if `worldId` still holds it, so a save from
a session this world-server has already superseded (an evicted duplicate
connection) can't clear a claim now held by whoever actually owns the
character.

- `id` — UUID (path).
- `worldId` — the calling world-server's own id (query string).

```json
PUT /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff?worldId=continent-a
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
