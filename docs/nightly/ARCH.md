# Nightly architecture notes

Honest working map for the unsupervised overnight session. Not a replacement
for `docs/architecture.md` (the decided design doc) — this is where debt and
"how it actually behaves today" live.

## Repo shape

```
apps/client         Godot 4 client, C#, net8.0 — 3D presentation over a 2D sim grid
apps/world-server    Headless authoritative server, C#, net10.0, UDP (LiteNetLib)
apps/gateway         Accounts, characters (Postgres), voyage handoff, C#, net10.0
packages/sim-core    Deterministic rules: terrain, harvest, craft, place, survival, clock
packages/shared-proto Wire messages + Tuning constants, referenced by everyone
tests/sim-core.tests xUnit-ish tests, sim-core only — nothing else has a test project
```

Reference graph: `shared-proto` has no project references (leaf package).
`sim-core` → `shared-proto`. `world-server`/`gateway`/`client` → `sim-core` (+
`shared-proto` transitively). This means shared-proto **cannot** reference
sim-core types (e.g. `TerrainGenerator`) without a cycle — relevant if you're
tempted to derive a `Tuning` constant from a sim-core constant.

## World-server tick loop (`apps/world-server/Program.cs`)

Single-threaded `while (!shutdown.IsSet)` loop at `Tuning.TicksPerSecond` (15
Hz), driven by `Thread.Sleep`. Per tick: advance survival for every player,
broadcast position/yaw, flush dirty inventories, heartbeat stats every 2s.
Networking is LiteNetLib event callbacks (`NetworkReceiveEvent`) firing
synchronously inside `PollEvents()` — so every message handler runs on the
tick thread. `Hello` blocks the loop on a synchronous gateway HTTP call by
design (see the comment at Program.cs:114) — a slow gateway response stalls
every player in that world for the duration of one join. Fine at current
scale; worth revisiting if gateway latency ever becomes non-trivial.

**Server authority is solid.** Movement is client-simulated / server-checked
(`MovementRules.Check`, shared sim-core code, both sides literally run the
same function). Harvest, craft, eat, place all resolve against
server-held `Player.Inventory` / `World` state before any effect — the client
never gets to assert an inventory change, only request one. This matches
`docs/architecture.md` invariant #1 and I found no violation of it anywhere
in the message handlers.

**Interest management gap (fixed tonight, see LOG.md).** Before tonight,
`PlayerStates` (position/yaw, sent every tick, unreliable) was one shared
packet broadcast to every connected player regardless of distance — bandwidth
scaled with total world population, not local density, contradicting
`docs/architecture.md` invariant #5 / CLAUDE.md invariant #6. A dead constant,
`Tuning.InterestRadiusChunks = 1`, existed in shared-proto but was never
referenced anywhere — strong evidence this was a known gap, not a deliberate
choice. Fixed by filtering per-recipient in the tick loop
(`Tuning.InterestRadiusMetres`). **Not yet fixed**: `TileChanged`,
`HarvestProgress`, `StructurePlaced`, and `PlayerLeft` are still unconditional
`Broadcast()` calls to every peer in the world — lower bandwidth impact
(event-driven, not per-tick) but the same invariant gap. Logged to
`BACKLOG.md`.

## Persistence

- `WorldStore` (Postgres): tile diffs and structures only, keyed by world
  seed — matches invariant #2 (seed + diffs, never full chunks).
- `infra/migrations/*.sql` is a flat numbered sequence (001–004), applied
  in order presumably by whatever runs at container start — I did not find
  a migration *runner* (no `dotnet-ef`, no custom migrator script) in this
  pass. If migrations are applied by hand today, that's worth flagging: the
  scheme has no recorded "current version" table I could find, so I can't
  tell whether a fresh Postgres actually bootstraps to 004 automatically.
  **Not verified — flagging for a human**, not fixing tonight (out of scope,
  and I can't run Postgres in this sandbox to check).
- Character state (inventory + survival meters) lives in the gateway's
  Postgres, loaded on `Hello` and saved fire-and-forget on disconnect —
  matches invariant #3.

## Client (`apps/client/scripts`)

Two files are already past the audit's own "god script" threshold
(>400 lines / >3 systems known):

- `world3d/SurvivalHud.cs` — **940 lines**. Owns survival meters, inventory
  grid, hotbar, crafting cards, placement cards, travel list, and the
  crafting/building sheet's tab switching. At least six UI subsystems in one
  `Control`. Nothing is broken here — it's Godot idiomatic enough — but the
  next feature that touches the HUD should probably split this into
  per-tab controllers before it grows further. Logged, not fixed (fixing it
  is 400+ lines of mechanical-but-risky refactor with zero test coverage
  backing it — too large a blast radius for an unsupervised night).
- `world3d/World3D.cs` — **735 lines**. Owns terrain mesh build, foliage
  placement (trees/shrubs/berry bushes), day/night sky, input (tap-to-harvest
  raycasting via reticle), structure spawning, and wiring between
  `WorldConnection`, `SurvivalHud`, `PlayerBody`, `RemotePlayers`. Same
  verdict: a real god-object by the letter of the audit rubric, but
  functionally coherent (it's "the world scene's root controller") and
  risky to split without any integration test harness. Logged.

`WorldConnection.cs` (421 lines) is the third file over 400 — it's a fairly
flat message-dispatch class (one method per wire message), so less concerning
than the two above; splitting it would mostly move code around rather than
reduce real coupling.

No `get_node("../../...")`-style path coupling — this is C#/Godot, so the
equivalent smell would be reaching into scene tree children via `GetNode<T>`
with a hardcoded path. Found NodePath *exports* (`HudPath`, `StatusPath` on
`World3D`) which is the idiomatic C# Godot pattern (inspector-wired, not a
hardcoded string), so this is fine — not the anti-pattern the audit is
looking for.

## Mobile performance

Not measured tonight — no device, no Godot editor GUI in this sandbox (headless
container, no `dotnet` SDK even present — see LOG.md "Not verified"). The
`docs/architecture.md` design (SubViewport downscale + baked pixel textures)
is architecturally sound for the stated goal (fewer shaded fragments, not a
post-process), but frame time / draw calls / overdraw need a human on a real
or emulated device.

## Testing

`tests/sim-core.tests` is the only test project in the repo. It covers
generation determinism, crafting, harvest, movement, placement, survival,
world-clock, terrain shape — i.e. everything in `sim-core`. **Nothing tests
`apps/world-server`** — no coverage of the tick loop, message handlers,
interest management, or netcode. Tonight's fix (interest-filtered broadcast)
therefore has *zero* automated coverage; it was hand-reviewed only (see
LOG.md "Verified" / "Not verified"). This is the single biggest testing gap
in the repo and is logged to `BACKLOG.md`.
