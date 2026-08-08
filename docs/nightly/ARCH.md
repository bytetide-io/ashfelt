# Ashfall — architecture notes (nightly engineer's map)

Honest, as-of-tonight map of the codebase for the unsupervised nightly-engineer
loop. Companion to `docs/architecture.md` (the authored source of truth for
invariants) and `docs/gameplay-roadmap.md` (the authored plan) — this file is
the *debt ledger*: what's actually here, what's fragile, what's untested.
Update it whenever a night's work changes the picture materially; don't let it
drift into fiction.

## Topology

```
apps/gateway        ASP.NET minimal API. Owns accounts/characters (Postgres).
                     Stateless, horizontally scalable in principle (nothing
                     in-process is authoritative — Postgres is).
apps/world-server    Top-level-statement console app, one process = one
                     bounded world. UDP via LiteNetLib. In-memory authoritative
                     state (players, world diffs); Postgres for persistence.
apps/client          Godot 4 .NET client. 3D projection over a tile-grid sim.
packages/sim-core    Deterministic rules shared by client + world-server
                     (net8.0, referenced by both). This is the only place
                     terrain/crafting/harvest/survival logic may live.
packages/shared-proto Wire message ids (MessageId), ItemId, Tuning constants,
                     CharacterState DTO. Shared by gateway, world-server,
                     client.
infra/migrations    Hand-rolled, numbered, idempotent SQL files. No migration
                     framework — applied by hand / docker-entrypoint-initdb.
```

Two runtimes: `sim-core`/`shared-proto`/`client` target **net8.0** (Godot's
runtime); `world-server`/`gateway` target **net10.0**. This is intentional
(see `docs/architecture.md`), not drift.

## Data ownership (who's allowed to know what)

- **Gateway DB** (`character` table): inventory, hunger/stamina/health/warmth,
  `owner_world_id`, voyage tickets. This is the only durable home for a
  character. World-servers never persist a character locally.
- **World-server DB** (`tile_diff`, `structure` tables, keyed by `world_id`):
  only *deltas* from the deterministic seed. Terrain itself is never stored —
  `TerrainGenerator` regenerates it from `(seed, coord)` every time, diffs
  replay on top at startup (`WorldStore.LoadDiffsAsync`/`LoadStructuresAsync`).
- **In-memory only, per world-server process**: `Dictionary<NetPeer, Player>`,
  current tick, harvest-node durability (`HarvestProgress` is never
  persisted — only the felling strike writes a diff).

## Character ownership — the join/voyage protocol (as of tonight)

A character must be owned by **exactly one** world-server at a time
(`character.owner_world_id`). Two paths acquire ownership:

- **Voyage arrival** (`Hello` with a non-empty ticket) → `POST
  /voyage/claim`. Deletes the single-use ticket row and sets
  `owner_world_id`, guarded by `owner_world_id IS NULL` so a reclaimed
  character can't be double-claimed.
- **Plain (re)connect** (`Hello` with an empty ticket) → `POST
  /characters/{id}/claim`, added tonight. Succeeds only when nobody owns the
  character or this world already does; 409s otherwise.

Within one world-server, a same-`CharacterId` reconnect race (old `NetPeer`
hasn't timed out yet when a new one presents the same UUID) is resolved by
kicking the stale local session — save, remove, disconnect — before the new
one loads. See `docs/nightly/LOG.md` (2026-08-08) for why this needed fixing
and how it was verified.

**Residual gap, not closed tonight:** if the origin world (A) receives a
plain reconnect for a character mid-voyage (ticket minted, not yet claimed by
B), A's claim succeeds — `owner_world_id` is `NULL` at that point, which is
indistinguishable from "nobody has ever owned this." B's later claim then
correctly fails (`owner_world_id IS NULL` guard), so no duplication occurs,
but B's attempt does consume the ticket for nothing and reports a generic
"already claimed" error rather than the real cause. Verified live tonight;
see BACKLOG.md.

## Net protocol shape

`MessageId` (`packages/shared-proto/Protocol.cs`) is a flat byte enum,
hand-serialized with `NetDataWriter`/`NetDataReader` — every message's wire
layout is documented only as an XML comment on its enum value, and
client/server both hand-write matching `Put`/`Get` calls in order. No shared
read/write helper exists yet (`gameplay-roadmap.md` §3.2 already flags this).
`ProtocolVersion.Current` (10) must be bumped on any layout change; client and
server disconnect on mismatch at `Hello`.

Dispatch on both ends is a `switch` on `MessageId` inside the receive
callback — a flat table, not yet a lookup (`gameplay-roadmap.md` §3.4 flags
the client side of this; the server's `Program.cs` switch has the same
shape and is growing the same way).

## Known-fragile areas (sizes as of tonight, `wc -l`)

| File | Lines | Why it's here |
|---|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 | HUD god-script: meters, hotbar, craft/build/travel panels, item slots — one file, one class. Flagged in `gameplay-roadmap.md` §3.4 at 419 lines; has more than doubled since. |
| `apps/client/scripts/world3d/World3D.cs` | 735 | Net dispatch + terrain streaming + local player + remote players + interaction/targeting, one class. Flagged at 616 lines in the same doc. |
| `apps/world-server/Program.cs` | 468 (grew ~40 tonight) | Everything: connection lifecycle, message switch, tick loop, all in one top-level-statement file. Not yet unreadable, but the switch is the same growth pattern as the client's. |
| `apps/client/scripts/WorldConnection.cs` | 421 | Client-side net dispatch mirror of `Program.cs`'s switch. |

None of these are broken — they're read-fine today — but each new message or
HUD panel is now edited into an already-large file. See BACKLOG.md; not
touched tonight (the join/reconnect duplication bug outscored refactoring on
the audit rubric, and the rubric says fix the one thing that scores highest,
not five things at once).

## Test coverage — honest picture

- `tests/sim-core.tests` (84 tests, all passing as of tonight): thorough for
  determinism, terrain, harvest, crafting, movement, survival, world-clock.
  This is the strongest-tested part of the codebase by far.
- **`apps/gateway` has zero automated tests.** Tonight's ownership-claim fix
  — new SQL, a new endpoint, a tightened race condition — was verified by
  hand against a live local Postgres instance (see LOG.md) because no test
  harness exists to check it automatically. The next person who touches
  `Program.cs`'s voyage/claim SQL has no regression safety net. This is the
  single biggest testing gap in the repo and is now more urgent than it was
  this morning, precisely because that code just got more load-bearing.
- **`apps/world-server` has zero automated tests** beyond what `sim-core`
  indirectly covers. The `Hello` handler's ownership/dedup logic added
  tonight was verified by build + a live Postgres smoke test, not by an
  automated test — there's no harness for spinning up two simulated
  `NetPeer` clients against a world-server.
- `apps/client` has no tests (expected for a Godot C# client at this stage;
  not flagging it as a gap requiring immediate action).

## Environment notes for future nightly sessions

- This sandbox ships **no .NET SDK by default**. `dotnet-sdk-10.0` and
  `dotnet-sdk-8.0` install cleanly via `apt-get` (after `apt-get update`;
  the specific 10.0 point version isn't always mirrored on first try — retry
  the update). Budget a few minutes for this every session until the image
  is baked with it.
- **Docker doesn't run here** (`dockerd` isn't reachable, and starting it
  fails on `ulimit` — no privilege for the container). `docker compose -f
  infra/docker/docker-compose.yml up` from `README.md` will not work in this
  sandbox. Instead: `apt-get install -y postgresql`, start it with `service
  postgresql start`, create the `ashfall` role/db by hand, and apply
  `infra/migrations/*.sql` in order with `psql`. This is what tonight's
  verification used, end to end, successfully.
