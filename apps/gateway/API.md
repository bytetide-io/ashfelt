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

Load a character by its device UUID, **claiming ownership for `worldId` in the
same call**. A world-server calls this at Hello for a plain join (no voyage
ticket). Inventory is keyed by the stable `ItemId` enum name; the four meters
are display points (0..100).

A brand-new UUID gets a fresh row it now owns (no more 404-for-new — the row is
created here). Ownership persists across a plain disconnect/reconnect to the
same `worldId`; moving to a different world requires the voyage protocol
(`/voyage`, `/voyage/claim`), never a bare Hello.

- `id` — UUID (path).
- `worldId` — the calling world-server's id (query, required).

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
409 Conflict   — another world already owns this character, or a voyage is
                 currently in flight for it (a live, unclaimed ticket exists).
                 The caller must reject the join.
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
