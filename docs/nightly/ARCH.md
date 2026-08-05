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
2. World-server claims the character from the gateway
   (`POST /characters/{id}/claim`, atomic ownership + load) on a background
   `Task`, not the packet/tick thread (see Concurrency below), replies
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
   character, then mint a single-use ticket via the gateway — both on a
   background `Task` now, ordered save-then-mint, same as before this was
   made non-blocking (see Concurrency below) — hand the ticket to the
   client, and drop the entity; world-server B claims the ticket on the
   client's `Hello` arrival. Character ownership (`character.owner_world_id`
   in Postgres) is the single source of truth for who may load a given
   character — see the July 24 log entry for why this used to be unenforced
   on a plain join and is now claimed atomically everywhere, including the
   non-voyage path.

## Concurrency

`world-server` is single-threaded by design (one packet/tick loop; see
Process map). Until 2026-08-05 the `Hello` and `RequestRelease` handlers
blocked that thread on the gateway HTTP round trip via
`.GetAwaiter().GetResult()`, so a slow or unreachable gateway stalled
*every* connected player's movement/harvest/tick broadcasts, not just the
joining/leaving one — the highest-scored finding in `BACKLOG.md`'s history.

The gateway call for both handlers now runs on a background `Task`; its
result is enqueued as a closure onto `pendingGatewayCompletions`
(`ConcurrentQueue<Action>`), drained once per tick right after
`server.PollEvents()`. Every closure — `CompleteJoin`/`RejectJoin`/
`CompleteRelease`/`DenyRelease` — runs exclusively on the tick thread, so
`players`, the reusable `writer` buffer, and `NetPeer.Send`/`Disconnect`
still have exactly one writer. Each checks `players.TryGetValue(peer, ...)`
before acting and no-ops if the peer already disconnected while the call was
in flight.

Two `Player` flags gate the window a claim/release is outstanding:
`JoinPending` (from `Hello` until the claim resolves — `Inventory`/
`Survival` are still defaults until then) and `Releasing` (from
`RequestRelease` until the save+voyage-grant resolves). While either is
true, `NetworkReceiveEvent`'s top-of-handler check silently drops every
other message from that one connection; other players are never gated.

A `claimingCharacters` reservation (`Dictionary<Guid, Player>`, separate
from `players`) closes a race a same-night independent review caught: a
peer that disconnects mid-claim is removed from `players` immediately, so
without this a fast reconnect for the same `CharacterId` would sail past
the duplicate-connection check and start a second, concurrent claim before
the first resolved. The reservation is released by whichever of
`RejectJoin`/`CompleteJoin`/a mid-join disconnect runs first, and is keyed
by the owning `Player` object so a claim that resolves late can't clear a
newer reservation for the same character.

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

- ~~World-server is single-threaded and blocks on gateway HTTP calls~~ —
  **fixed 2026-08-05**, see the Concurrency section above and `LOG.md`.
- **Building/blueprint/architect-mode system has no dedicated audit pass
  yet.** Landed entirely after the 2026-07-24 audit (`BuildSite`,
  `StructureCatalog`, blueprint protocol + persistence, architect-mode UI,
  roof shelter, collision) — real new server-authoritative surface
  (`BuildingRules.Validate`, four new message types, a new persistence
  table) that hasn't had a multiplayer-correctness pass. Flagged to
  `BACKLOG.md` for a future Phase 1.
- **`World3D.cs` (1103 lines, up from 735) and `SurvivalHud.cs` (1124 lines,
  up from 940)** are god scripts mixing net dispatch, terrain streaming,
  foliage generation, touch-input, harvest targeting, and (for the HUD) a
  full tabbed inventory/craft/build UI with no reusable widget. Matches
  `docs/gameplay-roadmap.md` §3.4, and keeps growing rather than shrinking —
  up roughly 50% each since the 2026-07-24 count.
- **`WorldConnection.cs`'s `OnReceive`** (now 534 lines total) is a
  hand-decoded switch on `MessageId` with no compile-time check that field
  order matches the server's writer — a reordering on either side is a
  silent wire bug.
- **`World3D.UpdateGatherPrompt`** runs a 7×7-tile noise-heavy scan every
  physics tick unconditionally (even while the action sheet is open), which
  matters on mobile battery/thermal even though it isn't a correctness bug.

## Target frameworks (unchanged, restated for quick reference)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. This is intentional (net10 referencing net8 libs works); don't
"fix" it.

## Build/test environment caveat

Two nightly sessions in a row (2026-07-24, 2026-08-05) have had **no working
`dotnet` SDK** — this time `apt-get install dotnet-sdk-10.0` was actually
available in the package index (unlike last time), but every `.deb` fetch
404'd against both `archive.ubuntu.com` and `security.ubuntu.com` through
the environment's proxy; same result for `dotnet-sdk-8.0`. **No Docker
daemon** either (CLI present, socket absent) — so nothing in this repo could
be compiled, run, or tested either night. All review and all changes were
done by careful manual reading, cross-referencing existing working code
paths for idiom/pattern, balance-checking braces/parens programmatically,
and — new this session — a second independent agent re-reading the full
diff cold (no context from the implementing session) specifically hunting
for races and compile errors, which did catch one real gap (see `LOG.md`).
That is a real substitute for careful review, not for a compiler: a human
must run `dotnet build` and `dotnet test tests/sim-core.tests` before
trusting either night's diff. If this keeps recurring, it's worth someone
checking whether the sandboxed network policy can allow the Ubuntu package
mirrors `dotnet-install.sh` needs, or pre-baking an SDK into the session
image, so a third night doesn't hit the exact same wall.
