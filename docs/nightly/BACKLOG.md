# Nightly backlog

Ranked ideas and findings not yet built/fixed, newest audit first. Score is
Severity(1-5) × Blast radius(1-5) at the time it was logged — re-score if you
suspect the codebase has moved since.

## From 2026-08-25 audit

### Multiplayer correctness

- **[FIXED 2026-08-25]** ~~`apps/world-server/Program.cs`'s message dispatch
  (`NetworkReceiveEvent`) had no exception guard around parsing, so any
  malformed or truncated packet on any message type could throw an
  unhandled exception straight out of the single tick loop and crash the
  whole process — every connected player on that world, not just the
  sender.~~ Score: Severity 5 (total process crash) × Blast radius 5
  (every connected player) = **25**. Found while reading the new
  `CommitBlueprint` handler (the first message with a client-declared loop
  count parsed field-by-field, which makes the read-past-the-buffer case
  easy to reach), but the exposure was pre-existing on every handler, not
  new to blueprints. Fixed by wrapping the parse+dispatch in a try/catch
  that logs and disconnects the offending peer — see `LOG.md`.

Carried forward, unchanged, from the 2026-07-24 audit below: the
Hello-blocking-the-tick-loop finding (score 20) is still open — still a
real architecture change, not a one-night fix, and doubly so with no
compiler available to verify an async restructuring. Not re-attempted
tonight for the same reason the last session gave.

### Testing

- Still true, and now covers more surface: `apps/world-server` and
  `apps/gateway` have zero automated tests, including the new
  `BuildSite`/`BuildingRules` server-authority checks (`Owner`/reach/
  support/stockpile) exercised by `CommitBlueprint`/`DepositRequest`/
  `BuildRequest`/`CancelBlueprint`, and now the packet-parse crash guard
  fixed tonight has no regression test either. `packages/sim-core`'s own
  new `BuildSiteTests.cs`/`BuildingRulesTests.cs`/`StructureCatalogTests.cs`
  are solid — this gap is specifically the server/gateway integration
  layer, same as logged 2026-07-24.

### Architecture / maintainability

- `apps/world-server/Program.cs` is now 759 lines (was 468 on 2026-07-24,
  before blueprint building). Not re-scored tonight — no new finding, just
  flagging that the top-level-statements single-file server is growing
  every feature adds a case to the same switch, same as the client
  god-scripts already logged below.

## From 2026-07-24 audit

### Multiplayer correctness

- **[FIXED 2026-07-24]** ~~No ownership check on normal (non-voyage)
  character load, letting the same character UUID be loaded concurrently by
  two sessions~~ — see `LOG.md`. Leaving the entry struck through rather than
  deleted per the "never silently repeat/undo, and log what you found"
  discipline — this is the one thing tonight's session fixed.

- **World-server blocks its single packet/tick thread on gateway HTTP calls**
  (`apps/world-server/Program.cs:121,144,322-325` — `Hello` and
  `RequestRelease` both call `.GetAwaiter().GetResult()` on an HTTP round
  trip). Score: Severity 4 × Blast radius 5 = **20**. A slow or unreachable
  gateway freezes movement/harvest/tick broadcasts for *every* connected
  player, on every join and every voyage — this is not a rare edge case, it's
  the common path. Not fixed tonight: the correct fix is a real architecture
  change (kick off the HTTP call as a real `Task`, park the connecting peer
  in a "pending" state that only skips gameplay messages, and drain
  completions from a thread-safe queue on the next tick so `PollEvents()`
  cadence for everyone else is never blocked). That's a bigger, riskier
  change than "ship one thing and be done" allows for a single night, and it
  deserves its own dedicated session with room to actually reason about the
  pending-state lifecycle (what happens if the peer disconnects mid-claim,
  etc.) rather than being squeezed in alongside another fix.

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
  `sim-core` has a test project. The ownership-claim fix landed tonight
  (`POST /characters/{id}/claim`) has no automated regression test; it was
  verified by manual code review only (see `LOG.md` — no dotnet SDK available
  this session). A future night should add an integration test project that
  spins up the gateway against a real (or testcontainers) Postgres and
  exercises the claim/voyage/save endpoints directly, including the
  concurrent-claim race this fix targets.

## Rejected feature ideas (logged per Phase-2 discipline, not built — audit
found a fix-tonight-caliber bug first, so Phase 2 wasn't reached)

None yet — Phase 2 (new feature) was skipped tonight because the audit
surfaced a multiplayer-correctness finding scoring 20 (over the 15 threshold,
and over the 9 threshold on the multiplayer-correctness track), which the
routine's decision rule requires fixing instead of building new content.
