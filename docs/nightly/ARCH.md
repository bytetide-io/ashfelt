# Ashfall — architecture map (nightly baseline)

Written the first night `docs/nightly/` existed. This is an honest snapshot,
not aspirational — where it disagrees with `docs/architecture.md` (the
decided design), that's noted explicitly.

## Repo shape

```
apps/client         Godot 4 mobile client, C#, net8.0  — ~3.4k LOC
apps/world-server    Headless authoritative server, net10.0 — ~1.0k LOC
apps/gateway         Auth/character/voyage REST API, net10.0 — ~210 LOC
packages/sim-core    Shared deterministic simulation, net8.0 — ~0.9k LOC
packages/shared-proto  Wire messages + Tuning constants, net8.0 — ~180 LOC
tests/sim-core.tests  xUnit, 84 tests, all in sim-core — 0 tests elsewhere
infra/                docker-compose (postgres + world-server), SQL migrations
```

`Directory.Packages.props` centrally pins `LiteNetLib 2.1.4`, `Npgsql 10.0.3`.

## Runtime shape

One `world-server` process per region, single-threaded event loop:
`NetManager.PollEvents()` dispatches queued LiteNetLib events synchronously
on the same thread that runs the tick loop (movement/survival advance,
state broadcast, `Thread.Sleep(tickMs)`). **Anything that blocks inside an
event handler blocks every player's tick**, not just the one being handled —
see Finding 1 in tonight's log, now fixed.

`gateway` is a minimal ASP.NET Core minimal-API app: no in-memory cache,
every request round-trips Postgres. `world` registry (`worlds` dict in
`Program.cs`) is hardcoded — two static entries, `continent-a`/`continent-b`.
Phase 4+ will need this to be live registration instead.

Persistence: `world-server` can run with `ASHFALL_DB` unset (in-memory,
diffs lost on restart) for local dev without Postgres. `gateway` has no such
mode — it throws at startup if `ASHFALL_DB` is missing, by design (character
storage is not optional).

## Where the systems live

- **Movement**: client runs physics and reports position; server checks it
  against `sim-core/MovementRules` (a bound check against the shared height
  field), never re-simulates. `Player.TryAccept` in `world-server/Player.cs`
  is the enforcement point.
- **Harvesting**: `sim-core/HarvestRules` + `World.TryHarvest` — node
  durability, tool-tier bonus. Authorized in `Program.cs`'s `ChopRequest`
  case; diffs persisted fire-and-forget via `WorldStore.SaveDiffAsync`.
- **Crafting**: `sim-core/CraftingRules.Evaluate` — pure function over an
  inventory dict; `Program.cs`'s `CraftRequest` case is a thin dispatcher.
- **Building**: `sim-core/PlacementRules` + `World.TryPlace`; structures are
  broadcast and backfilled to joiners from `world.Structures` (in-memory,
  loaded from Postgres at boot by `WorldStore.LoadStructuresAsync`).
- **Survival**: `sim-core/SurvivalRules` — integer-deterministic hunger/
  stamina/health/warmth, advanced once per tick in the main loop, independent
  of network activity.
- **Voyages**: `docs/voyage-transfer.md` describes the intended flow; the
  `gateway`'s `/voyage` and `/voyage/claim` endpoints implement ticket
  mint/claim with Postgres as the single source of truth for ownership
  (`character.owner_world_id`), so a crash mid-transfer self-heals via
  `ReclaimExpiredTicketAsync` on the next character load. This looks solid.
- **Item data**: `sim-core/ItemCatalog` is the single `ItemDef` table
  (name, stack, category, food value, tool class, placeability) that
  `HarvestRules`/`PlacementRules`/`CraftingRules` all look up — genuinely
  data-driven, matches the CLAUDE.md "no magic numbers" standard.

## Known debt (see BACKLOG.md for scored entries)

- **Interest management is not implemented.** `docs/architecture.md`
  invariant #5 says "a client only ever receives entities/chunks near it."
  In practice every `Broadcast()` call in `world-server/Program.cs`
  (`PlayerStates`, `TileChanged`, `HarvestProgress`, `StructurePlaced`) sends
  to every connected peer regardless of distance. Chunks are the exception —
  those are pulled on request, not pushed — but entity/event traffic is a
  flat broadcast. Fine at today's player counts; the `PlayerStates` payload
  alone is O(players²) bandwidth as the population grows.
- **No tests outside `sim-core`.** `world-server` and `gateway` have zero
  automated coverage. The async refactor landed tonight (Finding 1) was
  verified by hand against a live gateway + Postgres and a throwaway LiteNetLib
  test client (see LOG.md) — there is nothing regression-proof about it.
- **God scripts on the client.** `World3D.cs` (735 lines) owns terrain-mesh
  building, foliage collection, sky/day-night rendering, tap-to-gather input,
  and structure placement rendering — five concerns in one node.
  `SurvivalHud.cs` (940 lines, 49 methods) owns the survival meters, and the
  crafting/build/items/travel tabs — effectively the whole HUD in one
  `Control`. Neither is broken; both will get expensive to extend.
- **`world-server/Program.cs` is a top-level-statements script, ~550 lines**
  after tonight's change, mixing network dispatch, the tick loop, and now
  two background-task orchestration functions. It works, and top-level
  statements make it awkward to unit test in place — a future pass could
  extract a `NetworkHandlers`/`GameLoop` type, but that's a bigger, riskier
  change than one night's budget.
- **World registry is a hardcoded dict** in `gateway/Program.cs` (two
  entries). Fine for two dev worlds; will not survive adding a third
  world-server without a code change and redeploy.

## Environment note for future nights

This container ships neither `dotnet` nor a running Docker daemon by
default. `dotnet-sdk-10.0` and the net8.0 runtime install cleanly via
`packages.microsoft.com`'s apt repo (the `builds.dotnet.microsoft.com`
install script is blocked by the egress proxy — use apt, not
`dotnet-install.sh`). Docker's CLI is present but its daemon is not running
and `service docker start` fails (`ulimit` permission error) — for local
integration testing, native `postgresql-16` (already installed) is the
practical path: `service postgresql start`, create the `ashfall` role/db,
run `infra/migrations/*.sql` as superuser, then
`GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO ashfall;` (the
migrations create tables owned by `postgres`, not `ashfall`, when run this
way).
