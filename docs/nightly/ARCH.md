# Ashfall — architecture map (nightly notes)

Honest snapshot as of 2026-07-29. This supplements `docs/architecture.md` and
`docs/voyage-transfer.md` (read those first — they're the decided design).
This file is the "as-built" view: what's actually wired up, what's stubbed,
and where the debt is. Update it whenever a nightly session changes the shape
of something.

## Repo shape

```
apps/
  client/        Godot 4 (C#, net8.0) — 3D mobile client
  world-server/  net10.0 — authoritative sim per region, UDP (LiteNetLib)
  gateway/       net10.0 — ASP.NET Minimal API, accounts/characters/voyage
packages/
  sim-core/      net8.0 — shared rules: terrain, harvest, craft, survival,
                 movement, placement. Referenced by both client and server.
  shared-proto/  net8.0 — wire message ids, ItemId, Tuning constants
tests/
  sim-core.tests/  the only test project in the repo
infra/
  migrations/    hand-numbered SQL, applied in order, no runner/tracking table
  docker/        world-server Dockerfile + compose
```

Line counts (2026-07-29), for future god-script tracking:

| file | lines |
|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 |
| `apps/client/scripts/world3d/World3D.cs` | 735 |
| `apps/world-server/Program.cs` | 468 |
| `apps/client/scripts/WorldConnection.cs` | 421 |
| `apps/world-server/Player.cs` | 220 |
| `apps/gateway/Program.cs` | 210 |

## What's actually wired up vs. designed-but-stubbed

- **Chunk streaming is not implemented.** `MessageId.RequestChunk` and
  `WorldConnection.RequestChunk` exist in the protocol/client, but nothing
  ever calls `RequestChunk` after the initial connect. `World3D.Build()`
  builds a fixed `Radius = 2` (5×5) chunk area around the origin once, at
  welcome, and never streams more as the player walks. So today's "world" is
  a small fixed slice, not an open map — the roaming/streaming design in
  `docs/architecture.md` is aspirational, not yet true. This matters for any
  future interest-management work (below): there's no chunk-subscribe
  lifecycle to hang it on yet.
- **Structures have no interest management at all**, and neither do
  player-state broadcasts. See BACKLOG — this is tonight's headline finding.
- **Voyage transfer (Phase 3, instant)** is fully wired: gateway mints/claims
  tickets transactionally (`ON CONFLICT` + `DELETE ... RETURNING` in one
  statement), self-heals an expired ticket back to the origin world on next
  load or claim attempt. No test coverage exists for this path (see BACKLOG).
- **Persistence**: tile diffs and structures use upsert-by-natural-key SQL,
  replayed on world-server startup (`WorldStore.LoadDiffsAsync` /
  `LoadStructuresAsync`). Character state (inventory + 4 survival meters)
  lives in the gateway's `character` table as JSONB + int columns. Migrations
  are plain sequential `.sql` files (`001_init` … `004_warmth`), applied by
  hand or by whatever runs them — there is no migration-tracking table, so
  "which migrations has this DB seen" is tracked by nothing. `004_warmth.sql`
  is a good example of a safe additive migration (`ADD COLUMN ... DEFAULT`).
- **Determinism contract**: `sim-core` is genuinely disciplined about this —
  integer fixed-point survival meters, `Noise.Hash`-seeded jitter, no
  `DateTime.Now`/float accumulation in the shared library. This is the
  strongest part of the codebase and matches CLAUDE.md's invariants closely.
- **Server-authoritative surface**: movement is client-predicted /
  server-validated (`MovementRules.Check`, shared). Crafting, harvesting,
  placing, eating, and voyage release are all server-decided from a client
  *request* message — verified by reading `apps/world-server/Program.cs`
  end to end. No client-authoritative gameplay state found.
- **Single-threaded server loop**: LiteNetLib's `PollEvents()` and the tick
  loop run on the same thread in `Program.cs`, so there is no data race
  between "a packet arrived" and "the tick advanced" — two players hitting
  the same tree in the same tick are serialized by construction, not by a
  lock. This is worth keeping in mind before ever moving parts of this to a
  thread pool.

## Known debt (see BACKLOG.md for scored, actionable items)

- No test project for `world-server` or `gateway` — only `sim-core` has
  tests. The voyage-transfer ticket/ownership logic (the part explicitly
  called out in `docs/voyage-transfer.md` as needing to survive a crash
  without duplicating a character) has zero automated coverage.
- Two client files exceed the 400-line "god script" guideline
  (`SurvivalHud.cs` 940, `World3D.cs` 735). Both are single-purpose
  orchestrators (HUD-builder, world-builder) rather than tangled logic, so
  the risk is lower than the raw line count suggests, but they're due a look.
- A handful of `GetNode("../Player")`-style sibling-path lookups
  (`OrbitCamera.cs:29`, `PlayerBody.cs:46`, `DebugCapture.cs:40`) couple
  scene layout to script internals. Small blast radius (3 call sites, one
  scene), not urgent.
- `infra/migrations` has no tracking table — nothing prevents a migration
  from being re-run or skipped by accident on a real deploy.

## Tooling note for future nightly sessions

This session's sandbox had **no .NET SDK and no running Docker daemon**, and
the outbound proxy denies `builds.dotnet.microsoft.com` (org egress policy,
confirmed via `/__agentproxy/status` — a `connect_rejected` / 403 policy
denial, not a transient failure). That means **no build, no test run, no
verification of any C# change was possible this session.** If a future
nightly session hits the same wall, don't try to route around the policy
denial — audit-and-log is the correct fallback, not shipping unverified code.
