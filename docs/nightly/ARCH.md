# Ashfall — architecture map (nightly baseline)

Honest snapshot as of 2026-08-24, for the overnight-engineer routine. Update
this file when a night's change moves something described here; don't let it
drift into fiction.

See also `docs/architecture.md` (the canonical, curated doc) and
`docs/voyage-transfer.md`. This file is the messier, more operational
companion — line counts, what's actually wired up, what's stubbed.

## Processes

- **`apps/gateway`** (net10.0, ASP.NET minimal API) — accounts/characters DB,
  world registry, voyage ticket coordination. `Program.cs`, 210 lines
  (before tonight) / ~230 after. No test project.
- **`apps/world-server`** (net10.0, LiteNetLib UDP) — one process per bounded
  world. Owns the authoritative tick loop, terrain diffs, structures, and the
  in-memory `Player` for each connected peer. `Program.cs` is a 468-line
  top-level-statements file: connection lifecycle, all message handlers, and
  the tick loop live in one file (flat, not god-class-in-the-OOP-sense, but a
  single script doing routing + game rules + persistence orchestration). No
  test project.
- **`apps/client`** (net8.0, Godot 4, C#) — mobile 3D client. `World3D.cs`
  (735 lines) and `SurvivalHud.cs` (940 lines) are the two large scripts:
  `World3D` owns terrain meshing, camera, input, harvesting reticle and net
  event wiring; `SurvivalHud` owns the whole HUD/menu/inventory/crafting/
  voyage UI. Both exceed the 400-line "god script" guideline in the nightly
  brief; neither has crossed into an actual bug found tonight (see BACKLOG).

## Shared packages

- **`packages/sim-core`** (net8.0) — terrain generation, movement/placement/
  harvest/crafting rules, survival meters, world clock. This is the one
  place both client and server reference, per the project's load-bearing
  invariant #4. Covered by `tests/sim-core.tests` (9 test files, all green
  as of the last recorded run — see LOG.md history for `dotnet test` output;
  **not re-run tonight**, dotnet SDK is unavailable in this session, see
  "What I could not verify" in tonight's LOG entry).
- **`packages/shared-proto`** (net8.0) — wire message ids/protocol version,
  `CharacterState` (the JSON shape gateway↔world-server exchange over REST).

## Persistence

- Postgres, four migrations (`infra/migrations/001..004`), applied only via
  `docker-entrypoint-initdb.d` on first container boot — **there is no
  migration runner for an already-running database.** Bringing up an existing
  deployment on a newer migration requires an operator to apply the new
  `.sql` file by hand. Logged to BACKLOG.
- World state: seed + `tile_diff` rows + `structure` rows, keyed by
  `world_id`. Terrain itself is never persisted (invariant #2, respected).
- Character state: one row per device UUID in `character`
  (`inventory` JSONB + four meter columns), owned by exactly one world at a
  time via `character.owner_world_id`, transferred by the voyage-ticket
  handshake in `apps/gateway/Program.cs`.

## The bug fixed tonight (see LOG.md for the full writeup)

`character.owner_world_id` was documented (`docs/voyage-transfer.md`: "a
character is owned by exactly one world-server at a time") but only ever
*set* by the voyage-claim path. A normal join (empty ticket — the default,
non-voyage case) loaded the character by UUID with no ownership check at
all. Pointing a client at a second world-server without going through a
voyage admitted the same character twice, with two independent in-memory
inventories that would both save on disconnect — a duplication bug in the
core crafting/economy loop, and it also undermines the anti-duplication
guarantee the voyage-ticket system exists to provide. Fixed by making the
join itself claim ownership atomically (`POST /characters/{id}/claim`,
`UPDATE ... WHERE owner_world_id IS NULL OR owner_world_id = $worldId`,
`RETURNING` empty on conflict), and having the world-server reject the Hello
handshake when the claim is refused.

## Known debt not touched tonight

See `docs/nightly/BACKLOG.md` for the full scored list. Headlines:
survival "death" has no consequence (health clamps at 0 and stops, nothing
reads `SurvivalRules.IsDead`); stamina is drained by nothing (`TrySpendStamina`
is dead code); neither `gateway` nor `world-server` has a test project;
`World3D.cs` / `SurvivalHud.cs` are large multi-concern scripts; migrations
have no apply-to-existing-DB path.

## Environment note

This session's container has **no `dotnet` SDK installed** (`dotnet` is not
on `PATH`, and no `/usr/share/dotnet` or similar was found). Tonight's C#
changes are hand-verified (type/signature review, call-site grep) but were
**not compiled or test-run**. Flagged loudly in LOG.md — a human must run
`dotnet build` / `dotnet test tests/sim-core.tests` before trusting this on
a device.
