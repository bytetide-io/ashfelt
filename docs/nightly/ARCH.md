# Ashfall — architecture map (nightly)

Honest snapshot of the codebase as of 2026-07-28, for whoever (human or
overnight session) picks up work here next. `docs/architecture.md` and
`docs/voyage-transfer.md` are the decided, source-of-truth design docs — this
file is a working map of what actually exists and where the rough edges are.

## Size

~6,700 lines of C# across 5 projects, plus `tests/sim-core.tests`. Small
enough to read in full; this map should save you from having to.

```
apps/client         Godot 4 client (C#, net8.0) — 3D, third-person, touch-first
apps/world-server    Headless authoritative UDP server (C#, net10.0)
apps/gateway         ASP.NET minimal API: accounts, characters, voyage (net10.0)
packages/sim-core    Shared deterministic rules (net8.0) — terrain, movement,
                     harvest, craft, placement, survival, interest, day/night
packages/shared-proto Wire message ids + Tuning constants (net8.0)
infra/migrations    Hand-numbered idempotent SQL (001..004), no version table
infra/docker        docker-compose for local Postgres + world-server
```

## Data flow

```
mobile client ──auth (device UUID)──▶ gateway ──▶ postgres (character)
      │ WorldConnection (LiteNetLib/UDP)             │
      ▼                                              │
 world-server (Program.cs, top-level script)  ◀──────┘ REST: GET/PUT /characters,
      │                                                POST /voyage, /voyage/claim
      └── WorldStore ──▶ postgres (tile_diff, structure)
```

- **Client** predicts movement locally (`PlayerBody` + `sim-core.MovementRules`)
  and reports position; the server is the only thing that decides.
- **World-server** is one `Program.cs` top-level script: connection lifecycle,
  a `switch` over `MessageId` for every client request, and a `while` tick
  loop (15 Hz) that advances survival and broadcasts state. `Player.cs` holds
  per-connection authoritative state; `WorldStore.cs` is Postgres persistence
  for diffs/structures only (never full chunks — seed + diffs, invariant #2).
- **Gateway** is a second, independent process: ASP.NET minimal API, no
  world simulation. Owns `character` (inventory + 4 survival meters) and
  `voyage_ticket` (in-transit handoff between world-servers). Stateless
  world registry is a hardcoded `Dictionary` in `Program.cs` — fine for two
  hardcoded worlds, will not survive adding a third without a config source.
- **sim-core** is the shared-rule library, one static "Rules" class per
  concern: `MovementRules`, `HarvestRules`, `CraftingRules`, `PlacementRules`,
  `SurvivalRules`, `WorldClock`, and — as of tonight — `InterestRules`. This
  is the pattern to follow for new shared logic: pure static methods, tested
  in `tests/sim-core.tests`, no engine or network types.

## Protocol

`packages/shared-proto/Protocol.cs` is the single wire contract: one
`MessageId` enum, hand-documented layout per message, `ProtocolVersion.Current`
bumped on any layout change (currently 10) and checked at `Hello`. No
schema/codegen — payloads are written and read by hand on both sides in the
same order. This works at the current message count; if the message list
keeps growing, drifting hand-written read/write order is the likely next bug
class here (nothing catches a client and server that disagree on field order
except a runtime garbage-read).

## Persistence

- World state: `tile_diff` (upsert per tile, keyed by chunk+local coord) and
  `structure` (insert, id is app-assigned so it survives a restart). Replayed
  in full into memory at world-server startup — fine at current world sizes,
  will need paging/streaming once a world accumulates a very large diff count.
- Character state: one row per device UUID in the gateway's `character` table,
  inventory as JSONB keyed by `ItemId` enum *name* (not numeric value) —
  correct choice, since it survives `ItemId` renumbering.
- Migrations are hand-numbered SQL files (`001_init.sql` … `004_warmth.sql`),
  all idempotent (`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`).
  There is no migration-tracking table and no forward/back version recorded
  anywhere in the database — idempotency is what currently stands in for a
  real migration runner. Fine at 4 files; will not stay fine.

## What changed tonight

`InterestRules` (new, `packages/sim-core/InterestRules.cs`) plus the
world-server's per-tick player broadcast now filters `PlayerStates` to
players within `InterestRules.PlayerRadiusMetres` of the recipient, instead
of broadcasting every player's position to every other player unconditionally
every tick. See `LOG.md` for the full writeup — this was audit finding #1,
scored above the mandatory-fix threshold.

## Known debt (see BACKLOG.md for scored detail)

- `TileChanged` / `StructurePlaced` / `HarvestProgress` are still broadcast to
  every connected player regardless of distance — same invariant #6 gap as
  tonight's fix, just lower current severity because they're event-driven,
  not per-tick.
- `SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines) are both god
  scripts by the audit's own >400-line bar: HUD mixes meters, inventory grid,
  crafting/building sheet and hotbar in one `Control`; `World3D` mixes chunk
  building, foliage, structures and every `WorldConnection` event handler in
  one `Node3D`.
- `World.HasWarmthNear` is an O(structures) scan called once per connected
  player per tick — fine at today's structure counts, a candidate for a
  spatial index once bases get large.
- No integration test exercises two simulated clients against a live
  world-server; `tests/sim-core.tests` only covers the pure rule layer. The
  `InterestRules` unit tests added tonight cover the filtering logic in
  isolation, not the actual socket path.
- Gateway's world registry is a hardcoded `Dictionary` in `Program.cs` — a
  third world means editing gateway source, not config.

## Verification gap (be aware before trusting green)

This sandbox has **no `dotnet` SDK installed and no network path to install
one** (outbound to `builds.dotnet.microsoft.com` is denied by the environment
network policy — confirmed via the agent proxy status endpoint). Every change
in this repo, tonight included, is reviewed by hand against existing patterns
but has **not been compiled or run** in this environment. See LOG.md's
"Verified / Not verified" for exactly what that means for tonight's change.
