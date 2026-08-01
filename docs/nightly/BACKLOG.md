# Ashfall — nightly backlog

Ranked ideas and known issues not yet built/fixed, from the overnight-engineer
routine. Newest audit first. Score = Severity(1-5) × Blast radius(1-5). Any
new finding ≥ 15, or any multiplayer-correctness finding ≥ 9, must be picked
up (or explicitly deferred with reasoning) the night it's found.

## From the 2026-08-01 audit

| # | Score | Sev | Blast | Finding | Location |
|---|---|---|---|---|---|
| 1 | ~~20~~ | 5 | 4 | ~~No auth on gateway REST API~~ — **FIXED 2026-08-01**, see LOG.md | `apps/gateway/Program.cs` |
| 2 | 16 | 4 | 4 | Interest management (invariant #6) unimplemented — `PlayerStates`/`TileChanged`/`HarvestProgress`/`StructurePlaced` broadcast globally to every connected client regardless of distance; `Tuning.InterestRadiusChunks` is declared but never read | `packages/shared-proto/Protocol.cs:154`, `apps/world-server/Program.cs:224,236,294,406` |
| 3 | 16 | 4 | 4 | Blocking `.GetAwaiter().GetResult()` gateway calls on the Hello and RequestRelease paths stall the single-threaded 15 Hz tick loop for *every* player in the world while one player's gateway round-trip is in flight | `apps/world-server/Program.cs:119-129,322-325` |
| 4 | 12 | 4 | 3 | Voyage transfer and character persistence — the most concurrency-fragile subsystem in the repo — have zero automated tests; only `sim-core` has a test project | `tests/` (absence), `apps/gateway/Program.cs:106-187` |
| 5 | 12 | 3 | 4 | Fire-and-forget diff/structure DB writes swallow failures silently (no try/catch/log), unlike the character-save path right next to them which does log on failure | `apps/world-server/Program.cs:217,290` |
| 6 | 9 | 3 | 3 | No real migration runner — SQL files under `infra/migrations` apply only to a fresh Postgres volume via `docker-entrypoint-initdb.d`; no version table, no rollback, and a running Postgres never picks up a new migration file | `infra/docker/docker-compose.yml:15`, `infra/migrations/*.sql` |
| 7 | 8 | 4 | 2 | `SurvivalHud.cs` is a 940-line god-script (already flagged in `docs/gameplay-roadmap.md` §3.4 at 419 lines — has since more than doubled without the decomposition the roadmap called for). Builds every HUD element and every menu tab procedurally in one file | `apps/client/scripts/world3d/SurvivalHud.cs:21` |
| 8 | 8 | 4 | 2 | `World3D.cs` is a 735-line god-script mixing terrain build, input, sky clock and net dispatch (flagged in the roadmap at 616 lines, since grown further) | `apps/client/scripts/world3d/World3D.cs:15` |
| 9 | 6 | 2 | 3 | `RequestChunk`/`ChunkData` protocol path is dead code — the client never streams chunks over the wire, it builds a fixed local radius client-side instead | `apps/client/scripts/WorldConnection.cs:123`, `apps/world-server/Program.cs:154-168` |
| 10 | 6 | 2 | 3 | `World.HasWarmthNear` does an unindexed linear scan of every structure in the world, per player, per tick (15 Hz) — fine at current scale, unbounded growth risk as structures accumulate | `apps/world-server/Program.cs:385`, `packages/sim-core/World.cs:130` |
| 11 | 3 | 1 | 3 | `PlacementRules.Blocks` bypasses the `ItemCatalog` single-source-of-truth pattern used everywhere else in the file | `packages/sim-core/PlacementRules.cs:31` |
| 12 | 2 | 1 | 2 | `DebugCapture.cs` still calls `GetCamera2D()` in the now-3D client — capture framing options silently no-op | `apps/client/scripts/DebugCapture.cs:68` |

**Audit's own note on what's healthy** (worth keeping so nobody re-litigates
it without cause): no client-authoritative gameplay logic found — damage,
harvest, craft, placement and movement bounds are all server-checked; no
duplicated harvest/craft/movement/placement math across the client/server
boundary — both sides genuinely call into `sim-core`; no desync-risk
nondeterminism in `sim-core`; no race condition on two players
harvesting/crafting/placing the same object concurrently (the
single-threaded packet loop serialises this, and the voyage-ticket claim is
atomic at the DB level); day/night, survival meters and crafting are
fixed-point/integer and covered by tests.

## Suggested pick order for future nights

Findings #2 and #3 are both scored 16 (≥15 threshold) and were *not* picked
up tonight because the decision rule says fix exactly one thing — #1 (score
20) was strictly higher and is a bigger blast radius than either. Whichever
of #2/#3 gets picked up next should go first; #3 (blocking gateway calls
stalling the tick loop) is arguably the cheaper fix (swap to a non-blocking
handshake / async continuation) and would reduce latency spikes for every
player, not just the one joining/leaving.
