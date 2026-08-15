# Ashfall — architecture map (nightly baseline)

Written the first night the `docs/nightly/` process ran. This is a snapshot for
future nightly sessions to orient quickly; `docs/architecture.md` remains the
source of truth for decided invariants — this file is the honest, informal map
plus known debt. Update it when the shape of the codebase changes materially,
not on every commit.

## Layout (as of tonight)

```
apps/client          Godot 4 mobile client (C#, net8.0)
  scripts/            WorldConnection (netcode), world3d/ (rendering, input,
                       gather, structures), ui/ (design system, icons, HUD)
apps/world-server     Headless authoritative server (C#, net10.0)
  Program.cs           connection handling, tick loop, message dispatch
  Player.cs            per-connection authoritative state + validation
  WorldStore.cs        Postgres persistence (diffs + structures), optional
  GatewayClient.cs      REST calls to the gateway (character load/save, voyage)
apps/gateway          Accounts/character REST API + voyage coordination (net10.0)
packages/sim-core      Shared deterministic rules (net8.0): terrain, movement,
                       harvest, crafting, survival, day/night. Client and
                       world-server both reference it — this is the one place
                       gameplay math is allowed to live.
packages/shared-proto  Wire message ids (MessageId), ItemId, Tuning constants.
                       Both apps and the client reference this, never redeclare.
tests/sim-core.tests   84 tests, xUnit-style. Covers determinism, movement,
                       harvest, crafting, survival, placement, world clock.
                       This is the ONLY automated test project in the repo —
                       world-server, gateway and client have none.
infra/migrations      Numbered SQL files, applied by convention (no migration
                       runner found in-repo; presumably applied manually or by
                       the docker-compose init).
```

## Data flow (today)

- Client owns physics; reports position via `ClientState` at 15 Hz
  (`Tuning.ClientStateHz`). Server validates with `MovementRules.Check`
  (shared, sim-core) and corrects on rejection.
- Server tick loop runs at 15 Hz (`Tuning.TicksPerSecond`), single-threaded,
  `Thread.Sleep`-paced (`apps/world-server/Program.cs`). Every tick: advance
  survival meters for all connected players, send each player their
  `PlayerStates` snapshot, flush dirty inventories, and (every 2s) send a
  stats heartbeat.
- Harvesting, crafting, placing and eating are all client-request /
  server-authorises-and-broadcasts. None of the four have a per-player rate
  limit — see backlog.
- World storage is seed + diffs (tile_diff, structure tables), replayed on
  startup. Character (inventory, survival meters) lives in the gateway's
  Postgres, loaded/saved over HTTP at join/leave. No physics or RNG runs
  server-side beyond the shared deterministic sim-core rules.

## Known debt (see BACKLOG.md for scored detail)

- **Interest management was declared as an invariant but only partially
  implemented.** Fixed tonight for `PlayerStates` (see LOG.md). Still
  unfiltered: `StructurePlaced`/`TileChanged`/`HarvestProgress` broadcasts, and
  the structure backfill sent in full to every joining player.
- **No automated tests outside sim-core.** World-server netcode (Player.cs,
  Program.cs message handlers) and the client have zero test coverage. Verified
  by hand tonight with a throwaway 2-client harness (not committed); nothing
  catches a regression here automatically.
- **No per-player rate limiting on request messages** (`ChopRequest`,
  `CraftRequest`, `PlaceRequest`, `EatRequest`). Movement is budgeted by
  elapsed time since last accepted position; these four are not — a client
  that fires packets faster than the intended tap cadence gets served faster
  than intended, and each triggers a broadcast, so it's also a spam vector.
- **`World3D.cs`** (735 lines) is the closest thing to a god-node: it owns
  terrain/foliage building, tap-to-harvest input, the day/night clock, and
  structure rendering in one script. Not urgent (each responsibility is a
  short, clearly separated method) but a candidate for splitting into
  components if it keeps growing.
- **`SurvivalHud.cs`** (940 lines) is long but single-purpose — it's
  imperative Godot UI construction for one screen, not several tangled
  systems. Lower priority than World3D.cs; would still benefit from being
  split into one builder class per tab (Meters/Hotbar, ActionSheet,
  Inventory, Craft, Build, Travel) if it grows further.
- **Character persistence has no explicit schema-version field.** Migrations
  are numbered files and inventory is a self-describing JSONB keyed by stable
  `ItemId` names, which tolerates additive changes, but there's no recorded
  migration path for a genuinely breaking change. Low urgency: per
  `docs/architecture.md`, no world has shipped yet.

## Target frameworks (unchanged, confirmed tonight)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. Confirmed buildable tonight with `dotnet-sdk-8.0` + `dotnet-sdk-10.0`
installed fresh (this sandbox had no .NET SDK at session start).
