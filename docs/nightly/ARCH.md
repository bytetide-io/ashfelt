# Ashfall — architecture map (nightly baseline)

Written the first night the nightly process ran, from a cold read of the repo.
This is a map for the next unsupervised session, not a design doc — see
`docs/architecture.md` and `docs/voyage-transfer.md` for the decided, load-bearing
shape. Update this file when a night's work changes what's below; don't let it
go stale.

## Repo shape (line counts, ~6,500 LOC total excluding tests)

```
apps/client         Godot 4 client (C#, net8.0)     ~3,360 LOC
apps/world-server    UDP world simulation (net10.0)    ~910 LOC
apps/gateway         Auth/character/voyage (net10.0)   ~210 LOC
packages/sim-core    Shared deterministic rules        ~970 LOC (+ ActionThrottle tonight)
packages/shared-proto Wire messages + tuning constants  ~180 LOC
tests/sim-core.tests xUnit, sim-core only               9 files
infra/migrations     4 additive SQL files, no runner/version table
```

No test project exists for `apps/world-server` or `apps/gateway` — the only
automated coverage is `sim-core`. CI (`.github/workflows/ci.yml`) builds all
four .NET projects and runs `dotnet test tests/sim-core.tests`, plus a
separate Godot import/build job for the client.

## Runtime shape

One gateway (ASP.NET minimal API + Postgres) owns accounts/characters and a
static in-memory registry of world-servers (`continent-a`, `continent-b` —
hardcoded in `apps/gateway/Program.cs`, not yet dynamic registration). Each
world-server is a single-threaded event loop (LiteNetLib) at 15 Hz, one
process per bounded region, each with its own Postgres connection for
tile-diff/structure persistence. The client is a Godot 4 (.NET/Mono) app that
renders a low-res `SubViewport` for a "real 3D, reads as pixel art" look
(see `docs/architecture.md` for why).

**Note on `CLAUDE.md` vs. the actual client:** the working-agreement doc at
the repo root still describes Ashfall as "2D pixel top-down." The client
pivoted to third-person 3D with physics-based movement (documented, decided,
in `docs/architecture.md`), and that pivot is real and shipped. `CLAUDE.md`'s
opening line has not been updated to match. Flagged, not fixed tonight — that
file is the standing work agreement and shouldn't be edited unsupervised
without the human noticing the diff. See `BACKLOG.md`.

## Message flow, one world-server tick

```
client ── Hello(uuid, protoVer, voyageTicket?) ──▶ world-server
                                                        │ claims ticket (if voyaging) via gateway
                                                        │ GetCharacterAsync (blocking, at Hello only)
       ◀── Welcome(seed, chunkSize, spawn) ─────────────┘
       ◀── StructurePlaced × N (backfill) ───────────────
client ── RequestChunk(coord) ──▶ world-server ── ChunkData ──▶ client
client ── ClientState(pos, yaw) @15Hz ──▶ MovementRules.Check ── Correction? ──▶ client
client ── ChopRequest/CraftRequest/PlaceRequest/EatRequest ──▶ sim-core rules ──▶ Broadcast/Update
world-server (every tick) ── PlayerStates (unreliable) ──▶ ALL connected peers
world-server (every 2s or on-change) ── StatsUpdate / InventoryUpdate ──▶ owning peer
```

Every economy-affecting request (chop/craft/place/eat) is validated against
shared `sim-core` rules that both client and server reference — client-side
prediction and server authority read the same logic, per invariant #1/#4.

## Persistence model

- **World state** = seed (int) + `tile_diff` rows (chunk-keyed upsert) +
  `structure` rows. Terrain itself is never stored — replayed from
  `TerrainGenerator` on boot (invariant #2). Loading is a full-table scan per
  world-server boot (`SELECT ... WHERE world_id = $1`, no pagination); fine at
  current scale, will need attention once diff counts grow into the millions.
- **Character state** (inventory + 4 survival meters) lives only in the
  gateway's `character` table, JSONB inventory keyed by stable `ItemId` enum
  names (invariant #3). No schema version column, but every migration to date
  has been an additive `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` with a
  default, so old rows load under the new shape without an explicit migration
  step. That pattern is doing the job of a version field by convention, not
  by enforcement — nothing stops a future migration from being non-additive
  without anyone noticing until a load throws.
- **Voyage handoff**: `character.owner_world_id` plus a single-use
  `voyage_ticket` row (PK'd by character, so re-minting overwrites a stale
  ticket rather than accumulating). Expiry self-heals ownership back to the
  origin world with no operator step — read `ReclaimExpiredTicketAsync` and
  `docs/voyage-transfer.md` together, they agree.

## Anti-cheat / authority surface

Two independent gates, both shared with the client for prediction:

1. **Movement** (`sim-core/MovementRules`) — bounds speed, rise/fall rate, and
   height-above/below-terrain per elapsed real time since the *last accepted*
   position (not last reported), so spamming `ClientState` buys nothing.
   Well tested (`MovementRulesTests.cs`).
2. **Actions** (`sim-core/ActionThrottle`, added tonight) — a flat minimum
   interval between accepted `ChopRequest`/`CraftRequest`/`PlaceRequest`/
   `EatRequest` per player. Before tonight this gate did not exist: every
   accepted chop strike unconditionally yielded its item regardless of request
   rate, so a client that skipped the tap gesture and blasted packets could
   farm any resource at wire speed. See `LOG.md` for the full writeup.

What is *not* gated: `RequestChunk`. A peer can request arbitrary chunk
coordinates at any rate; each one costs a `TerrainGenerator` regen plus a
full-chunk packet. Not an economy exploit (chunks carry no player-mutable
state beyond diffs already applied), but it is an uncosted CPU/bandwidth
sink — see `BACKLOG.md`.

## Interest management — documented invariant, not yet implemented

`CLAUDE.md` invariant #6 and `docs/architecture.md` invariant 5 both state "a
client only ever receives entities/chunks near it." In the current code,
`PlayerStates` (every tick), `TileChanged`, `HarvestProgress`, and
`StructurePlaced` are all sent via `Broadcast()`, which iterates every
connected peer on the world-server with no distance filter. `RequestChunk` is
the only spatially-scoped message, and it's client-pulled, not server-pushed.
This is invisible today because a bounded region with a handful of playtesters
never approaches the bandwidth this would cost at real population — but it is
a real gap against a documented invariant, not a stylistic one. Logged to
`BACKLOG.md` rather than fixed tonight (out of scope for a single-session
change; touches every broadcast call site).

## Client structure

`apps/client/scripts/world3d/SurvivalHud.cs` (940 lines) is the largest file
in the repo by a wide margin and the clearest "knows about more than three
systems" candidate: it owns the HUD chrome, inventory list, crafting panel,
building placement UI, hotbar, and the day/night chip. `World3D.cs` (735
lines) is the scene root — input routing, gather-target resolution, and
wiring the HUD's events to `WorldConnection`. Neither is unmaintainable today,
but both are the first place a "god script" complaint will land as more
systems (combat, more crafting tabs, trading) get added. Logged to
`BACKLOG.md`.

## Determinism contract

`tests/sim-core.tests/DeterminismTests.cs` is the compatibility contract
called out in `CLAUDE.md` — treat a failing determinism test as "this change
breaks every existing world," never as a test to update. `Noise.Hash` is the
only randomness source in `sim-core`; no `float` accumulation, no platform
RNG, no `DateTime.Now` found anywhere in `packages/sim-core` on this read.

## Target frameworks (as declared, matches `CLAUDE.md`)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0, referencing the net8 packages intentionally.
