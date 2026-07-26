# Nightly backlog

Ranked by `Severity(1-5) × BlastRadius(1-5)`, descending. Scores are from the
2026-07-26 audit (see `LOG.md`); re-score if the surrounding code has changed
significantly since.

## 1. No dedupe on character ownership — Sev 4 × Blast 4 = 16

`apps/world-server/Program.cs` `Hello` handler never checks whether
`player.CharacterId` is already held by a live `Player` in `players.Values`
before loading it, and a normal (non-voyage, empty-ticket) join never touches
an ownership/version field at all. `apps/gateway/Program.cs`'s
`PUT /characters/{id}` is an unconditional upsert with no `owner_world_id` or
version-column guard. A relaunch mid-network-blip, a cloned device UUID, or a
connection lingering past a voyage grant can produce two live copies of one
character; whichever disconnects last silently overwrites the gateway record
— duplicate or lost items. Undermines invariant #3 ("owned by exactly one
world-server at a time"), which the *voyage* path enforces carefully via
ticket-claim but the *normal join* path does not enforce at all.

Why not fixed tonight: needs a schema change (an `owner_world_id` or version
column, checked on every PUT) plus deciding the failure mode for a rejected
double-join — bigger and riskier than a single-session fix, and CLAUDE.md
requires migrations for save-format changes to be written *and tested*
against a prior-format save, which isn't something to rush.

## 2. Interest management never enforced — Sev 4 × Blast 4 = 16

`Tuning.InterestRadiusChunks` (`packages/shared-proto/Protocol.cs`) is
declared but never read in `apps/world-server`. Every broadcast —
`PlayerStates` (15 Hz), `TileChanged`, `HarvestProgress`, `StructurePlaced` —
goes to all connected peers regardless of distance. Bandwidth is O(N) per
player / O(N²) total (~3 KB/s/player at N=10 for positions alone, before
event traffic). Directly violates a named invariant in `docs/architecture.md`
(#6), not just a perf nit.

Why not fixed tonight: correct enforcement needs an explicit per-client
"interested set" with add/remove semantics as players and structures move in
and out of range (not just a distance filter on `Broadcast`), which touches
every message type that currently assumes global broadcast. Wants its own
focused session and a test plan, not a bolt-on at 2am.

## 3. Zero automated tests for world-server / gateway — Sev 4 × Blast 4 = 16

`tests/sim-core.tests` covers `packages/sim-core` well. Nothing covers the
Hello/voyage/reconnect state machine, the fire-and-forget save-on-disconnect
path, the gateway's ticket-claim race (`rows==0` conflict path), or
`WorldStore`'s real Postgres-backed load/save (only the in-memory `World` is
covered by `WorldTests.ReplayingStoredDiffs`). This is exactly the layer the
project's own audit criteria weight highest, and it's the layer with the
least safety net.

Why not fixed tonight: needs an integration-test harness for LiteNetLib +
Postgres (likely testcontainers or an in-memory Npgsql substitute) that
doesn't exist yet — infrastructure work, not a one-file fix. Good candidate
for a dedicated night.

## 4. No migration-tracking mechanism — Sev 3 × Blast 4 = 12

`infra/docker/docker-compose.yml` mounts `infra/migrations` as
`docker-entrypoint-initdb.d`, which Postgres runs once, only against a fresh
empty volume. There's no `schema_migrations` table or runner. A live
deployment never re-applies `002_character.sql`/`003_voyage.sql`/
`004_warmth.sql` after the first boot — the first symptom would be a runtime
SQL error on the next voyage or warmth tick after a deploy that assumed the
new column existed.

Why not fixed tonight: needs picking a migration runner/convention (or
hand-rolling a minimal one) and is an infra decision, not a code bugfix —
worth deciding deliberately, not as a side effect of an unrelated fix.

## 5. Protocol-mismatch has no user-facing signal — Sev 3 × Blast 4 = 12

The wire protocol *is* versioned and a mismatch is rejected correctly
server-side (`Program.cs` `Hello`: disconnects if `clientVersion !=
ProtocolVersion.Current`). But `apps/client/scripts/WorldConnection.cs`
treats any disconnect as transient and retries forever with a generic
"Disconnected… Reconnecting…" message — there's no way for a player on a
stale build to learn they need to update. Fires on every server protocol
bump, for every client still on the old build — not an edge case for a
mobile-distributed app.

Why not fixed tonight: not a multiplayer-correctness bug (server behaves
correctly), and the real fix is a small protocol addition (server tells the
client *why* it was disconnected) plus client UI — reasonable scope for a
future night, not urgent enough to bump tonight's single-item fix.

## 6. `SurvivalHud.cs` rebuilds inventory UI on every dirty tick — Sev 3 × Blast 3 = 9

`SurvivalHud.cs` (`RefreshInventory` → `RefreshGrid`/`RefreshHotbar`)
`QueueFree`s and reconstructs up to 24 slots from scratch every time the
server flushes `InventoryDirty`, which happens on every harvest strike — up
to 15 Hz while actively gathering, on a mobile device, during the game's core
loop.

## 7. `HarvestRules` has no dedicated unit tests — Sev 2 × Blast 3 = 6

Only exercised indirectly via `WorldTests.cs` (bare-handed + one Axe-tier-1
case). Tool-tier stacking, mismatched-tool-class, and the "never below 1
strike" floor at high tiers are untested; a regression here would silently
over/under-reward every player holding a tool.

## 8. `SurvivalHud.cs` is a 940-line file owning ~10 UI responsibilities — Sev 2 × Blast 3 = 6

Meters, day/night chip, hotbar, joystick/jump anchoring, gather prompt, and a
four-tab action sheet, all built imperatively in one class with no
scene/sub-control decomposition. No leaks or sim-core duplication found, but
it has absorbed every feature added since Phase 2 and will keep growing.

## 9. Day/night phase formula duplicated — Sev 2 × Blast 2 = 4

`SurvivalHud.cs` (`UpdateDayChip`) and `World3D.cs` (`UpdateSky`) each
independently compute `sin((timeOfDay-0.25)*Tau)`. Presentation-only, but
exactly the "third occurrence is a refactor" pattern the project's own
conventions call out — worth a shared helper before a third caller appears.

---

## Non-findings worth recording (so a future night doesn't re-derive them)

- **World3D.cs has no client-authoritative logic that should be
  server-side.** Every gather/place/eat is a request the server
  independently re-validates (`IsWithinReach`, `world.TryHarvest`,
  `world.TryPlace`). Correct per invariant #1.
- **`MovementRules`/`Player.TryAccept` budget by elapsed *accepted* time**,
  not wall-clock since last packet — so spamming updates can't buy distance.
  No exploitable teleport path found.
- **There is no combat, damage, or PvP system anywhere in this codebase.**
  Verified by grep (`Combat|PvP|\bDamage\b|\bAttack\b`) across `apps/` and
  `packages/` — zero implementation hits. If a future brief assumes combat
  exists or asks to extend it, that's net-new architecture, not a tweak.
