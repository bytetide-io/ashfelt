# Gateway API

Base URL in dev: `http://localhost:5041` (see `Properties/launchSettings.json`).
Requires `ASHFALL_DB` (Postgres connection string) for character storage.

## Authentication

`/characters/*` and `/voyage*` are world-server-only — the client never reads
or writes character state directly (invariant #3). Every request to those
paths must carry:

```
X-Ashfall-Key: <ASHFALL_GATEWAY_KEY>
```

Defaults to `ashfall` on both sides for local dev, same convention as the
world-server's `ASHFALL_CONNECT_KEY`; **override it in any shared or
production deployment**. A missing or wrong key gets `401 Unauthorized`.
`/health` and `/worlds` are unauthenticated — `/worlds` is the public
destination list the client's travel menu reads directly.

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

Atomically take ownership of a character for the calling world and load it.
Inventory is keyed by the stable `ItemId` enum name; the four meters are
display points (0..100). A UUID that has never saved before is created fresh
(defaults) and claimed in the same call, so this doubles as first-join.

This is the *only* way to load a character — there is no plain GET. Loading
without claiming would let two world-server connections for the same
character each hold an independent in-memory copy of one inventory, which is
a duplication bug, not just a race; see `docs/architecture.md`.

- `id` — UUID (path).
- `worldId` — the requesting world's id (body).

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
409 Conflict    — another world already owns this character; do not load it
```

## PUT /characters/{id}

Upsert a character, creating it on first save. Body is the same shape as the
claim response. Does not touch ownership — a session already holds the claim
for the world calling this.

- `id` — UUID (path).

```json
PUT /characters/6f9619ff-8b86-d011-b42d-00cf4fc964ff
{
  "inventory": { "Wood": 12, "Stone": 3 },
  "hunger": 87,
  "stamina": 100,
  "health": 100,
  "warmth": 100
}
```

```
200 OK
```
