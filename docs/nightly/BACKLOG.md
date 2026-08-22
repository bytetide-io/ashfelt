# Nightly backlog

Ranked ideas and findings not yet built/fixed. Score is
`Severity(1-5) × Blast radius(1-5)` at the time it was logged — re-score if
you suspect the codebase has moved since. Highest first. A night should
generally pull from the top unless it has a good reason not to (record the
reason in `LOG.md` if so).

## Open

### 1. World-server blocks its single packet/tick thread on gateway HTTP calls — score 20 (S4×B5)
`apps/world-server/Program.cs` — the `Hello` and `RequestRelease` handlers
both call `.GetAwaiter().GetResult()` on an HTTP round trip to the gateway
(`ClaimCharacterAsync`, `ClaimVoyageAsync`, `SaveCharacterAsync`,
`RequestVoyageAsync`). A slow or unreachable gateway freezes
movement/harvest/tick broadcasts for *every* connected player, on every join
and every voyage — not a rare edge case, the common path. Logged
2026-07-24, not yet fixed: the correct fix is a real architecture change (a
real background `Task` for the HTTP call, a "pending" peer state that skips
gameplay messages, completions drained from a thread-safe queue on the next
tick so `PollEvents()` cadence for everyone else is never blocked). Bigger
and riskier than a single night allows; deserves its own session with room to
reason about the pending-state lifecycle (mid-claim disconnects, etc.).

### 2. No per-action rate limit on Chop/Craft/Place requests — score 12 (S4×B3)
`apps/world-server/Program.cs` handles `ChopRequest`, `CraftRequest`, and
`PlaceRequest` with a reach/inventory check but no cooldown. A client that
sends these messages faster than the intended interaction pace (a modified
client, or a bare LiteNetLib client using the public default connect key) can
fell nodes and gather resources far faster than the harvest-strike pacing
implies, trivializing the core gather loop — directly undermines the
project's #1 stated priority ("does this make surviving more interesting?").
Needs a custom game client to exploit, not a stock client/curl, unlike #4
below — lower urgency than that was. Fix shape: track `LastActionAt` per
player per action kind (mirroring `Player.LastAcceptedAt` for movement) and
reject requests inside some minimum interval, probably tied to
`HarvestNodeDef`/animation timing once that exists client-side.

### 3. `World3D.UpdateGatherPrompt` runs an unconditional per-physics-tick scan — score 12 (S4×B3)
`apps/client/scripts/world3d/World3D.cs:491-559` runs a 7×7-tile scan every
physics tick (60/sec), each tile costing a multi-octave FBM height sample
plus 1-3 `Noise.Hash` calls via `sim-core.TerrainGenerator`. Runs even while
standing still and even while the action sheet hides the visual prompt. Fix:
cache the player's current tile coordinate and early-return when it hasn't
changed since the last physics frame; skip the scan entirely while the
action sheet is open.

### 4. `SurvivalHud.cs` / `World3D.cs` / `WorldConnection.cs` — score 9 each (S3×B3)
All three already flagged in `docs/gameplay-roadmap.md` §3.4 and have grown
since it was written, not shrunk:
- `SurvivalHud.cs` (940 lines, was 419) — one class builds every HUD panel
  (4 survival meters, day/night chip, touch stick + jump button, hotbar,
  gather prompt, a full tabbed action sheet with its own tab state machine
  and business logic like calling `CraftingRules.CanCraft` directly). No
  reusable meter/slot widget exists.
- `World3D.cs` (735 lines, was 616) — net dispatch, day/night lighting,
  procedural foliage generation, touch-input classification, harvest-target
  scanning, and structure placement/mesh building in one script.
- `WorldConnection.cs`'s `OnReceive` — a 12-case hand-decoded switch on
  `MessageId` with wire order that must exactly match the server's writer
  with no compile-time check; a reordering on either side is a silent wire
  bug.

Fix shape for all three is already in roadmap §3.4/§3.2: split `World3D.cs`
by responsibility with a `MessageId`-keyed dispatch table (mirrors §3.2's
typed read/write helpers, which would also fix the `WorldConnection.cs`
fragility above), build a reusable item-slot/meter widget kit for the HUD.

### 5. No server-side interest management — score 9 (S3×B3), grows with player count
`PlayerStates` (every tick) and `StatsUpdate` (heartbeat) broadcast to every
connected player regardless of distance, in `apps/world-server/Program.cs`'s
`Broadcast(...)` calls. `Tuning.InterestRadiusChunks` exists but nothing reads
it for entity broadcast (chunks are already pull-based via `RequestChunk`, so
terrain streaming is fine). This is invariant #6 on paper, not in code yet —
tracked as planned work in `docs/gameplay-roadmap.md` §3.3 (build the
entity/actor system with interest management, and make players the first
entity kind through it). Low real impact while worlds hold a handful of
players; will start costing real bandwidth and CPU once that changes.

### 6. `World3D.CollectTrees`/`CollectShrubs`/`CollectBerryBushes` triplicate the same pattern — score 6 (S3×B2)
`apps/client/scripts/world3d/World3D.cs:325-436` — same loop/sample/jitter/
place pattern three times, differing only in density constant and offset
scale. Due per `CLAUDE.md`'s "third occurrence is a refactor," and Phase C
will add more foliage types on this same pattern. Fix: one
`CollectFoliage(TileType, perTile, saltBase, place)` helper.

### 7. `PUT /characters/{id}` doesn't check `owner_world_id` — score 8 (S4×B2)
The claim endpoint (`POST /characters/{id}/claim`, fixed 2026-07-24) and the
gateway auth gate (`X-Ashfall-Key`, fixed 2026-08-22) together close the two
biggest holes here, but the plain save endpoint still lets *any*
authenticated world-server overwrite *any* character regardless of who
currently owns it. Today only trusted world-servers hold the key, and each
only saves its own connected players, so this needs a second bug (a
compromised or misbehaving world-server) to matter. Worth adding once there's
more than one real deployment: reject (or at least warn-log) a `PUT` where
the caller's `worldId` doesn't match the character's current
`owner_world_id`.

### 8. Inconsistent node-path coupling in the client — score 4 (S2×B2)
`apps/client/scripts/world3d/PlayerBody.cs:46` uses a raw
`GetNode<Node3D>("../CameraRig")` relative path while `World3D.cs` uses
exported `NodePath` fields for some lookups and hardcoded string
`GetNode<T>(...)` for others in the same method. Fix: exported `NodePath`
fields consistently.

### 9. `CharacterState` has no version field — score 4 (S2×B2)
Every change so far (adding `owner_world_id`, then `Warmth`) has been
additive with a safe DB-column default, so nothing has broken yet. There's no
guard against a genuinely breaking change (renaming/removing a field) landing
without a migration path. Low urgency until a breaking change is actually
needed — tracked here so whoever makes one thinks about it first.

### 10. Migrations only auto-apply to a fresh Postgres volume — unscored, operational
`infra/migrations/*.sql` run via `docker-entrypoint-initdb.d`, which Postgres
only executes once against an empty data directory. An already-running
deployment needs someone to hand-apply new migration files. The files
themselves are safely idempotent (`IF NOT EXISTS`, `ADD COLUMN ... DEFAULT`),
so this is an operational trap, not a data-loss risk. Fix shape: a tiny
migration runner (even just "apply any `.sql` in order, tracked in a
`schema_migrations` table") the world-server or a startup script runs before
serving.

### 11. No automated test coverage for `apps/gateway` or `apps/world-server` — unscored, process gap
Only `sim-core` has a test project. Two nights running now (2026-07-24,
2026-08-22) have shipped gateway auth/ownership changes verified by manual
code review only, because neither session had a working `dotnet` in its
sandbox (see #12). A future night should add an integration test project
that spins up the gateway against a real (or testcontainers) Postgres and
exercises the claim/save/voyage/auth endpoints directly, including the
concurrent-claim race and the unauthenticated-request-gets-401 case.

### 12. This sandbox has no .NET SDK — blocks verification, not scored
Both nightly sessions to date (2026-07-24, 2026-08-22) ran in a sandbox with
no `dotnet` on `PATH` and could not build, test, or run anything in this
repo. `dotnet test tests/sim-core.tests` (required before any commit per
`CLAUDE.md`) could not be run either time. This is now a pattern: whoever
reviews a nightly diff should treat it as **unverified by construction**, and
whoever provisions the sandbox should get the .NET 10 SDK (and ideally a
reachable Postgres) preinstalled so future sessions aren't flying blind.

## Fixed

- **~~No ownership check on normal (non-voyage) character load, letting the
  same character UUID be loaded concurrently by two sessions~~** — fixed
  2026-07-24: `GET /characters/{id}` replaced with atomic
  `POST /characters/{id}/claim`. See `LOG.md`.
- **~~No authentication at all on any gateway route~~** — fixed 2026-08-22:
  every route but `/health`/`/worlds` now requires the `X-Ashfall-Key` shared
  secret. Before this, any HTTP caller who knew a character's device UUID
  (every player knows their own) could set that character's inventory and
  survival meters directly, bypassing the ownership-claim fix above entirely
  — claiming ownership doesn't help if the save endpoint next to it has no
  authentication either. See `LOG.md`.

## Rejected / deferred by design (not backlog — for context)

- **PvP.** `docs/voyage-transfer.md` explicitly defers PvP during voyage "not
  until instant transfer is solid and shipped," and `CLAUDE.md` states the
  game is "not a shooter." The scheduled nightly-routine prompt template
  assumes PvP/PvE is a genre pillar; the project's own docs say otherwise.
  Noted here so a future night doesn't propose combat-first features against
  the grain of the actual design.
