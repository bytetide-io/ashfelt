# Ashfall — architecture map (as of 2026-07-25)

Honest snapshot of the codebase for the overnight-engineer rotation. Read
`docs/architecture.md` and `docs/voyage-transfer.md` first — they are the
decided design. This file is the "what's actually there," including the
rough edges, kept up to date by whoever runs a nightly session.

## Layout and size

```
apps/client         Godot 4 client (C#, net8.0)        3,364 LOC
apps/world-server   Headless authoritative server (C#, net10.0)  912 LOC + PendingSaveTracker
apps/gateway        Minimal REST API: accounts/characters/voyages (net10.0)  210 LOC
packages/sim-core   Deterministic shared rules (net8.0, referenced by both)  967 LOC
packages/shared-proto  Wire message ids + Tuning constants (net8.0)  180 LOC
tests/sim-core.tests   Unit tests for sim-core only                1,035 LOC
tests/world-server.tests  New tonight — covers PendingSaveTracker only
infra/migrations    4 additive SQL migrations, no down-migrations
infra/docker        docker-compose for local Postgres + world-server
```

No `.sln`; built per-project via `dotnet build <path>` (see `.github/workflows/ci.yml`).
Central package management in `Directory.Packages.props` (LiteNetLib, Npgsql,
xunit versions pinned there only).

## The three processes

- **gateway** (`apps/gateway/Program.cs`, 210 lines, one file): a minimal
  ASP.NET minimal-API. Owns `character` (inventory + 4 survival meters) and
  `voyage_ticket` (in-transit ownership handoff). No auth beyond a device UUID
  the client generates once and never changes — there is no login. World
  registry (`/worlds`) is a **hardcoded in-memory dictionary** of two entries
  (`continent-a`, `continent-b`); a world-server does not register itself.
  That's fine for two fixed dev instances, not for adding a third world
  without redeploying the gateway.

- **world-server** (`apps/world-server/`): one process = one region, UDP via
  LiteNetLib, single-threaded event loop (`PollEvents` + a 15 Hz tick
  `Thread.Sleep` loop) — there is no lock contention inside the tick loop
  because everything touching `players`/`world` runs on that one thread.
  Async work (gateway HTTP calls) either blocks that thread deliberately
  (`.GetAwaiter().GetResult()` at join/voyage — a wait is expected there) or
  is fire-and-forget on the thread pool (`Task.Run` on disconnect-save) so a
  slow write never stalls other players. `PendingSaveTracker` (added tonight)
  is the one piece of cross-thread state outside that model — a
  `ConcurrentDictionary` deliberately, since its writer (thread-pool
  continuation) and reader (main thread) run on different threads.

- **client** (`apps/client/scripts/`): `WorldConnection.cs` (421 lines) owns
  the UDP link and wire (de)serialization; `World3D.cs` (735 lines) builds the
  3D scene from chunk data and reacts to server events; `SurvivalHud.cs`
  (940 lines) builds and drives the entire touch UI procedurally (no `.tscn`
  UI layout — the HUD is code-built from `DesignSystem.cs` tokens). Both large
  files are single-responsibility (one drives the network+world, the other
  drives the HUD) and already broken into well-named private methods; neither
  reads as several unrelated systems glued together, but both are candidates
  to split if they grow further (see BACKLOG).

## Shared rules (`packages/sim-core`)

Pure, deterministic, tested: `TerrainGenerator` (height + tile from `Noise.Hash`),
`MovementRules` (server-side speed/height bound check), `HarvestRules` and
`CraftingRules` (data-table driven, not switch statements — see their own
doc comments), `PlacementRules`, `SurvivalRules` (hunger/stamina/health/warmth),
`WorldClock`. `World.cs` composes these into the authoritative in-memory world
(tile diffs + structures) both the server and, indirectly via prediction,
the client rely on.

## Wire protocol (`packages/shared-proto/Protocol.cs`)

`MessageId` enum, one doc comment per message describing its byte layout —
this is the closest thing to a protocol spec and is kept current. `Tuning`
holds every cross-cutting constant: tick rate (15 Hz), client report rate
(15 Hz), chop range (5m), a day length (600s), stats heartbeat (every 2s).

**`Tuning.InterestRadiusChunks = 1` is declared but never read anywhere in the
codebase** — see BACKLOG #1. Every broadcast in `world-server/Program.cs`
(`PlayerStates`, `TileChanged`, `HarvestProgress`, `StructurePlaced`,
`StatsUpdate`) goes to every connected peer regardless of distance. Harmless
at today's scale (a handful of players per world in dev/testing) but it means
architecture.md's invariant #6 ("a client only ever receives entities/chunks
near it") is currently aspirational, not implemented.

## Persistence

- **World state**: seed + diffs, exactly as documented. `tile_diff` and
  `structure` tables, upserted, never rewritten wholesale. No down-migrations
  in `infra/migrations/`, but every migration so far is additive
  (`ADD COLUMN IF NOT EXISTS`, `CREATE TABLE IF NOT EXISTS`) with a sensible
  default for existing rows — the de facto migration strategy is "additive
  only," which has held for 4 migrations.
- **Character state**: lives in the gateway's `character` table, loaded on
  Hello and saved on disconnect or voyage-release. Between those points, all
  progress (everything gathered, crafted, or placed) lives **only** in the
  world-server's in-memory `Player` object — a world-server crash mid-session
  loses it. That's an accepted, documented tradeoff for this phase, not a bug.
- **The reconnect race fixed tonight**: the disconnect save was
  fire-and-forget with nothing stopping a fast reconnect's Hello-time load
  from racing it — see LOG.md for the mechanics and the fix
  (`PendingSaveTracker`).

## Test coverage map

| Area | Coverage |
|---|---|
| `packages/sim-core` | Good — a test file per rule set, determinism explicitly pinned |
| `packages/shared-proto` | None (it's data/enums; low risk) |
| `apps/world-server` | `PendingSaveTracker` only, added tonight. `Player.cs`'s own invariants (`ConsumeOne`, `ApplyCraft` throwing on over-spend, `TryAccept`, `IsWithinReach`, `FindSpawn`), `WorldStore`'s diff/structure round-trip, and the full `Program.cs` message-dispatch switch are all untested. |
| `apps/gateway` | None. The voyage ticket mint/claim/expire dance (`Program.cs:106-206`) is the highest-value thing to cover — it's exactly the kind of race-prone state machine a regression would be silent in. |
| `apps/client` | None (no client test project exists at all). |

## Known debt not fixed tonight

See `BACKLOG.md` for the full ranked list with scores. Headline items:
missing interest management, the wider server/gateway testing gap, and the
`world-server/Program.cs` message dispatch growing into a god-script as more
message types are added.
