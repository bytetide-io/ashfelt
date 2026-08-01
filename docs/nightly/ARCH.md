# Ashfall — architecture map (nightly-maintained)

Honest, living snapshot of the codebase for the overnight-engineer routine.
Update this whenever a change is structural. Canonical design docs are
`docs/architecture.md` and `docs/voyage-transfer.md`; this file is a map for
finding your way around fast, plus a running list of debt.

Last updated: 2026-08-01 (first nightly pass — this file was created tonight;
no prior nightly history exists).

## Repo shape

```
apps/client         Godot 4 mobile client, C#, net8.0
apps/world-server    Headless authoritative UDP server, C#, net10.0 (console app, LiteNetLib)
apps/gateway          Auth + character persistence + voyage coordination, C#, net10.0 (Postgres)
packages/sim-core     Shared deterministic sim: terrain, harvest/craft/placement/survival
                       rules, movement bounds, day/night clock, item catalog. net8.0.
packages/shared-proto  Wire message ids/layouts (Protocol.cs), Character.cs DTO. net8.0.
tests/sim-core.tests   Only test project in the repo — determinism + rule tests.
infra/migrations       Plain numbered .sql files, applied in order, no migration runner.
infra/docker           docker-compose for Postgres + world-server local dev.
```

One CI workflow (`.github/workflows/ci.yml`): builds sim-core/shared-proto/
world-server/gateway and runs `dotnet test tests/sim-core.tests`; a second job
builds the client assemblies and does a headless Godot import. Nothing in CI
runs world-server or gateway logic — those two apps have zero automated
coverage beyond "does it compile."

## Data flow

```
client ──Hello(charId, voyage ticket)──▶ world-server ──REST──▶ gateway ──▶ Postgres
client ◀──PlayerStates (15Hz, unreliable)── world-server
client ──ClientState (position)──▶ world-server  (server validates against height field)
client ──ChopRequest/CraftRequest/PlaceRequest/EatRequest──▶ world-server (authoritative)
world-server ──tile_diff / structure rows──▶ Postgres (per-world DB, seed + diffs only)
```

`apps/world-server/Program.cs` is a single top-level-statements file (468
lines) holding: UDP listener setup, the Hello/voyage-claim handshake, all
per-message-type handling (chop/craft/place/eat/release), and the 15 Hz tick
loop (survival advance, position broadcast, dirty-inventory push, stats
heartbeat). It is the de facto core of the server. Not yet a "god script" by
line count but it is the one place that knows about every subsystem — watch
this file's growth.

`packages/shared-proto/Protocol.cs` defines `MessageId`, `ItemId` (wire-stable,
append-only) and `Tuning` (tick rate 15 Hz, chop range 5 m, interest radius 1
chunk, day length 600 s, stats heartbeat every 2 s). This is the single place
network cadence and gameplay-adjacent constants are declared — good, matches
the "no magic numbers" rule.

## Client structure

- `apps/client/scripts/world3d/World3D.cs` (735 lines) — the 3D world root:
  wires terrain, camera, HUD, harvesting reticle, remote players, and the
  SubViewport pixel-art pipeline together. Largest client script; a
  refactor candidate once it grows further, not urgent yet.
- `apps/client/scripts/world3d/SurvivalHud.cs` (940 lines) — the single
  largest file in the repo. Builds every HUD element (meters, day/night chip,
  crafting/build/items/travel menu tabs) procedurally in code rather than as
  separate reusable scenes/components. This is the strongest "god script"
  candidate in the codebase per the CLAUDE.md composition-over-inheritance
  and small-reusable-scenes guidance.
- `apps/client/scripts/WorldConnection.cs` (421 lines) — owns the UDP
  connection, message encode/decode, and reconnect logic.
- `apps/client/scripts/ui/DesignSystem.cs` / `PixelIcons.cs` — the Ink/Ember
  design tokens and runtime-generated pixel icons; correctly centralised per
  `docs/architecture.md`.

## sim-core (the shared rules library)

`TerrainGenerator`, `MovementRules`, `HarvestRules`, `CraftingRules`,
`PlacementRules`, `SurvivalRules`, `WorldClock`, `ItemCatalog`, `Noise`,
`World`, `TileType`. All under ~160 lines each — healthy size. `ItemCatalog`
is the single source of item behaviour (stack size, food value, tool class,
placeability); `HarvestRules`/`PlacementRules` are lookups over it rather than
hand-listed switches, which is the intended pattern per the README. New
content (items, recipes) should extend the catalog, not add parallel
special-casing.

Determinism is enforced by `Noise.Hash` (integer hashing) and covered by
`tests/sim-core.tests/DeterminismTests.cs`, which is a compatibility contract
— never "fix" a failing determinism test by updating its expected values.

## Persistence

- World state: `tile_diff` (per-chunk tile overrides) + `structure` (placed
  buildings), keyed by `world_id`, seed stored once on `world`. No full-chunk
  persistence anywhere — matches invariant #2.
- Character state: `character` table in the gateway DB (`infra/migrations/002_character.sql`),
  inventory as JSONB keyed by stable `ItemId` enum names (survives wire
  renumbering), survival meters as plain integers.
- Voyage state: `infra/migrations/003_voyage.sql` — `owner_world_id` nullable
  column + ticket table; NULL owner means in-transit, so a mid-transfer crash
  can't duplicate a character. This is the correctness-critical persistence
  path in the repo; see `docs/voyage-transfer.md`.
- Migrations are plain sequential `.sql` files (001..004) with no version
  table and no automated migration runner found in this pass — applying them
  appears to be a manual/docker-compose-time step. No rollback path. Logged
  to BACKLOG.

## Known debt (see BACKLOG.md for scored detail)

The 2026-08-01 audit confirmed the suspicions above and found one thing this
map missed: the gateway's `/characters` and `/voyage*` endpoints had **no
authentication at all** (score 20/25 — highest of the audit) — fixed the same
night, see `LOG.md`. Remaining, ranked in `BACKLOG.md`:

- Interest management (invariant #6) is declared in `Tuning` but not actually
  implemented — every broadcast goes to every client regardless of distance.
- The gateway handshake blocks the world-server's single-threaded tick loop
  (`.GetAwaiter().GetResult()` on join/release) — stalls every player in the
  world while one player's HTTP round-trip is in flight.
- `SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines) are god-scripts;
  both were already flagged smaller in `docs/gameplay-roadmap.md` and have
  grown past that call without the decomposition it asked for.
- World-server and gateway have no automated tests; only sim-core does.
- Migrations have no version tracking / rollback runner.

See `BACKLOG.md` for the full scored list and `LOG.md` for what was actually
acted on each night.
