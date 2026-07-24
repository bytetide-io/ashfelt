# Nightly backlog

Ranked ideas and findings not built/fixed yet. Score = Severity(1-5) ×
Blast radius(1-5), computed the way `docs/nightly/LOG.md`'s audit protocol
defines them. Pick highest score first unless it's stale (re-check before
trusting an old score — the codebase moves).

## Open findings

### 1. Interest management is declared but not implemented — score 9
`Tuning.InterestRadiusChunks` (`packages/shared-proto/Protocol.cs`) exists and
`MessageId.PlayerStates`'s doc comment claims "every player in interest
range," but `apps/world-server/Program.cs`'s tick loop broadcasts every
connected player's position to every other connected player, every tick, with
no distance filter at all. Harmless correctness-wise at today's player counts
(one small dev world), but: (a) it's a documented invariant
(`docs/architecture.md` #6, `CLAUDE.md` "Interest management") that the code
doesn't honor, and (b) bandwidth is O(N) per player / O(N²) server-wide with
no cap. Fix: filter `PlayerStates` (and eventually chunk pushes) to peers
within `InterestRadiusChunks` of the recipient, or delete the constant and the
doc comment's claim if interest management is deliberately deferred. Severity
3 (doc/impl mismatch on a load-bearing invariant) × blast radius 3 (every
player, but no current symptom) = 9.

### 2. No schema migration path — score 9
`infra/migrations/*.sql` only runs via Postgres' `/docker-entrypoint-initdb.d`
first-init hook — it is never re-applied to an existing data volume. Anyone
who created their `pgdata` volume before a given migration file existed never
gets it applied automatically (confirmed for `004_warmth.sql`'s `ALTER TABLE`).
No version table, no migration runner. Not urgent while there's no real
player data, but every future schema change has the same silent-no-op risk,
and it will eventually corrupt someone's dev/staging environment in a
confusing way (missing column errors at runtime, not at migration time).
Fix: adopt a real migration runner (even a minimal one — a `schema_migrations`
table + "run any .sql not yet recorded, in order, on world-server/gateway
startup" is enough) before Phase 4 adds the next schema change. Severity 3 ×
blast radius 3 = 9.

### 3. `World3D.cs` is a 735-line god-scene-in-code — score 6
Single `_Ready` wires together `WorldConnection`, `PlayerBody`, `RemotePlayers`,
`SurvivalHud`, day/night lighting, tap-to-harvest input resolution, and
structure rendering. Works, and each piece is a well-named private method —
but it's the "a node that knows about more than ~3 systems" smell called out
in the nightly audit brief. Candidate split: pull day/night (`AdvanceClock`/
`UpdateSky`/`SunArcTilt` etc.) into a `DayNightController` node, and the
tap-to-harvest input resolution (`BeginTap`/`EndTap`/`UpdateGatherPrompt`)
into a `HarvestInteractor` node, leaving `World3D` as pure world-build +
wiring. Severity 2 × blast radius 3 = 6.

### 4. `SurvivalHud.cs` is a 940-line UI god-script — score 4
No `.tscn` scene backs the HUD; every control is built in code. That's a
deliberate, documented choice (icons/design tokens are data, not files —
see `docs/architecture.md`), but the result is one class owning meters,
hotbar, the crafting sheet, the build sheet, the travel list and the item
grid. Candidate split: one `Control` subclass per sheet tab
(`CraftSheet`, `BuildSheet`, `TravelSheet`), composed by a slimmer
`SurvivalHud`. Lower priority than #3 — it's flat and repetitive rather than
tangled, so the maintenance cost is real but not urgent. Severity 2 × blast
radius 2 = 4.

### 5. Zero netcode/integration test coverage — score 9 (testing category)
`tests/sim-core.tests` covers the deterministic core well, but
`apps/world-server/Program.cs`, `apps/gateway/Program.cs` and the client's
`WorldConnection` have no automated tests at all. Tonight's bug (missing
tile-diff backfill on join) lived exactly here and would have been caught by
a thin two-fake-clients integration test. Recommend a `tests/world-server.tests`
project that spins up a real `NetManager` server + one or two bare LiteNetLib
client sockets (no Godot needed) and asserts on wire messages — start with
"second client joining after a harvest gets a `TileChanged` backfill for it."
Severity 3 × blast radius 3 = 9. (Logged, not auto-fixed: this is a testing
gap, not itself a live multiplayer-correctness bug, so it doesn't trigger the
audit's mandatory-fix rule — but it's exactly what let tonight's real bug
through the CI unnoticed. Strong candidate for tomorrow night.)

### 6. Dead `RequestChunk` / `ChunkData` wire path — score 1
`WorldConnection.RequestChunk()` and the `ChunkReceived` event are never
called/subscribed by anything in `apps/client` — `World3D.Build()` generates
its whole radius from the seed locally instead. Leftover from the pre-3D
tilemap client. The world-server still serves `RequestChunk` correctly, so
it's inert, not broken — but it's dead protocol surface that will mislead
whoever next assumes chunk-streaming is how terrain reaches the client.
Fix: either delete the dead path, or repurpose it as the actual mechanism for
extending world radius beyond what's built at join (useful once worlds get
larger than a fixed `Radius`). Severity 1 × blast radius 1 = 1.

## Ideas (not yet scored as findings — future feature nights)

- A feature-night candidate that satisfies the vision checklist well: **corpse
  scent draws predators** (simulated, not decorative — attracts based on
  world state, not a scripted spawn), composes with the existing harvest/
  survival systems, and creates real player-vs-player tension (a kill or a
  death near you is now a liability, not just a loot pile). Deferred because
  tonight's audit found a must-fix bug first (see `LOG.md`) — the audit
  protocol says skip the feature phase entirely when that happens.
