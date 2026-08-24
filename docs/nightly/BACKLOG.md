# Ashfall — nightly backlog

Ranked by Severity(1-5) × Blast radius(1-5). Not fixed yet; pick up top-down.

## 1. No automated tests for `gateway` or `world-server` — 4 × 4 = 16

Only `packages/sim-core` has a test project. The character/voyage/ownership
logic — exactly the kind of thing that silently breaks netcode and saves —
has zero coverage. Concretely blocked tonight's fix from being verified any
way other than manual read-through (see LOG.md). Needs: a test project that
can exercise the gateway's minimal-API endpoints against a real or
test-container Postgres (or an in-memory fake of the ownership/claim SQL),
and ideally a world-server integration test that spins up two in-process
"world" clients against one gateway and asserts the second is refused.

## 2. Survival "death" has no consequence — 4 × 4 = 16

`SurvivalRules.SurvivalState.IsDead` (`Health == 0`) is defined and never
read anywhere in `apps/world-server` or the client. Health clamps at 0 in
`SurvivalRules.Step` and nothing else happens: no respawn, no drop, no denial
of further actions, no client-visible state. A player can starve and freeze
to 0 health forever and keep chopping, crafting and eating exactly as before.
For a survival-first game this hollows out the entire threat the warmth/
hunger meters exist to create. Related: `SurvivalRules.TrySpendStamina` is
implemented but never called from either `apps/world-server/Program.cs` or
the client — nothing currently costs stamina, so the meter only ever
regenerates. Not fixed tonight because it's a design/gameplay feature (what
should death *do* — respawn at a bed? drop inventory? a downed state a
teammate can revive from, given "multiplayer-interesting" is a design value?)
rather than a one-line correctness bug, and the audit turned up a
higher-confidence exploit (see LOG.md) that used the one-fix budget.

## 3. Migrations have no apply-to-running-database path — 3 × 3 = 9

`infra/docker-compose.yml` mounts `infra/migrations/*.sql` into Postgres's
`docker-entrypoint-initdb.d`, which Postgres only runs once, on an empty data
volume. There is no migration runner (no `dotnet ef database update`
equivalent, no flyway/dbmate, not even a documented manual step) for taking
an already-running database from migration N to N+1. Fine while every
environment is disposable local dev; will bite the moment a real world's
Postgres volume needs `005_*.sql` applied without wiping player data.

## 4. `World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines) are god
scripts — 3 × 3 = 9

`World3D` owns terrain meshing, orbit camera wiring, touch input, the
harvest-reticle raycast/resolve loop, and all `WorldConnection` event
handling. `SurvivalHud` owns the HUD bars, the inventory grid, the crafting
menu, the placement-mode overlay, and the voyage/travel menu. Both exceed the
400-line guideline and each script "knows about" more than the ~3 systems
rule of thumb. Nothing incorrect was found inside either tonight — this is a
maintainability/velocity cost, not a bug — but a future pass should split
each into per-concern components (e.g. `HarvestReticle`, `InventoryPanel`,
`CraftingPanel`, `VoyageMenu`) that compose, matching the "small reusable
scenes/components" rule in CLAUDE.md.

## 5. `ClientState` is sent every render frame, not on a fixed tick — 2 × 2 = 4

`World3D._Process` calls `WorldConnection.SendClientState` once per rendered
frame (uncapped by any send-rate limiter), unreliable UDP. Payload is small
(3 floats + yaw, ~16 bytes + header) so this is not urgent, but on a
high-refresh-rate phone display this scales with framerate rather than a
fixed tick, and nobody has measured actual bytes/player/second. Worth
capping to a fixed interval (e.g. matching `Tuning.TicksPerSecond` on the
server) and recording the measured bandwidth number, per the audit's
"measure it, record the number" instruction — not done tonight for lack of a
runnable build to profile against (see LOG.md).

## 6. Gateway world registry is hardcoded — not scored (known, documented)

`apps/gateway/Program.cs` keeps `continent-a` / `continent-b` in a static
`Dictionary` with an explicit code comment marking it as a Phase 3+
placeholder for live world-server registration. Not a bug; flagging only so
a future night doesn't rediscover it as a surprise.
