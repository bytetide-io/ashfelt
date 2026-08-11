# Ashfall — architecture notes (nightly-crew view)

Honest, current-state map of the codebase, written for the unsupervised nightly
process. `docs/architecture.md` and `docs/voyage-transfer.md` are the design
source of truth (invariants, decided-vs-deferred); this file is "what's
actually there tonight," including debt. Keep it updated as nights land.

## Note on drift between CLAUDE.md/README title and reality

`CLAUDE.md` and the top of `README.md` both describe Ashfall as a "2D pixel
top-down survival game." `docs/architecture.md` and the rest of `README.md`
describe (and the client code implements) a **third-person 3D game with
physics-based movement, viewed through a low-res `SubViewport` for a pixel-art
look.** The 3D description matches the actual client code
(`apps/client/scripts/world3d/*`). This is pre-existing doc drift, not
something tonight's session introduced or resolved — flagging it here so a
future night (or a human) fixes the stale "2D top-down" line rather than
re-deriving this confusion from scratch.

## Layout

```
apps/client         Godot 4 mobile client, C#, net8.0
apps/world-server   Headless authoritative UDP server (LiteNetLib), net10.0 — one instance = one region/world
apps/gateway        ASP.NET minimal API: auth-free character storage + voyage routing, net10.0
packages/sim-core    Shared deterministic rules: terrain, movement, harvest, craft, placement, survival, clock. net8.0.
packages/shared-proto Wire message IDs + Tuning constants shared by client/server/gateway. net8.0.
infra/docker         docker-compose for local Postgres + world-server
infra/migrations     Hand-numbered SQL files, applied only via Postgres docker-entrypoint-initdb.d on a fresh volume
tests/sim-core.tests xUnit-style tests over sim-core only (movement bounds, determinism, harvest/craft/placement rules)
```

## Runtime shape

```
mobile client ──UDP:9050──▶ world-server ──HTTP──▶ gateway ──▶ postgres
                                  │                                 (characters, voyages)
                                  └──▶ postgres (world diffs, structures)
```

- One `world-server` process = one bounded world. Single-threaded event loop:
  `NetManager.PollEvents()` (LiteNetLib) dispatches connect/disconnect/receive
  events, then a fixed-rate tick (`Tuning.TicksPerSecond = 15`) advances
  survival meters and broadcasts state. Everything — packet handling and the
  tick — runs on one thread; there is no separate simulation thread.
- `gateway` is a thin ASP.NET minimal-API service over Postgres: character
  CRUD (`/characters/{id}`) and voyage claim/grant (`/voyage`,
  `/voyage/claim`). It does not run any game simulation.
- The client predicts movement locally (`MovementRules.Check`, shared via
  sim-core) and reconciles against server `Correction` messages; everything
  else (harvest, craft, place, eat) is request → server-decides, no client
  prediction.

## File sizes (as of tonight, `wc -l`)

Flagging anything that's grown past a single, obvious responsibility:

| File | Lines | Note |
|---|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 | **God script.** Meters, hotbar, inventory grid, craft tab, build tab, travel tab, sheet animation all in one class. |
| `apps/client/scripts/world3d/World3D.cs` | 735 | **God script.** Terrain/foliage generation, gather input, day/night lighting, structure rendering, voyage teardown. |
| `apps/world-server/Program.cs` | ~480 | Top-level-statements Program; every message handler lives inline in one `NetworkReceiveEvent` switch. Not yet a god script by line count, but it's where the next few hundred lines of protocol growth will land unless split. |
| `apps/client/scripts/WorldConnection.cs` | 421 | Wire (de)serialization + connection lifecycle for every message type. |
| `apps/gateway/Program.cs` | 210 | Minimal-API endpoints, all in one file — fine at this size. |
| `apps/world-server/Player.cs` | ~230 | Per-connection authoritative state; reasonably scoped. |

Neither god script is *wrong* to exist yet at this project size, but per
`CLAUDE.md`'s "no god-scenes" rule they're both past the point where the next
feature added to either should instead go into a new, focused
scene/script — not grow the existing one further.

## Netcode: what's real vs what's a name only

- **Interest management (invariant #6) is declared, not implemented.**
  `Tuning.InterestRadiusChunks` exists in `shared-proto/Protocol.cs` but has
  no reader anywhere in the world-server. Every `Broadcast()` call
  (`PlayerStates`, `TileChanged`, `HarvestProgress`, `StructurePlaced`,
  `InventoryUpdate`) sends to every connected peer regardless of distance.
  This is fine at prototype player counts and gets expensive fast at real
  ones — see `BACKLOG.md` #1.
- **Chunk streaming is wired but unused.** `RequestChunk`/`ChunkData` exist on
  both sides of the protocol, but the client only ever builds one fixed
  `Radius=2` block around the origin at world-build time
  (`World3D.cs:230-266`) and never calls `RequestChunk`. Walking past that
  built radius currently drops the player off the collision mesh.
- **Movement** is the one place client-authoritative *prediction* is
  intentional and documented (`docs/architecture.md`): the client simulates,
  the server bounds-checks via `MovementRules.Check` (sim-core, shared) and
  corrects. Everything else (harvest, craft, place, eat) is server-decided
  with no client prediction — a `ChopRequest`/`CraftRequest`/etc. either lands
  or is silently ignored (no explicit rejection message for most of these,
  unlike movement's `Correction`).
- **Persistence is split cleanly along invariant #3** (character in gateway,
  world in world-server DB), and world-side persistence (tile diffs,
  structures) survives a restart per `WorldStore.LoadDiffsAsync` /
  `LoadStructuresAsync` at boot. Character-side persistence historically only
  happened on disconnect or explicit voyage release — see `LOG.md` for
  tonight's fix (periodic autosave) and `BACKLOG.md` for what's still open
  there (no DB migration runner for already-provisioned deployments).

## Determinism boundary

`packages/sim-core` is the only place allowed to decide *rules* (terrain,
harvest yields, craft costs, placement legality, survival meter math,
movement bounds) and it holds to the `Noise.Hash`-only, no-float-drift
contract — this is well-tested (`tests/sim-core.tests`) and, per tonight's
audit, is the healthiest part of the codebase. The two rule files with the
thinnest direct test coverage are `HarvestRules.cs` and `Noise.cs` (both only
covered indirectly via `WorldTests`/`DeterminismTests`); `PlacementRules.Blocks`
specifically has zero coverage anywhere and turned out to be dead code (see
`BACKLOG.md` #5).

## What a future night should read before touching things

- Adding a new item/recipe: `packages/sim-core/ItemCatalog.cs` is the single
  source of truth; `HarvestRules`/`PlacementRules`/`CraftingRules` are lookups
  over it, not hand-listed switches — keep it that way.
- Adding a new networked action: follow the existing `Program.cs` pattern
  (validate reach/holdings → mutate `world`/`player` → fire-and-forget
  persistence if it changes durable state → `Broadcast`/`peer.Send`). Don't
  add a second persistence pattern.
- Anything touching `World3D.cs` or `SurvivalHud.cs`: consider whether the new
  logic can be a new script/scene instead of another responsibility bolted on.
