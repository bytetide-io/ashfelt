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

## From 2026-08-01 audit

### Multiplayer correctness / security

- **[FIXED 2026-08-01]** ~~No authentication at all on `/characters/*` or
  `/voyage*` — any caller who could reach the gateway's HTTP port could
  claim, read or overwrite any character, or mint/claim voyage tickets for a
  character they don't own~~ — see `LOG.md`. Independent of, and composes
  with, the 2026-07-24 ownership-exclusivity fix above (that fix stops two
  world-servers holding one character at once; this fix stops an
  unauthenticated caller from acting as a world-server in the first place).

- **Interest management (invariant #6) is unimplemented.** `Tuning.InterestRadiusChunks`
  is declared in `packages/shared-proto/Protocol.cs` but never read anywhere.
  `PlayerStates`, `TileChanged`, `HarvestProgress` and `StructurePlaced` all
  broadcast to every connected client regardless of distance
  (`apps/world-server/Program.cs`). Not scored as multiplayer-*correctness*
  (nothing behaves wrong) but as bandwidth/scale debt — fine at today's
  player counts, a real cost once a world has more than a handful of
  concurrent players. Severity 3 × Blast radius 3 = **9**.

### Data integrity

- **Fire-and-forget world-state DB writes swallow failures silently.** The
  tile-diff and structure persistence calls in `apps/world-server/Program.cs`
  (around the chop/place handlers) have no try/catch or log, unlike the
  character-save fire-and-forget right next to them in the same file, which
  does log on failure. A failed diff/structure write today is invisible —
  the in-memory world state stays correct until restart, then the write
  silently never happened. Severity 3 × Blast radius 3 = **9**. Fix: wrap
  both in the same try/catch-and-log pattern already used for character
  saves.

### Architecture / maintainability

- **`PlacementRules.Blocks` bypasses `ItemCatalog`** (`packages/sim-core/PlacementRules.cs`),
  the single-source-of-truth pattern the rest of the file and
  `HarvestRules`/`CraftingRules` already follow. Severity 1 × Blast radius 3
  = **3**. Fix: move the blocking-tile data into `ItemCatalog`/`StructureCatalog`
  alongside the other per-item/per-structure data.

- **`RequestChunk`/`ChunkData` is dead protocol.** The client never streams
  chunks over the wire — it builds a fixed local radius client-side instead
  (`apps/client/scripts/WorldConnection.cs`, `apps/world-server/Program.cs`).
  Either wire it up for real streaming (needed eventually for worlds bigger
  than the fixed local radius) or remove the dead message types. Severity 2
  × Blast radius 3 = **6**.

- **`World.HasWarmthNear` is an unindexed linear scan** of every structure in
  the world, called once per player per tick (15 Hz)
  (`packages/sim-core/World.cs`, called from `apps/world-server/Program.cs`).
  Fine at current structure counts; will need a spatial index (grid bucket by
  chunk, same as tile diffs) once worlds accumulate more building. Severity 2
  × Blast radius 3 = **6**.

- **`DebugCapture.cs` still calls `GetCamera2D()`** in the now-3D client —
  capture framing options silently no-op since the client's move to 3D.
  Severity 1 × Blast radius 2 = **2**.

Note: this session's own audit independently re-found the blocking-gateway-
HTTP-calls issue (20, above) and the `SurvivalHud`/`World3D` god-script sizes
(9 each, above) already tracked from 2026-07-24 — not re-listed with a
second score, since nothing about them changed.

## Rejected feature ideas (logged per Phase-2 discipline, not built — audit
found a fix-tonight-caliber bug first, so Phase 2 wasn't reached)

None from 2026-07-24 — Phase 2 (new feature) was skipped that night because
the audit surfaced a multiplayer-correctness finding scoring 20 (over the 15
threshold, and over the 9 threshold on the multiplayer-correctness track),
which the routine's decision rule requires fixing instead of building new
content. Same outcome 2026-08-01, same reason, different finding (see
above) — Phase 2 has not been reached in either session run so far.
