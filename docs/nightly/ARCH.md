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
                   Owns world state (terrain diffs, structures, build sites)
                   + in-memory per-connection Player state (position,
                   inventory, survival meters) for the duration of a session.
apps/gateway      ASP.NET Core minimal API, net10.0. Owns *character* state
                   (inventory + survival meters) in Postgres, keyed by a
                   client-generated device UUID. Also brokers voyage handoff
                   between world-servers (ticket mint/claim).
packages/sim-core Shared deterministic simulation: terrain generation, tile
                   rules, harvest/craft/place/movement/survival rules, item
                   catalog, world clock, building rules (`BuildSite`,
                   `BuildingRules`, `StructureCatalog`). Referenced by both
                   client (net8.0) and world-server (net10.0) so prediction
                   and authority can never disagree. This is the one place
                   that must stay platform/RNG/wall-clock free (see
                   determinism below).
packages/shared-proto  Wire message ids (`MessageId`) and DTOs shared between
                   client/world-server (UDP) and world-server/gateway (HTTP
                   JSON) — `CharacterState`, `VoyageGrant`, etc.
infra/docker      docker-compose for local Postgres (+ a world-server image).
infra/migrations  Hand-written, numbered SQL files, applied in order by being
                   mounted into the postgres image's initdb.d. No migration
                   *runner* — see Persistence notes below.
tests/sim-core.tests  The only automated test project in the repo. Covers
                   sim-core only: determinism, movement, crafting, placement,
                   survival, terrain shape, item catalog, world clock,
                   build sites/rules, structure catalog, and (as of
                   2026-07-31) the harvest strike cooldown.
```

## Data flow, one player session

1. Client connects UDP to a world-server, sends `Hello` (protocol version,
   device UUID, optional voyage ticket).
2. World-server, synchronously (blocking its one packet/tick thread — see
   Known debt): claims the character from the gateway
   (`POST /characters/{id}/claim`, atomic ownership + load), replies
   `Welcome` with spawn position, then backfills tile diffs, structures, and
   the player's own build sites (others' build sites backfill only their
   already-*built* pieces — the pending blueprint/stockpile stays private to
   the owner).
3. Client requests chunks around itself; world-server regenerates each chunk
   from `(seed, coord)` via `sim-core.TerrainGenerator`, layers any stored
   diffs on top, and sends `ChunkData`.
4. Client simulates its own movement/physics locally and reports position at
   `ClientStateHz`; world-server checks each report against
   `sim-core.MovementRules` (bounded by the height field, `WorldObstacles`
   for built walls, and a speed budget) and either accepts it or sends a
   `Correction`.
5. Gather/craft/place/eat/build are all client *requests*; the world-server
   re-derives the outcome from `sim-core` rules against its own authoritative
   state and only then mutates + broadcasts. The client never dictates a
   state change, only proposes one. As of 2026-07-31, harvesting
   (`ChopRequest`) is also rate-limited server-side
   (`HarvestRules.CanStrike`) — see this date's log entry.
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
   everywhere, including the non-voyage path. Note this claim is concurrency
   control, not authentication — see Known debt.

## Determinism

`sim-core` rule tables (`ItemCatalog.All`, `HarvestRules.Nodes`,
`CraftingRules.Recipes`, `StructureCatalog`) are already fixed-order static
arrays with a lookup dictionary built once at type-load — not switches,
contrary to what `docs/gameplay-roadmap.md` §3.1 currently implies is still
missing. That foundation piece is largely done; what's still genuinely
missing from the roadmap's §3 foundation list is the typed protocol
read/write helpers (§3.2), the entity/actor system (§3.3), and the client
decomposition (§3.4).

Terrain generation uses `Noise.Hash` (integer hashing), never platform RNG.
Verified: no `Dictionary`/`HashSet` iteration in `sim-core` currently
produces order-sensitive output — the tables above are arrays, and the
`ConcurrentDictionary`s in `World.cs` (diffs, structures, harvest strikes)
are only ever probed by key or iterated for existence checks / broadcasts,
never for a value that depends on enumeration order.

## Persistence

- World state: `world`, `tile_diff`, `structure`, and (as of the building
  system) `build_site`/`build_piece`/`build_storage` tables. Seed + diffs
  only — full chunks are never persisted (`packages/sim-core/World.cs`,
  `apps/world-server/WorldStore.cs`).
- Character state: `character` table (inventory JSONB + 4 survival meters +
  `owner_world_id`), owned by the gateway. `infra/migrations/002..005` show
  it's been extended repeatedly (voyage ownership, warmth, blueprints) via
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
  `BACKLOG.md`; a partial fix exists unmerged (PR #16).
- **`World3D.cs` (1103 lines) and `SurvivalHud.cs` (1124 lines)** are god
  scripts mixing net dispatch, terrain streaming, foliage generation,
  touch-input, harvest targeting, and (for the HUD) a full tabbed
  inventory/craft/build UI with no reusable widget. Matches
  `docs/gameplay-roadmap.md` §3.4, and has grown since that doc was written,
  not shrunk — the building/blueprint feature added its own dedicated files
  (`ArchitectCamera.cs`, `ArchitectController.cs`, `BlueprintPieces3D.cs`,
  `BlueprintView.cs`) rather than growing these two further on that front, so
  the remaining growth is concentrated in HUD panels and message dispatch.
- **`WorldConnection.cs`'s `OnReceive`** is a 12+-case hand-decoded switch on
  `MessageId` with no compile-time check that field order matches the
  server's writer — a reordering on either side is a silent wire bug.
- **`World3D.UpdateGatherPrompt`** runs a 7×7-tile noise-heavy scan every
  physics tick unconditionally (even while the action sheet is open), which
  matters on mobile battery/thermal even though it isn't a correctness bug.
- **No interest management on the `PlayerStates` broadcast** (found
  2026-07-31): every player's position goes to every player every tick,
  unconditionally, regardless of distance — an invariant-#6 gap distinct
  from the (already-filtered) chunk path. A fix is drafted but unmerged (PR
  #14). See `BACKLOG.md`.
- **Gateway character endpoints have no per-character secret** (found
  2026-07-31): the 2026-07-24 ownership-claim fix stops two worlds loading a
  character *concurrently*, but doesn't stop any caller who knows/guesses a
  device UUID from claiming or overwriting it — there's no authentication,
  only concurrency control. Deliberate for the current device-UUID model;
  flagged so it isn't mistaken for already handled.
- **Harvesting had no rate limit until 2026-07-31** — `ChopRequest` only
  checked reach, not time since the last strike, so a client sending it
  faster than a human taps could farm resources at unbounded speed. Fixed
  this date (`HarvestRules.CanStrike`, `Player.TryConsumeChopCooldown`).
  `CraftRequest`/`PlaceRequest` still have no equivalent, but both are
  bounded by inventory already held so the exploit potential is much lower.

## Target frameworks (unchanged, restated for quick reference)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. This is intentional (net10 referencing net8 libs works); don't
"fix" it.

## Build/test environment caveat

No session in this repo's history has had a working `dotnet` SDK or Docker
daemon available in its sandbox (the network policy blocks
`builds.dotnet.microsoft.com`, which would be needed to install one) — this
has been true on 2026-07-24, 2026-07-30, and 2026-07-31 sessions alike. All
review and all changes on this repo so far have been done by careful manual
reading, cross-referencing existing working code paths for idiom/pattern,
and (for at least one prior session) hand-running the exact SQL/logic
against a manually-started local Postgres. **Whoever reviews a nightly PR
must run `dotnet build` and `dotnet test tests/sim-core.tests` before
merging** — every single PR from this routine has shipped without that
having been confirmed. See each date's `LOG.md` entry for exactly what was
and wasn't verified that night.
