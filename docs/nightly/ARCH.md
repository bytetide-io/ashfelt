# Ashfall — architecture notes for the nightly process

Honest working map of the codebase, written the first night this process ran
(2026-07-27). `docs/architecture.md` and `docs/gameplay-roadmap.md` are the
canonical docs — read those first. This file is the nightly-specific view:
what's solid, what's fragile, where the debt lives, kept up to date by future
nights as the ground truth changes.

## Shape

```
apps/gateway        ASP.NET minimal API. Accounts/characters (Postgres JSONB),
                     world registry (static, in-memory), voyage ticket mint/claim.
apps/world-server    Single-threaded LiteNetLib UDP loop, 15 Hz tick. One process
                     = one bounded world. Owns players, terrain diffs, structures.
apps/client          Godot 4 (.NET), 3D presentation over a tile-grid sim.
packages/sim-core    Deterministic rules: terrain, harvest, craft, placement,
                     survival, movement bounds, world clock. net8.0, referenced
                     by both client and world-server.
packages/shared-proto Wire MessageId enum + CharacterState DTO. net8.0.
```

Both world-server and gateway are net10.0 processes that reference net8.0
libraries (sim-core, shared-proto) — intentional, documented in
`docs/architecture.md`.

## What's solid

- The client-predicts/server-decides split (`MovementRules.Check` in
  sim-core) is genuinely shared, not duplicated — a real implementation of
  invariant #1.
- Seed + diffs world storage (`WorldStore`) — terrain is never persisted,
  only `tile_diff` and `structure` rows, replayed on boot. Verified this
  works: restarted a world-server against a live Postgres and it correctly
  reported `restored N diff(s)`.
- The single-threaded world-server tick loop (`Program.cs`) avoids an entire
  class of concurrency bugs by construction — `players`, `world` and the
  writer are only ever touched from one thread. Fire-and-forget saves
  (`Task.Run`) are the one deliberate escape from that, and they're commented
  as such.
- `sim-core` is well tested: 84 tests across determinism, harvest, crafting,
  placement, movement, survival, world-clock, terrain shape. Kept green
  tonight.

## What's fragile (as of tonight, before/after this session's fix)

- **Character ownership** (`apps/gateway/Program.cs`, `apps/world-server/Program.cs`) —
  was enforced only on voyage arrival, not on a normal join. Fixed this
  session; see `LOG.md` 2026-07-27 and `docs/voyage-transfer.md`. Read the log
  entry before touching `GET /characters/{id}` or the Hello handler again —
  there's a non-obvious first-join race (a brand-new character has no row to
  claim) that the fix handles by claiming atomically via an upsert, not a
  plain UPDATE.
- **No interest management.** `Tuning.InterestRadiusChunks` is declared
  (`shared-proto/Protocol.cs:154`) and architecture.md invariant #5 calls it
  "decided," but nothing reads that constant. `PlayerStates` is broadcast to
  every connected peer every tick regardless of distance — O(n²) egress.
  Fine at today's player counts, won't be once a world has real population.
  See `BACKLOG.md`.
- **No rate limit on action requests.** Movement is budgeted by
  `Player.TryAccept` (time-based). `ChopRequest`/`CraftRequest`/`PlaceRequest`
  are not — a modified client can fire them as fast as the reliable channel
  allows. See `BACKLOG.md`.
- **Client god-scripts.** `World3D.cs` (735 lines: net dispatch, terrain/
  foliage build, day/night, touch input, structure rendering) and
  `SurvivalHud.cs` (940 lines: every HUD panel in one `Control`). Both
  already called out in `docs/gameplay-roadmap.md` §3.4 as planned
  decomposition work, not news — tracked in `BACKLOG.md` with a score so it
  competes fairly against other findings instead of being perpetually "next."
- **Zero test coverage outside sim-core.** No test project exists for
  `apps/world-server` or `apps/gateway`. The ownership bug fixed tonight is
  exactly the kind of thing a concurrency test on the gateway would have
  caught before it shipped. No test infrastructure (Testcontainers, a
  `WebApplicationFactory` harness) exists yet to write one against — this
  session verified the fix by hand against a real local Postgres instead
  (see `LOG.md`), which is not a substitute for a real regression test.

## Environment notes for future nights

- This sandbox ships **no .NET SDK and no running Docker daemon** by
  default — both had to be installed/started by hand this session
  (`apt-get install dotnet-sdk-8.0 dotnet-sdk-10.0`, `service postgresql
  start`, since `docker` itself has no daemon here but `postgresql-16` is
  available via apt as a substitute for `infra/docker/docker-compose.yml`).
  Confirm this is still true before assuming you can't build — it was a
  missing-tool problem, not a sandbox restriction, and cost real time to
  discover.
- Godot itself is not available here, so the client cannot be opened,
  built, or run in this environment. Any client-only change ships unverified
  beyond `dotnet build`-level checks that don't exist for GDScript-equivalent
  C# scene wiring (a `.tscn` referencing a renamed method is a runtime error,
  not a compile error). Say so explicitly in the morning report whenever a
  client change is made.
