# Ashfall — architecture notes (nightly-maintained)

Honest, current-state map of the codebase for the overnight audit routine.
Companion to `docs/architecture.md` (which owns the *decided* invariants) and
`docs/gameplay-roadmap.md` (which owns *what to build*); this file owns
*what's actually here right now*, kept current by each nightly session. Don't
duplicate the invariants doc here; link to it.

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
                   between world-servers (ticket mint/claim). Every route but
                   `/health` and `/worlds` requires a shared-secret header
                   (`X-Ashfall-Key` / `ASHFALL_GATEWAY_KEY`) — see Trust
                   boundaries below.
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
                   The gateway is not containerized yet — run via `dotnet run`.
infra/migrations  Hand-written, numbered SQL files, applied in order by being
                   mounted into the postgres image's initdb.d. No migration
                   *runner* — see Persistence notes below.
tests/sim-core.tests  The only automated test project in the repo. Covers
                   sim-core only: determinism, movement, crafting, placement,
                   survival, terrain shape, item catalog, world clock,
                   building. Nothing exercises `apps/gateway` or
                   `apps/world-server` — see Known debt.
```

### File sizes (client + server, ~5,600+ lines of C# excluding tests)

Called out because two of these keep coming up in nightly audits:

```
apps/client/scripts/world3d/SurvivalHud.cs   940  god-script — every HUD panel
                                                    (meters, hotbar, craft,
                                                    build, items, travel) in
                                                    one file, no shared widget
apps/client/scripts/world3d/World3D.cs       735  god-script — net dispatch +
                                                    terrain streaming + foliage
                                                    gen + interaction/targeting
apps/client/scripts/WorldConnection.cs       421  hand-decoded switch on
                                                    MessageId, no compile-time
                                                    layout check vs. the server
apps/world-server/Program.cs                 700+ top-level statements; every
                                                    message handler in one
                                                    switch, blocks on gateway
                                                    HTTP calls (see Known debt)
```

Both `SurvivalHud.cs`/`World3D.cs` are already named in
`docs/gameplay-roadmap.md` §3.4 as needing a split, and have grown every time
a nightly session has measured them (`SurvivalHud.cs` was 419 lines when that
roadmap section was written; `World3D.cs` was 616).

## Data flow, one player session

1. Client connects UDP to a world-server, sends `Hello` (protocol version,
   device UUID, optional voyage ticket).
2. World-server, synchronously (blocking its one packet/tick thread — see
   Known debt): claims the character from the gateway
   (`POST /characters/{id}/claim`, atomic ownership + load, gated by the
   `X-Ashfall-Key` shared secret), replies `Welcome` with spawn position,
   then backfills existing tile diffs and structures.
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
   who may load a given character — enforced on *every* load path (voyage
   and plain join alike) since the 2026-07-24 session, and every gateway
   route that can move that ownership around has required a caller to prove
   it's a world-server, not a player's device, since 2026-08-22.

## Determinism

`sim-core` rule tables (`ItemCatalog.All`, `HarvestRules.Nodes`,
`CraftingRules.Recipes`) are already fixed-order static arrays with a
lookup dictionary built once at type-load — not switches, contrary to what
`docs/gameplay-roadmap.md` §3.1 currently implies is still missing. That
foundation piece is largely done; what's still genuinely missing from the
roadmap's §3 foundation list is the typed protocol read/write helpers
(§3.2), the entity/actor system with interest management (§3.3), and the
client decomposition (§3.4).

Terrain generation uses `Noise.Hash` (integer hashing), never platform RNG.
No `Dictionary`/`HashSet` iteration in `sim-core` currently produces
order-sensitive output — the rule tables above are arrays, and the
`ConcurrentDictionary`s in `World.cs` (diffs, structures, harvest strikes)
are only ever probed by key or iterated for existence checks / broadcasts,
never for a value that depends on enumeration order.

## Persistence

- World state: `world`, `tile_diff`, `structure`, and build-site tables. Seed
  + diffs only — full chunks are never persisted (`packages/sim-core/World.cs`,
  `apps/world-server/WorldStore.cs`).
- Character state: `character` table (inventory JSONB + 4 survival meters +
  `owner_world_id`), owned by the gateway. `infra/migrations/002..004` show
  it's been extended twice already (voyage ownership, warmth) via
  additive `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` with a `DEFAULT`, which
  is a *de facto* migration convention even though there's no formal
  versioned-migration runner — anyone changing this schema should keep
  following that additive/defaulted pattern so old rows stay valid.
  `CharacterState` itself carries no explicit version field; safe so far
  because every change has been additive with a safe default.
- No explicit schema version field anywhere in the migration set either.
  Given migrations are just numbered files applied once at container init
  (not replayed against a live DB), a second migration runner (Flyway, DbUp,
  or even a hand-rolled `schema_version` table) would be needed before this
  can survive a real production deploy with existing data — flagged in
  `BACKLOG.md`, not urgent pre-alpha.

## Trust boundaries

- **Client → world-server**: UDP, gated by a connect key
  (`ASHFALL_CONNECT_KEY`, default `"ashfall"`) and a protocol version check.
  Everything the client sends is treated as a request, validated against
  `sim-core` rules before it changes any authoritative state.
- **World-server → gateway**: HTTP, gated by a shared secret
  (`ASHFALL_GATEWAY_KEY`, default `"ashfall"`) since 2026-08-22 — every route
  but `/health` and `/worlds` requires an `X-Ashfall-Key` header matching it.
  Before that date this was **completely open**: any HTTP caller who knew a
  character's device UUID (which every player already knows — it's stored
  client-side, `user://character_id`) could set that character's inventory
  and survival meters directly via `PUT /characters/{id}`, bypassing every
  harvest/craft/eat check the world-server enforces. See `LOG.md`
  2026-08-22 for the full writeup.
- Both default keys are the literal string `"ashfall"`, and that default is
  public — it's in this open-source repo, so it provides no protection by
  itself. It only stops an unmodified client/curl call from working by
  accident, and gives operators a single env var to set (`ASHFALL_CONNECT_KEY`,
  `ASHFALL_GATEWAY_KEY`) before exposing either service beyond localhost. The
  gateway warns loudly on boot if still running with the default key; the
  world-server does not warn at all yet for its connect key — worth adding if
  a future night is looking for something small to pair with this.

## Known debt (see `BACKLOG.md` for scored, actionable entries)

- **World-server is single-threaded and blocks on gateway HTTP calls** inside
  the packet-receive handler (`Hello`, `RequestRelease`) via
  `.GetAwaiter().GetResult()`. A slow or unreachable gateway stalls *every*
  connected player's movement/harvest/tick processing for the duration of the
  call, on every join and every voyage. Highest-scored open item in
  `BACKLOG.md` (20) — not fixed yet because it needs a real architecture
  change (a pending-join queue drained from the tick loop), not a one-night
  patch.
- **No per-action rate limiting** on `ChopRequest`/`CraftRequest`/`PlaceRequest`
  beyond the reach check — a modified client can gather far faster than the
  harvest-strike pacing implies.
- **No server-side interest management for entities**: `PlayerStates` and
  `StatsUpdate` broadcast to *every* connected player every tick/heartbeat,
  not just nearby ones. `Tuning.InterestRadiusChunks` exists but nothing
  reads it for entity broadcast (chunk streaming itself is already pull-based
  via `RequestChunk`, so terrain is fine). This is invariant #6 on paper, not
  in code yet — tracked as planned work in `docs/gameplay-roadmap.md` §3.3
  (build the entity/actor system with interest management, make players the
  first entity kind through it). Low real impact while worlds hold a handful
  of players; will start costing real bandwidth/CPU once that changes.
- **`SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines)** are god
  scripts — see "File sizes" above. Matches `docs/gameplay-roadmap.md` §3.4,
  and has grown since that doc was written, not shrunk.
- **`WorldConnection.cs`'s `OnReceive`** is a 12-case hand-decoded switch on
  `MessageId` with no compile-time check that field order matches the
  server's writer — a reordering on either side is a silent wire bug.
- **`World3D.UpdateGatherPrompt`** runs a 7×7-tile noise-heavy scan every
  physics tick unconditionally (even while the action sheet is open), which
  matters on mobile battery/thermal even though it isn't a correctness bug.
- **Migrations are apply-once**: `infra/migrations/*.sql` only run
  automatically via `docker-entrypoint-initdb.d`, which Postgres only
  executes against an *empty* data volume. An already-running deployment
  needs someone to apply new `.sql` files by hand; there's no migration
  runner or `schema_version` table. The migrations themselves are safely
  idempotent, so this is an operational trap, not a data-loss risk.
- **No automated test coverage at all for `apps/gateway` or
  `apps/world-server`** — only `sim-core` has a test project. Both nights so
  far that touched gateway auth/ownership code (2026-07-24's ownership claim,
  2026-08-22's shared-secret gate) verified by manual review only, with no
  working `dotnet` in the sandbox either time (see below).

## Target frameworks (unchanged, restated for quick reference)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. This is intentional (net10 referencing net8 libs works); don't
"fix" it.

## Build/test environment caveat

**Every nightly session so far (2026-07-24 and 2026-08-22) has run in a
sandbox with no working `dotnet` SDK** — not installed, and in at least one
case the network policy blocked fetching one. Neither session could compile,
run, or test anything in this repo; both did all review and all changes by
careful manual reading and cross-referencing existing working code for
idiom/pattern. This is now a pattern, not a one-off — if nightly sessions are
meant to keep running unsupervised, whatever provisions the sandbox needs the
.NET 10 SDK (and ideally a Postgres reachable via Docker) preinstalled, or
every future session will keep shipping code nobody — including the session
that wrote it — has ever run. A human must run `dotnet build` and
`dotnet test tests/sim-core.tests` on any nightly branch before trusting it.
See `LOG.md` for exactly what is and isn't verified per session.
