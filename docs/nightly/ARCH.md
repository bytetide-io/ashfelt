# Ashfall — architecture map (as of 2026-08-14)

Honest snapshot for the overnight-engineer routine. See `docs/architecture.md`
for the decided, source-of-truth design; this file is a map of what actually
exists in the tree tonight, plus known debt. Update it as nights pass instead
of letting it drift from the code.

## Shape

```
apps/
  client/         Godot 4 C# client (net8.0). 3D top-down-ish third-person,
                   low-res SubViewport pixel-art look, touch-first HUD.
  gateway/         ASP.NET minimal API (net10.0). Owns accounts/characters
                   (Postgres), the static world registry, and voyage tickets.
  world-server/    Headless LiteNetLib UDP server (net10.0). One process per
                   bounded world. Authoritative position, inventory, world
                   diffs, structures, survival meters.
packages/
  sim-core/        Shared simulation rules (net8.0): terrain generation,
                   movement bounds, harvest/craft/placement rules, survival
                   meter math, day/night clock. Referenced by client AND
                   world-server — the "one shared sim library" invariant.
  shared-proto/    Wire message ids (MessageId), tuning constants (Tuning),
                   protocol version, item catalog ids (net8.0).
infra/
  migrations/      Hand-numbered, additive-only SQL migrations (001-004).
tests/
  sim-core.tests/  xunit, net10.0 test host against the net8.0 sim-core lib.
                   91 tests as of tonight. This is the ONLY automated test
                   project in the repo — see Known debt.
```

Total C# under `apps/` + `packages/`: ~5,700 lines. Small enough to read in
full in one sitting; do that before trusting a summary, including this one.

## What's real vs. what's aspirational

`docs/architecture.md` states five invariants as **decided**. As of tonight:

1. Server-authoritative, client predicts — **true**. `MovementRules.Check`
   (sim-core) runs identically on both sides; the server's `Player.Position`
   is the only truth broadcast to others.
2. World storage = seed + diffs — **true**. `WorldStore` only ever writes
   `tile_diff` and `structure` rows; `World.GenerateChunk` replays diffs over
   `TerrainGenerator` output, nothing else is persisted.
3. Character is global, map is not — **true**. Gateway owns `character`
   (inventory + 4 survival meters), world-servers hold no character state
   between sessions.
4. One shared sim library — **true**. No duplicated terrain/crafting/harvest
   logic found between client and world-server; both reference `sim-core`.
5. Interest management ("a client only ever receives entities/chunks near
   it") — **was false until tonight** for the player-position broadcast; see
   `LOG.md` 2026-08-14. Still false for `TileChanged` / `HarvestProgress` /
   `StructurePlaced`, which broadcast to every connected player regardless of
   distance (lower urgency — those are event-driven, not per-tick; logged to
   `BACKLOG.md`).

## Netcode shape

- Tick rate 15 Hz (`Tuning.TicksPerSecond`), single-threaded LiteNetLib poll
  loop in `apps/world-server/Program.cs` — no locking needed because all
  packet handling and the tick body run on one thread. This is also why two
  players interacting with the same tile/structure can't race: harvesting,
  crafting, and placement are handled synchronously in receive order.
- `ClientState` (position) is unreliable, sent at `ClientStateHz` (15 Hz) and
  budgeted server-side by elapsed wall time since the last *accepted*
  position — spamming updates cannot buy extra distance (`Player.TryAccept`).
- `PlayerStates` (broadcast) is now personalized per viewer by chunk-Chebyshev
  distance (`Tuning.InterestRadiusChunks`, currently 1 chunk / 64 m), computed
  fresh every tick — see tonight's fix. It is still O(players²) chunk-distance
  checks per tick; fine at current expected scale (tens of players per
  world), but the first thing to revisit if a single world-server needs to
  hold hundreds.
- Fire-and-forget saves (`_ = store.SaveDiffAsync(...)`, `_ = gateway.SaveCharacterAsync(...)`)
  keep a slow DB write from stalling the packet loop for everyone else. Reads
  (character load on Hello, gateway calls on join/release) are synchronous
  and deliberately so — see the comments in `Program.cs` explaining why.
- Protocol version (`ProtocolVersion.Current`) is checked at `Hello`; a
  mismatch disconnects rather than risking a garbled parse. Bump it whenever
  a message's wire layout changes — the `MessageId` enum doc comments record
  each layout.

## Persistence

- `infra/migrations/001..004` are hand-numbered, forward-only, and additive
  (new nullable/defaulted columns, never a destructive change) — there's no
  migration *framework*, just discipline and small numbers. That discipline
  has held up across 4 migrations; it will need an actual tool (or at least a
  migration-runner script) before it holds up across 20.
- No save-format version field on the JSONB `character.inventory` blob itself
  — item stacks are keyed by `ItemId` enum name (string), which is more
  robust to enum renumbering than a raw byte would be, but there's no
  explicit schema version to detect a *shape* change (e.g. adding per-item
  metadata later). Not urgent today; worth a column before inventory grows
  past a flat count map.

## Client

- `World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines) are the two large
  client files. Neither is a god-object in the coupling sense — `World3D` is
  cohesively "build and react to world state," `SurvivalHud` is cohesively
  "build and refresh the touch UI," and both are laid out in named regions.
  They're long because Godot UI-in-C# has no scene-file compaction, not
  because of tangled responsibilities. Logged to `BACKLOG.md` anyway since
  >400 lines is the audit's own bright line.
- Rendering is mobile-conscious by construction: MultiMesh batches all trees/
  shrubs/berries into a handful of draw calls, the world renders into a
  shrunk `SubViewport` (fewer shaded fragments, not a post-process blur), and
  materials are flat/toon with specular disabled.
- `RemotePlayers.Apply` used to only add players, never prune ones missing
  from a snapshot — harmless when the snapshot was "everyone," which is why
  it went unnoticed until tonight's interest-management fix made a missing
  player mean something. Fixed alongside the server change; see `LOG.md`.

## Testing

- `tests/sim-core.tests` is real and is enforced by `CLAUDE.md` as a
  pre-commit gate. It covers terrain, harvest, craft, movement, world clock,
  placement, and (as of tonight) interest-management chunk distance.
- **There is no automated test for `world-server` or `gateway` code itself**
  (`Player.cs`, `WorldStore.cs`, `Program.cs`'s message handling, voyage
  ticket claim/expiry in `gateway/Program.cs`). Tonight's interest-management
  fix was verified with a throwaway two-client LiteNetLib harness run by hand
  against a live server (see `LOG.md`) rather than a checked-in test, because
  no test project or harness for netcode existed to extend. That gap is
  itself logged to `BACKLOG.md` — the harness pattern is worth turning into a
  real `tests/world-server.tests` project on a future night.

## Where to look first next time

- `apps/world-server/Program.cs` is the top-level message dispatch and tick
  loop — start there for anything netcode-shaped.
- `packages/sim-core/World.cs` + `TerrainGenerator.cs` for anything about
  what a tile *is* and how the world is generated/diffed.
- `packages/shared-proto/Protocol.cs` for wire layout and tuning constants —
  check this before assuming a constant lives somewhere else; several (like
  `InterestRadiusChunks` before tonight) are declared here as intent even
  before the code enforces them.
