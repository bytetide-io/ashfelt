# Nightly backlog

Ranked ideas and known issues not yet built, scored `Severity(1-5) x Blast
radius(1-5)` where scoring was done as part of an audit. Newest entries on
top within each night's block.

## 2026-08-03

- **World-server tick loop blocks on synchronous gateway HTTP calls**
  (`Hello` character load, `RequestRelease` save+mint) via
  `.GetAwaiter().GetResult()`. Every player in a world stalls for the
  duration of one player's join/voyage if the gateway is slow or briefly
  unreachable. Deliberate tradeoff, explained in `apps/world-server/Program.cs`
  comments (avoids cross-thread mutation of inventory the tick loop reads).
  Score: Severity 3 x Blast radius 4 = **12**. Not fixed tonight — the honest
  fix is an async request queue processed off the network-receive callback,
  which is a bigger redesign than a single-finding night should take on
  without a recorded decision in `docs/`. Revisit if the gateway ever runs
  somewhere with real network latency instead of localhost.

- **No automated tests for `apps/world-server` or `apps/gateway`.** Only
  `packages/sim-core` has a test project. The gateway's voyage
  mint/claim/reclaim SQL is exactly the kind of race-sensitive logic that
  breaks silently under refactor with nothing to catch it.
  Score: Severity 3 x Blast radius 4 = **12**. Needs a Postgres-backed
  integration test project (`tests/gateway.tests` or similar, using
  `infra/docker/docker-compose.yml` or Testcontainers) — more setup than one
  night justifies starting cold. Suggest dedicating a full night to this
  before Phase 3 (voyages) is called done.

- **`World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines) are god
  scripts** — each single node owns more than three systems (World3D: terrain
  build, foliage, day/night clock, structures, tap-input; SurvivalHud:
  meters, inventory, crafting, placement, travel, hotbar). Both are cleanly
  subdivided internally and not currently causing bugs, so this didn't clear
  tonight's fix-it bar, but it's the shape that turns into a merge-conflict
  magnet as more people touch UI/world-building code.
  Score: Severity 2 x Blast radius 3 = **6** each. Fix by extracting child
  scenes/nodes with their own scripts — e.g. `SurvivalHud` splits cleanly into
  a meters panel, an inventory grid, and a crafting/building/travel sheet;
  `World3D` splits into a foliage builder and a day/night controller.

- **Structure and tile-change broadcasts (`StructurePlaced`, `TileChanged`,
  `HarvestProgress`) are still global**, unfiltered by distance — the same
  interest-management gap fixed for `PlayerStates` tonight still applies to
  world-state events. Lower frequency (only on actual world changes, not
  every tick) so it didn't score as high, but it's the same invariant.
  Score: Severity 2 x Blast radius 3 = **6**. Fix alongside a future pass that
  also gives the world-server a spatial index, since naive per-recipient
  distance filtering for world objects (as opposed to the small live player
  set) could get expensive with many structures.

- **`RequestChunk` has no bounds/rate check.** A client can request any
  chunk coordinate, and the server will always generate and send it. Cheap
  and harmless today (deterministic, no secret data), but worth bounding
  before internet-facing.
  Score: Severity 2 x Blast radius 2 = **4**. Low priority.

## Phase 4 / gameplay-roadmap ideas (from `docs/gameplay-roadmap.md`, not
re-scored here — see that doc for the authoritative list)

Not re-audited tonight; see `docs/gameplay-roadmap.md` for the standing
feature roadmap (item icons, audio, balancing, mobile UI pass).
