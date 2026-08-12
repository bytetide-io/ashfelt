# Architecture notes (nightly)

Honest working map of the codebase for the overnight-engineer routine. Not a
replacement for `docs/architecture.md` (the source of truth for decisions) —
this is where debt, sizes and rough edges get tracked so a future night
doesn't have to rediscover them.

## Shape

```
apps/client         Godot 4 mobile client (C#, net8.0) — 3D pixel-art render
apps/world-server    Headless authoritative UDP server (C#, net10.0)
apps/gateway         ASP.NET minimal API — accounts, characters, voyages
packages/sim-core    Shared deterministic sim (net8.0, referenced by both)
packages/shared-proto Wire message + character-state shapes (net8.0)
infra/migrations     Plain numbered .sql files, idempotent (IF NOT EXISTS /
                     ADD COLUMN IF NOT EXISTS), applied by mounting the
                     directory into postgres's docker-entrypoint-initdb.d
infra/docker         docker-compose for local postgres + world-server
tests/sim-core.tests xUnit-style tests — the ONLY automated test project
```

This is a real C#/Godot-4-C# project, not GDScript — the generic nightly
brief's GDScript-specific smells (`get_node("../../Player")`, `.tres`
resources, `queue_free()` leaks) don't map 1:1. Translated: watch for direct
cross-node `GetNode<T>("path")` coupling (client mostly avoids this — it uses
exported `NodePath`s instead, e.g. `World3D` reaching the HUD across the
SubViewport boundary) and for logic embedded in `_Process`/`_PhysicsProcess`
that belongs in a system.

## Multiplayer model

- Movement: client-simulated, server-validated (`MovementRules.Check` in
  sim-core, shared). Server never runs a physics engine.
  `apps/world-server/Player.cs` `TryAccept` is the single acceptance gate.
- Harvesting/crafting/placement: fully server-authoritative. The world-server
  loop (`apps/world-server/Program.cs`) is single-threaded — `NetManager` is
  polled synchronously inside the tick loop (`server.PollEvents()`), so all
  message handling and the tick's own state mutation happen on one thread.
  There is no cross-thread mutation of `players`/`world` to worry about,
  which is why several comments in Program.cs lean on "the loop is
  single-threaded" as their safety argument — that's true today; if that ever
  changes (e.g. moving to async I/O per-connection) those comments' guarantees
  break silently.
- Character persistence: gateway owns `character` (Postgres), keyed by a
  client-generated device UUID. World-server loads on join, saves on leave.
  Fire-and-forget saves (never block the tick loop for other players);
  loading is synchronous/blocking (a join is inherently a wait).
- Voyages (`docs/voyage-transfer.md`): three-party handshake, gateway is the
  ownership authority via `character.owner_world_id` + a single-use,
  TTL'd `voyage_ticket` row.

**2026-08-12 fix (see LOG.md):** `owner_world_id` was previously only
consulted by the voyage path. A normal join skipped it entirely and could
load the same character on two world-servers at once. Every join now claims
ownership atomically (`POST /characters/{id}/claim` or `/voyage/claim`)
before loading; a clean disconnect releases it, and a 300s staleness window
self-heals a crash that never got to release. See
`infra/migrations/005_ownership_claim.sql`.

## Data model

- World state = seed + diffs. `tile_diff` (per-tile overrides) and
  `structure` (placed buildings) are the only persisted world tables; chunks
  are always regenerated from `TerrainGenerator` + the seed.
- Character state = one JSONB `inventory` column (keyed by stable `ItemId`
  enum name, not the wire byte, so a protocol renumber can't corrupt a save)
  plus four integer meter columns (hunger/stamina/health/warmth).
- No `version` field anywhere in a persisted payload (`CharacterState`,
  `tile_diff`, `structure`). Migrations are additive/idempotent so this has
  been survivable so far, but there's no mechanism to migrate a *shape*
  change in `CharacterState` (e.g. renaming a field) — see BACKLOG.md.

## File sizes worth knowing about

Everything server-side is small and single-purpose. The client has three
files that are large enough to watch:

| File | Lines | Note |
|---|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 | HUD: meters, hotbar, crafting/building sheet, tabs. Candidate god-script — see BACKLOG. |
| `apps/client/scripts/world3d/World3D.cs` | 735 | Scene root: input, camera rig, harvesting reticle, network callbacks, structure spawning. |
| `apps/world-server/Program.cs` | ~490 | Top-level-statements event loop; long by construction (it's the whole server), not by accretion — each `case` is short and single-purpose. |

## Testing

`tests/sim-core.tests` covers sim-core thoroughly (determinism, terrain,
movement, crafting, survival, placement, world clock, item catalog). There is
**no automated test coverage for `apps/world-server` or `apps/gateway`** — the
UDP protocol handling, the ownership-claim logic (including tonight's fix),
and the voyage handshake are all currently verified by manual/local testing
only. See BACKLOG.md.

## What tonight's session could NOT verify

No .NET SDK is installed in this environment and the outbound proxy blocks
`builds.dotnet.microsoft.com` (policy denial, confirmed via
`$HTTPS_PROXY/__agentproxy/status`), so `dotnet build`/`dotnet test` could not
be run. Every change tonight was reviewed by hand instead — see LOG.md for
exactly what that covered and didn't. A human must run
`dotnet test tests/sim-core.tests` and a manual two-client join/reconnect
check before trusting this on a real device.
