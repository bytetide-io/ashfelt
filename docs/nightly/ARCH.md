# Ashfall — nightly engineer's architecture map

Written by the first nightly session (2026-07-30). This is a working map for
whoever runs the next unsupervised session — an honest snapshot of where the
debt is, not a design doc. `docs/architecture.md` and
`docs/gameplay-roadmap.md` are the authoritative sources for vision and
decisions; this file exists so a future night doesn't have to re-derive the
codebase's shape from scratch.

## Layout (see also root README.md)

```
apps/client         Godot 4 mobile client (C#, net8.0)
apps/gateway        Auth, character storage, voyage routing (C#, net10.0, ASP.NET minimal API)
apps/world-server   Headless authoritative UDP server (C#, net10.0, LiteNetLib)
packages/sim-core   Shared deterministic simulation (net8.0) — terrain, harvest, craft, placement, survival rules
packages/shared-proto  Wire message defs + Tuning constants (net8.0)
infra/docker        docker-compose for local Postgres + world-server
infra/migrations    SQL schema, applied in order 001..004
tests/sim-core.tests  The only test project in the repo
```

**No test project exists for `apps/gateway` or `apps/world-server`.** Anything
touching netcode or persistence is currently verified by hand (manual
multi-client play, or — as of tonight — direct SQL against a scratch Postgres
instance) rather than CI. This is a standing gap; see BACKLOG.md.

## world-server (`apps/world-server/Program.cs`, ~430 lines)

Single-threaded: `NetManager.PollEvents()` and the tick loop share one thread,
so there's no data race *within* the process on two messages touching the
same tile/craft/inventory in the same instant. `Player.cs` holds per-connection
state; `WorldStore.cs` persists diffs/structures to Postgres; `GatewayClient.cs`
is the REST client to the gateway for character load/save/voyage.

- Movement is properly server-validated: the client predicts, `MovementRules.Check`
  (sim-core) bounds what the server will accept, `Player.TryAccept` corrects.
- Inventory/crafting/harvest are entirely server-evaluated with loud
  invariant-violation throws, not silent clamps — good.
- **Character ownership across the join path was unenforced until tonight**
  (see LOG.md 2026-07-30) — fixed via an atomic gateway-side claim plus an
  in-process duplicate-session evictor.
- **No interest management** (invariant #6 is written down but not built):
  `PlayerStates`, `StructurePlaced`, `TileChanged`, `HarvestProgress` all
  broadcast to every connected peer regardless of distance. Fine at today's
  tiny playtest population; will not scale — see BACKLOG.md for the
  bandwidth math.
- Blocking gateway calls (`.GetAwaiter().GetResult()`) run straight on the
  single event-processing thread with no `HttpClient` timeout configured — one
  slow gateway round trip stalls every connected player's tick.

## gateway (`apps/gateway/Program.cs`, ~250 lines)

ASP.NET minimal API, Postgres via Npgsql, `EnableDynamicJson` for the JSONB
inventory column. All SQL is parameterized — no injection surface found.
The voyage ticket mechanism (`/voyage`, `/voyage/claim`) is solid: single-use
consumption via `DELETE ... RETURNING` inside a CTE, TTL self-heal for a
stranded ticket. As of tonight, `GET`/`PUT /characters/{id}` also take a
`worldId` and participate in the same ownership model (see `API.md`).

No schema/format version field exists on any table. `CharacterState.Inventory`
is keyed by `ItemId` enum *name* specifically so a wire renumber doesn't
corrupt a stack — a real, working piece of forward-compatibility design — but
an enum *rename or removal* is still silently dropped on load
(`Player.LoadCharacter`), not migrated or flagged.

## sim-core (`packages/sim-core/`, ~970 lines across 11 files)

Clean composition-over-inheritance: `ItemDef`/`HarvestNodeDef`/`Recipe` are
flat `readonly record struct` tables read by static rule classes
(`ItemCatalog`, `HarvestRules`, `CraftingRules`, `PlacementRules`,
`SurvivalRules`). No `System.Random`, `DateTime.Now`, or float-accumulation
anywhere — genuinely deterministic. `Noise.Hash` is the sole entropy source.

**The compatibility contract has a hole**: `DeterminismTests.cs` only checks
internal self-consistency (instance vs instance, tile vs chunk) — it never
pins a golden/frozen value for a fixed seed. A changed `Noise.Hash` constant
or terrain threshold passes the whole suite while reshaping every persisted
world. This is the highest-scored open finding; see BACKLOG.md #1.

There is no single `Tuning` file inside sim-core — despite the name, balance
numbers live scattered across `TerrainGenerator.cs`, `HarvestRules.cs`,
`CraftingRules.cs`, `ItemCatalog.cs`, `SurvivalRules.cs`, and a *different*
`Tuning` class in `packages/shared-proto/Protocol.cs:142` (tick rate, day
length). `docs/gameplay-roadmap.md` §3.5 already calls for consolidating this.

## client (`apps/client/scripts/`)

The two largest files have grown well past what `docs/gameplay-roadmap.md`
§3.4 measured them at, and none of that doc's recommended splits have
happened yet:

| File | Lines now | Roadmap's earlier count |
|---|---|---|
| `world3d/SurvivalHud.cs` | 940 | 419 |
| `world3d/World3D.cs` | 735 | 616 |
| `WorldConnection.cs` | 421 | — |
| `ui/DesignSystem.cs` | 252 | — |

`World3D.cs` mixes network dispatch, terrain/foliage mesh generation,
day-night lighting, touch input/gather-targeting, and structure rendering in
one `Node3D`. `WorldConnection.OnReceive` dispatches on `MessageId` via a
13-case `switch`, not a table. Two sibling nodes reach each other via relative
`GetNode("../X")` paths (`PlayerBody.cs:46`, `OrbitCamera.cs:29`) instead of
exported references or signals.

What's actually solid, confirmed by direct read: no per-frame allocations in
any `_Process`/`_PhysicsProcess`; every dynamic-child rebuild correctly
`QueueFree()`s before replacing (`RefreshHotbar`/`RefreshGrid`/`RefreshTravel`,
`RemotePlayers.Remove/Clear`) — no leaks or orphans found; rendering is
mobile-conscious (MSAA off, mobile renderer, half-res `SubViewport`,
`MultiMesh`-instanced foliage); sim rules are referenced from `sim-core`, never
duplicated client-side.

## Known-good invariants, verified tonight

- `sim-core` determinism (no RNG/clock/float-accumulation) — confirmed by
  direct read of every file in the package.
- SQL parameterization across gateway and `WorldStore` — no string-built
  queries anywhere.
- Seed+diffs world storage — chunks are never persisted; diffs/structures
  reload correctly on restart; in-progress harvest strikes are deliberately
  memory-only (documented trade-off, not a bug).

## Standing gap for future nights

No CI or test project exercises `apps/gateway` or `apps/world-server`. Tonight's
verification of the ownership-claim fix was done by hand: real SQL run against
a local Postgres instance (this sandbox has `postgresql-16` installed but no
`dotnet` SDK and no running Docker daemon, so the C# itself could not be
compiled or executed — see LOG.md "Not verified"). A `WebApplicationFactory`-based
integration test project for the gateway would have caught regressions here
automatically; consider it before the next ownership-adjacent change.
