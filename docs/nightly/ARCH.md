# Ashfall — architecture notes (nightly-maintained)

Honest, current-state map of the codebase for the overnight audit routine.
Companion to `docs/architecture.md` (which owns the *decided* invariants) and
`docs/gameplay-roadmap.md` (which owns *what to build*); this file owns
*what's actually here right now*, kept current by each nightly session.

## Process map

```
apps/client       Godot 4 mobile client, C#, net8.0 — third-person 3D, low-res
                   pixel-art render, touch UI. Connects to one world-server
                   over UDP (LiteNetLib).
apps/world-server Headless, single-threaded authoritative server, net10.0.
                   One process = one bounded region ("continent-a", …).
                   Owns world state (terrain diffs, structures) + in-memory
                   per-connection Player state (position, inventory, survival
                   meters) for the duration of a session.
apps/gateway      ASP.NET Core minimal API, net10.0. Owns *character* state
                   (inventory + survival meters) in Postgres, keyed by a
                   client-generated device UUID. Also brokers voyage handoff
                   between world-servers (ticket mint/claim).
packages/sim-core Shared deterministic simulation: terrain generation, tile
                   rules, harvest/craft/place/movement/survival rules, item
                   catalog, world clock. Referenced by both client (net8.0)
                   and world-server (net10.0) so prediction and authority can
                   never disagree. This is the one place that must stay
                   platform/RNG/wall-clock free (see determinism below).
packages/shared-proto  Wire message ids (`MessageId`) and DTOs shared between
                   client/world-server (UDP) and world-server/gateway (HTTP
                   JSON) — `CharacterState`, `VoyageGrant`, etc.
infra/docker      docker-compose for local Postgres (+ a world-server image).
infra/migrations  Hand-written, numbered SQL files, applied in order by being
                   mounted into the postgres image's initdb.d. No migration
                   *runner* — see Persistence notes below.
tests/sim-core.tests  The only automated test project in the repo. Covers
                   sim-core only: determinism, movement, crafting, placement,
                   survival, terrain shape, item catalog, world clock.
```

## Data flow, one player session

1. Client connects UDP to a world-server, sends `Hello` (protocol version,
   device UUID, optional voyage ticket).
2. World-server, synchronously (blocking its one packet/tick thread — see
   Known debt): claims the character from the gateway
   (`POST /characters/{id}/claim`, atomic ownership + load), replies
   `Welcome` with spawn position, then backfills existing structures.
3. Client requests chunks around itself; world-server regenerates each chunk
   from `(seed, coord)` via `sim-core.TerrainGenerator`, layers any stored
   diffs on top, and sends `ChunkData`.
4. Client simulates its own movement/physics locally and reports position at
   `ClientStateHz`; world-server checks each report against
   `sim-core.MovementRules` (bounded by the height field + a speed budget)
   and either accepts it or sends a `Correction`.
5. Gather/craft/place/eat are all client *requests*; the world-server
   re-derives the outcome from `sim-core` rules against its own authoritative
   state and only then mutates + broadcasts. The client never dictates a
   state change, only proposes one.
6. On disconnect, the world-server fire-and-forgets a character save to the
   gateway (in-memory state is already authoritative and snapshotted
   synchronously before the async write, so this doesn't risk a torn read).
7. A voyage (`RequestVoyage` → `RequestRelease`) has world-server A save the
   character synchronously, mint a single-use ticket via the gateway, hand
   the ticket to the client, and drop the entity; world-server B claims the
   ticket on the client's `Hello` arrival. Character ownership
   (`character.owner_world_id` in Postgres) is the single source of truth for
   who may load a given character — see the July 24 log entry for why this
   used to be unenforced on a plain join and is now claimed atomically
   everywhere, including the non-voyage path.

## Determinism

`sim-core` rule tables (`ItemCatalog.All`, `HarvestRules.Nodes`,
`CraftingRules.Recipes`) are already fixed-order static arrays with a
lookup dictionary built once at type-load — not switches, contrary to what
`docs/gameplay-roadmap.md` §3.1 currently implies is still missing. That
foundation piece is largely done; what's still genuinely missing from the
roadmap's §3 foundation list is the typed protocol read/write helpers
(§3.2), the entity/actor system (§3.3), and the client decomposition (§3.4).

Terrain generation uses `Noise.Hash` (integer hashing), never platform RNG.
Verified: no `Dictionary`/`HashSet` iteration in `sim-core` currently
produces order-sensitive output — the tables above are arrays, and the
`ConcurrentDictionary`s in `World.cs` (diffs, structures, harvest strikes)
are only ever probed by key or iterated for existence checks / broadcasts,
never for a value that depends on enumeration order.

## Persistence

- World state: `world`, `tile_diff`, `structure` tables. Seed + diffs only —
  full chunks are never persisted (`packages/sim-core/World.cs`,
  `apps/world-server/WorldStore.cs`).
- Character state: `character` table (inventory JSONB + 4 survival meters +
  `owner_world_id`), owned by the gateway. `infra/migrations/002..004` show
  it's been extended twice already (voyage ownership, warmth) via
  additive `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` with a `DEFAULT`, which
  is a *de facto* migration convention even though there's no formal
  versioned-migration runner — anyone changing this schema should keep
  following that additive/defaulted pattern so old rows stay valid.
- No explicit schema version field anywhere. Given migrations are just
  numbered files applied once at container init (not replayed against a live
  DB), a second migration runner (Flyway/DbUp/etc.) would be needed before
  this can survive a real production deploy with existing data — flagged to
  `BACKLOG.md`, not urgent pre-alpha.

## Known debt (see `BACKLOG.md` for scored, actionable entries)

- **World-server is single-threaded and blocks on gateway HTTP calls** inside
  the packet-receive handler (`Hello`, `RequestRelease`) via
  `.GetAwaiter().GetResult()`. A slow or unreachable gateway stalls
  *every* connected player's movement/harvest/tick processing for the
  duration of the call, on every join and every voyage. Scored in
  `BACKLOG.md`; not fixed tonight because it needs a real architecture
  change (a pending-join queue drained from the tick loop) — bigger blast
  radius to get wrong for less certain gain than the ownership bug, which is
  a five-minute-round-trip data bug that's already exploitable today.
- **`World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines)** are god
  scripts mixing net dispatch, terrain streaming, foliage generation,
  touch-input, harvest targeting, and (for the HUD) a full tabbed
  inventory/craft/build UI with no reusable widget. Matches
  `docs/gameplay-roadmap.md` §3.4, and has grown since that doc was written,
  not shrunk.
- **`WorldConnection.cs`'s `OnReceive`** is a 12-case hand-decoded switch on
  `MessageId` with no compile-time check that field order matches the
  server's writer — a reordering on either side is a silent wire bug.
- **`World3D.UpdateGatherPrompt`** runs a 7×7-tile noise-heavy scan every
  physics tick unconditionally (even while the action sheet is open), which
  matters on mobile battery/thermal even though it isn't a correctness bug.

## Target frameworks (unchanged, restated for quick reference)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. This is intentional (net10 referencing net8 libs works); don't
"fix" it.

## Build/test environment caveat

This session had **no working `dotnet` SDK** (not installed, and the
network policy blocks `builds.dotnet.microsoft.com` needed to install one)
and **no Docker daemon** (docker CLI present, socket absent) — so nothing
in this repo could be compiled, run, or tested tonight. All review and all
changes were done by careful manual reading, cross-referencing existing
working code paths for idiom/pattern, and balance-checking braces/parens.
A human must run `dotnet build` and `dotnet test tests/sim-core.tests`
before trusting tonight's diff. See `LOG.md` for exactly what is and isn't
verified.

## Update — 2026-08-25

A lot landed between the last audit and this one: a full blueprint/architect
building system (`packages/sim-core/{BuildPieces,BuildSite,BuildingRules,
StructureCatalog}.cs`, `apps/world-server/WorldObstacles.cs`, the client's
`Architect*`/`Blueprint*` scripts), collision (trees/walls/structures now
block movement — `MovementRules.IMovementObstacles`), an animated character
body, and health regen. `apps/world-server/Program.cs` alone grew from 468
to 759 lines. The repo map and known-debt list above are from before this —
worth a proper re-measure next session rather than patched piecemeal here.

**Reviewed the new server-authoritative surface specifically** (this is
where a new multiplayer-correctness bug would hide): `BuildingRules.Validate`
rejects an illegal plan server-side before a `BuildSite` is even created;
`BuildSite.TryBuildAt`/`TryBuildNext` re-check support and stockpile before
every strike, not just at commit; `CommitBlueprint`/`DepositRequest`/
`BuildRequest`/`CancelBlueprint` in `Program.cs` all check `site.Owner ==
player.CharacterId` and reach (`IsWithinReach`) before acting, matching the
existing harvest/place pattern exactly. `MovementRules.Check` grew an
`IMovementObstacles` parameter shared by client prediction and server
authority, with wall-edge blocking deliberately limited to orthogonally
adjacent single-tile steps (a wider jump is still bounded by the destination
tile's own walkability check) — reasoned, not accidental. Found nothing
wrong with any of it.

**What tonight actually fixed** is unrelated to the new features directly,
but was found while reading the new `CommitBlueprint` handler: see `LOG.md`
2026-08-25 and `BACKLOG.md`. Short version — `apps/world-server/Program.cs`'s
message dispatch had no top-level exception guard, so any malformed/short
packet on *any* message type could throw an unhandled exception straight
out of the single tick loop and crash the whole process for every connected
player. `CommitBlueprint` is what made it obvious (it's the first message
with a client-declared loop count read field-by-field), but the exposure was
already there on every existing handler. Now caught, logged, and the
offending peer disconnected — nothing else changed.
