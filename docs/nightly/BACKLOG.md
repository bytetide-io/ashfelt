# Ashfall nightly backlog

Ranked ideas and findings not yet built, scored `Severity (1-5) × Blast radius
(1-5)` where applicable. Highest first. A night should generally pull from the
top unless it has a good reason not to (record the reason in `LOG.md` if so).

## Open

### 1. No per-action rate limit on Chop/Craft/Place requests — score 12 (S4×B3)
`apps/world-server/Program.cs` handles `ChopRequest`, `CraftRequest`, and
`PlaceRequest` with a reach/inventory check but no cooldown. A client that
sends these messages faster than the intended interaction pace (a modified
client, or a bare LiteNetLib client using the public default connect key) can
fell nodes and gather resources far faster than the harvest-strike pacing
implies, trivializing the core gather loop. Not remotely exploitable via a
stock client/curl the way tonight's gateway-auth gap was — it needs a custom
game client — but it directly undermines the project's #1 stated priority
("does this make surviving more interesting?"). Fix shape: track
`LastActionAt` per player per action kind (mirroring `Player.LastAcceptedAt`
for movement) and reject requests inside some minimum interval, probably tied
to `HarvestNodeDef`/animation timing once that exists client-side.

### 2. `SurvivalHud.cs` / `World3D.cs` god-scripts — score 9 (S3×B3)
940 and 735 lines respectively, both already flagged in
`docs/gameplay-roadmap.md` §3.4 and grown since (were 419 / 616 lines when
that section was written). `World3D.cs` mixes net dispatch, terrain streaming,
local-player handling, remote-player handling and interaction/targeting.
`SurvivalHud.cs` owns every HUD panel (meters, hotbar, craft, build, items,
travel) with no shared widget kit beyond `StripedBar`. Roadmap §3.4 already
has the shape of the fix (split `World3D.cs` by responsibility with a
`MessageId`-keyed dispatch table; build a reusable item-slot/panel widget kit
for the HUD) — a future night should just execute it. Not attempted tonight:
it's a large, purely-client refactor with real regression risk across every
touch-input surface, and this sandbox cannot run Godot to verify it visually
(see "Build/test environment" in `ARCH.md`).

### 3. No server-side interest management — score 9 (S3×B3), grows with player count
`PlayerStates` (every tick) and `StatsUpdate` (heartbeat) broadcast to every
connected player regardless of distance, in `apps/world-server/Program.cs`'s
`Broadcast(...)` calls. `Tuning.InterestRadiusChunks` exists but nothing reads
it for entity broadcast (chunks are already pull-based via `RequestChunk`, so
terrain streaming is fine). This is invariant #6 on paper, not in code yet —
already tracked as planned work in `docs/gameplay-roadmap.md` §3.3 (build the
entity/actor system with interest management, and make players the first
entity kind through it). Low real impact while worlds hold a handful of
players; will start costing real bandwidth and CPU once that changes. Re-score
this up if `dotnet run --project apps/world-server` is ever load-tested with
more than a few dozen concurrent players.

### 4. `PUT /characters/{id}` doesn't check `owner_world_id` — score 8 (S4×B2)
Even with tonight's `X-Ashfall-Key` gate closing the "any caller" hole, the
save endpoint still lets *any* authenticated world-server overwrite *any*
character regardless of who currently owns it (`character.owner_world_id`).
Today only trusted world-servers hold the key, and each only saves its own
connected players, so this needs a second bug (a compromised or misbehaving
world-server) to matter — meaningfully lower severity than tonight's finding,
which is why it wasn't the one fixed. Worth adding once there's more than one
real deployment: reject (or at least warn-log) a `PUT` where the caller's
`worldId` doesn't match the character's current `owner_world_id`.

### 5. Migrations only auto-apply to a fresh Postgres volume — score 6 (S3×B2)
`infra/migrations/*.sql` run via `docker-entrypoint-initdb.d`, which Postgres
only executes once against an empty data directory. An already-running
deployment needs someone to hand-apply new migration files. The files
themselves are safely idempotent, so this is an operational trap, not a
data-loss risk. Fix shape: a tiny migration runner (even just "apply any
`.sql` in order, tracked in a `schema_migrations` table") the world-server or
a startup script runs before serving.

### 6. `CharacterState` has no version field — score 4 (S2×B2)
Every change so far (adding `Warmth`) has been an additive optional meter with
a safe DB-column default, so nothing has broken yet. There's no guard against
a genuinely breaking change (renaming/removing a field) landing without a
migration path. Low urgency until a breaking change is actually needed —
tracked here so whoever makes one thinks about it first.

### 7. This sandbox has no .NET SDK — blocks verification, not scored
Every nightly session running in an environment like tonight's cannot build,
test, or run anything. `dotnet test tests/sim-core.tests` (required before any
commit per `CLAUDE.md`) could not be run tonight. Whoever reviews tonight's
diff should treat it as **unverified** and build it before trusting it. If
nightly sessions are meant to keep running in sandboxes like this one,
whatever provisions the sandbox needs the .NET 10 SDK preinstalled.

## Rejected / deferred by design (not backlog — for context)

- **PvP.** `docs/voyage-transfer.md` explicitly defers PvP during voyage "not
  until instant transfer is solid and shipped," and `CLAUDE.md` states the
  game is "not a shooter." Tonight's scheduled prompt template assumed PvP/PvE
  is a genre pillar; the project's own docs say otherwise. Noted here so a
  future night doesn't propose combat-first features against the grain of the
  actual design.
