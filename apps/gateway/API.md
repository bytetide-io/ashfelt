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

Load a character by its device UUID, **and claim it for `worldId`**. A
character is owned by exactly one world-server at a time (`docs/voyage-transfer.md`);
this is what that invariant is enforced by. Inventory is keyed by the stable
`ItemId` enum name; the four meters are display points (0..100).

- `id` — UUID (path).
- `worldId` — the calling world-server's id (query, required).

The row is created with defaults (empty inventory, full meters) the first
time a UUID is ever seen, so a brand-new character is claimed exactly the
same way a returning one is — there is no unclaimed window between "never
saved" and "owned by someone."

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
400 Bad Request — worldId missing
409 Conflict    — another world-server already owns this character and its
                   claim has not gone stale (see OwnershipStaleAfterHours in
                   Program.cs); the caller must refuse the join
```

## PUT /characters/{id}

Upsert a character, creating it on first save, and **release ownership**
(`owner_world_id` back to `NULL`). The two callers — a player disconnecting,
and a voyage release saving before it mints a ticket — are both "this world
is done with the character" moments. Body is the same shape as the GET
response.

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
