# Nightly backlog

Ranked ideas and findings not yet built/fixed, newest audit first. Score is
Severity(1-5) × Blast radius(1-5) at the time it was logged — re-score if you
suspect the codebase has moved since.

## From 2026-07-24 audit

### Multiplayer correctness

- **[FIXED 2026-07-24]** ~~No ownership check on normal (non-voyage)
  character load, letting the same character UUID be loaded concurrently by
  two sessions~~ — see `LOG.md`. Leaving the entry struck through rather than
  deleted per the "never silently repeat/undo, and log what you found"
  discipline — this is the one thing tonight's session fixed.

- **[FIXED 2026-08-05]** ~~World-server blocks its single packet/tick thread
  on gateway HTTP calls~~ (`apps/world-server/Program.cs` `Hello` and
  `RequestRelease`, both used to call `.GetAwaiter().GetResult()` on an HTTP
  round trip). Was Severity 4 × Blast radius 5 = **20**. This is exactly the
  dedicated session the 2026-07-24 entry below asked for — see tonight's
  `LOG.md` entry for the fix (background `Task` + drained completion queue +
  `JoinPending`/`Releasing` gating + a `claimingCharacters` reservation to
  close a fast-reconnect race an independent review caught mid-session).

### Mobile performance

- **`World3D.UpdateGatherPrompt` (`apps/client/scripts/world3d/World3D.cs:491-559`)**
  runs an unconditional 7×7-tile scan every physics tick (60/sec), each tile
  costing a multi-octave FBM height sample plus 1-3 `Noise.Hash` calls via
  `sim-core.TerrainGenerator`. Runs even while standing still and even while
  the action sheet hides the visual prompt. Score: Severity 4 × Blast radius
  3 = **12**. Fix: cache the player's current tile coordinate and early-return
  when it hasn't changed since the last physics frame; skip the scan entirely
  while the action sheet is open.

### Architecture / maintainability

- **`SurvivalHud.cs` is 940 lines** (`apps/client/scripts/ui/SurvivalHud.cs`
  per the client audit — grown from the 419 cited in
  `docs/gameplay-roadmap.md` §3.4, not shrunk). One class builds 4 survival
  meters, the day/night chip, touch stick + jump button, hotbar, gather
  prompt, and a full tabbed action sheet (inventory/craft/build/travel) with
  its own tab state machine and business logic (`RefreshActionAvailability`
  calling `CraftingRules.CanCraft` directly). No reusable meter/slot widget
  exists — `AddMeter`, `HotbarSlotFor`, `GridSlot`, `BuildCraftCard`,
  `BuildPlaceCard` each hand-build near-identical layouts. Score: Severity 3
  × Blast radius 3 = **9**. Fix: extract a `MeterRow` and one `ItemSlot`
  widget reused by hotbar/inventory/craft/place; split the action sheet into
  its own scene/controller (roadmap §3.4).

- **`World3D.cs` is 735 lines**, up from 616, mixing net dispatch, day/night
  lighting, procedural foliage generation, touch-input classification,
  harvest-target scanning, and structure placement/mesh building in one
  script. Score: Severity 3 × Blast radius 3 = **9**. Fix: split per roadmap
  §3.4 into separate nodes/systems World3D composes.

- **`WorldConnection.cs:299-420` `OnReceive`** is a 12-case hand-decoded
  switch on `MessageId` with wire order that must exactly match the server's
  writer with no compile-time check. Score: Severity 3 × Blast radius 3 = **9**.
  Fix: per-message `Read`/`Write` static pairs in `shared-proto` (roadmap
  §3.2), then a dispatch table here.

- **`World3D.CollectTrees`/`CollectShrubs`/`CollectBerryBushes`
  (`apps/client/scripts/world3d/World3D.cs:325-436`)** triplicate the same
  loop/sample/jitter/place pattern, differing only in density constant and
  offset scale. Score: Severity 3 × Blast radius 2 = **6**. Fix: one
  `CollectFoliage(TileType, perTile, saltBase, place)` helper — due per
  CLAUDE.md's "third occurrence is a refactor," and Phase C will add more
  foliage types on this same pattern.

- **Inconsistent node-path coupling**: `PlayerBody.cs:46` uses a raw
  `GetNode<Node3D>("../CameraRig")` relative path while `World3D.cs` uses
  exported `NodePath` fields for some lookups and hardcoded string
  `GetNode<T>(...)` for others in the same method. Score: Severity 2 ×
  Blast radius 2 = **4**. Fix: exported `NodePath` fields consistently.

### Persistence

- **No formal migration runner/version field** — `infra/migrations/*.sql`
  are numbered files applied once via Postgres `initdb.d`, not replayed
  against a live DB. The additive `ALTER TABLE ... ADD COLUMN IF NOT EXISTS
  ... DEFAULT` convention has held so far (used for `owner_world_id` and
  `warmth`) but there's no tooling enforcing it and no way to apply a new
  migration to an already-running Postgres without a manual `psql` step. Not
  scored (no severity yet — no incident), but worth a real migration tool
  (DbUp, Flyway, or even a tiny hand-rolled `schema_version` table + runner)
  before this ships with real player data.

### Testing

- **No test coverage at all for `apps/gateway` or `apps/world-server`** — only
  `sim-core` has a test project. Neither the 2026-07-24 ownership-claim fix
  nor tonight's async-claim rework has an automated regression test; both
  were verified by manual code review only (see `LOG.md` — no dotnet SDK
  available either night). A future night should add an integration test
  project that spins up the gateway against a real (or testcontainers)
  Postgres and exercises the claim/voyage/save endpoints directly, including
  the concurrent-claim race both fixes targeted — and ideally a fake/mock
  `GatewayClient` the world-server side can use to test `JoinPending`/
  `Releasing`/`claimingCharacters` behavior (slow response, disconnect
  mid-claim, fast reconnect) without a real HTTP round trip at all.

## From 2026-08-05 audit

Ran Phase 1 by re-checking every item below against the current code rather
than a full fresh sweep — the one item that had crossed both fix-tonight
thresholds (blocking gateway calls, previous section) was confirmed still
present and fixed; see `LOG.md`. Did **not** do a dedicated audit pass over
the building/blueprint/architect-mode system, which landed entirely after
the 2026-07-24 audit (commits `9dc784d`..`6731ba0`, ~15 commits: `BuildSite`,
`StructureCatalog`, blueprint protocol + server handlers + persistence,
architect-mode client UI, roof shelter, collision, animated player body).
That's real new surface area — server-authoritative validation
(`BuildingRules.Validate`), a new persistence table, four new message types —
that hasn't had a multiplayer-correctness pass yet. Flagging for next night's
Phase 1 rather than attempting it as a rushed add-on tonight.

- **God scripts have grown further, not shrunk**: `World3D.cs` 735→**1103**
  lines, `SurvivalHud.cs` 940→**1124** lines (both up ~50% since the
  2026-07-24 count), `WorldConnection.cs`'s hand-decoded switch now **534**
  lines total. Re-score next time one of these is touched — likely higher
  than the 9s logged in 2026-07-24, since blast radius grows with every
  feature that adds another branch to `OnReceive` or another tab to the
  action sheet instead of composing existing widgets. Still not attempted:
  matches roadmap §3.4, same fix as logged above, just more overdue.

## Rejected feature ideas (logged per Phase-2 discipline, not built — audit
found a fix-tonight-caliber bug first, so Phase 2 wasn't reached)

**2026-08-05:** Same as below — the backlog's carried-over blocking-gateway-
calls finding (Severity 4 × Blast radius 5 = 20, multiplayer-correctness ≥9)
was confirmed still present, so per the routine's decision rule Phase 2 was
skipped again without generating or rejecting any feature ideas.

**2026-07-24:** None — Phase 2 (new feature) was skipped tonight because the
audit surfaced a multiplayer-correctness finding scoring 20 (over the 15
threshold, and over the 9 threshold on the multiplayer-correctness track),
which the routine's decision rule requires fixing instead of building new
content.
