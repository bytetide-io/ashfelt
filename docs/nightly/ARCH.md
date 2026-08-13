# Architecture notes (nightly-maintained)

Honest, living map of the codebase for the overnight engineer. Update this
whenever a night's work changes the shape of something described here — this
file should never go stale relative to `docs/architecture.md`, which states
intent; this one tracks actual condition.

## Layout and size

```
apps/client         Godot 4 mobile client (C#, net8.0)
apps/world-server    Headless authoritative server (C#, net10.0)
apps/gateway          Auth, character storage, voyage routing (C#, net10.0)
packages/sim-core     Shared deterministic simulation (net8.0, referenced by client + world-server)
packages/shared-proto  Wire message definitions + Tuning constants (net8.0)
infra/migrations      Additive SQL schema, no version table
tests/sim-core.tests   xunit, sim-core only
```

Largest files (approx. line counts as of 2026-08-13):

| File | Lines | Note |
|---|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 | god script: meters + hotbar + 4 menu tabs; rebuilds UI from scratch on every `InventoryUpdate` |
| `apps/client/scripts/world3d/World3D.cs` | 735 | god script: terrain/foliage mesh, tap-to-harvest input, day/night clock, structure rendering, all net wiring |
| `apps/world-server/Program.cs` | ~530 | top-level-statements event loop; not god-script territory yet but growing |
| `apps/client/scripts/WorldConnection.cs` | 421 | client net layer, one big message switch |
| `apps/gateway/Program.cs` | 210 | minimal-API endpoints, straightforward |
| `apps/world-server/Player.cs` | ~235 | per-connection state + validation; well-scoped |

## Subsystems

- **Movement**: client predicts, server validates via `MovementRules.Check`
  (sim-core) against the shared height field. No server physics engine —
  see `docs/architecture.md` for the tradeoff.
- **Harvesting**: `HarvestRules` (sim-core) decides yield/hits-to-fell;
  `World.TryHarvest` tracks in-memory strike progress (never persisted) and
  converts the tile via a diff once felled.
- **Crafting**: `CraftingRules.Evaluate` (sim-core) is pure — computes
  deltas, server applies via `Player.ApplyCraft`.
- **Building**: `PlacementRules` + `World.TryPlace`; structures persist via
  `WorldStore.SaveStructureAsync`, keyed by the in-memory id so it survives
  a restart with a stable id.
- **Survival**: `SurvivalRules` (sim-core) drains hunger/stamina/warmth/health
  on the tick; `IsDead` exists but nothing currently reads it — death has no
  consequence yet (see BACKLOG).
- **Voyage**: gateway owns the claim/release handshake described in
  `docs/voyage-transfer.md`; self-heals a stranded ticket via
  `ReclaimExpiredTicketAsync` on next character load.
- **Interest management**: as of 2026-08-13, `PlayerStates` and
  `StructurePlaced` are filtered to `Tuning.InterestRadiusMetres` around each
  viewer (see LOG entry). `TileChanged` and `HarvestProgress` are **not** yet
  interest-managed — still broadcast to every connected player. `RequestChunk`
  has no proximity check either (a client can request any chunk it names).

## Known debt (see BACKLOG.md for scored detail)

- Both client god-scripts above need decomposition — not urgent, but every
  touch to `SurvivalHud.cs` or `World3D.cs` risks an unrelated regression.
- `SurvivalHud`'s `RefreshGrid`/`RefreshHotbar` fully rebuild (QueueFree +
  recreate) on every inventory update instead of diffing.
- No automated tests outside `sim-core` — `world-server`, `gateway`, and the
  voyage claim/expire race are the highest-consequence, least-tested code in
  the repo.
- No cooldown enforcement on `ChopRequest` — a client can fire strikes as
  fast as the socket allows, bypassing the intended pacing (flagged, not yet
  fixed — see BACKLOG, this was the second-highest-scored finding as of the
  2026-08-13 audit).
- `infra/migrations` has no schema-version table; fine while additive, no
  plan for a breaking change yet.
- `World.RemoveStructure` exists and is tested but nothing in
  `world-server` ever calls it — there is no demolish feature.

## Target frameworks

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. This is intentional (net10 referencing net8 works); see
`docs/architecture.md`.
