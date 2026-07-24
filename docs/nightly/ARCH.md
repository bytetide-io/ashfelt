# Ashfall — architecture map (nightly notes)

First-night snapshot. This is a supplement to `docs/architecture.md` and
`docs/voyage-transfer.md` (both are still the source of truth for
invariants) — this file is an honest, dated map of what's actually in the
tree, plus known debt, for whoever runs the next nightly session.

## Layout (as of 2026-07-24)

```
apps/client         Godot 4 mobile client (C#, net8.0)
apps/gateway        Auth-free account/character store + voyage routing (net10.0, ASP.NET minimal API)
apps/world-server   Headless authoritative region server, one instance per world (net10.0, top-level Program.cs)
packages/sim-core   Deterministic shared rules: terrain, harvest, craft, placement, survival, clock (net8.0)
packages/shared-proto  Wire message ids + Tuning constants, referenced by both client and server
infra/docker        docker-compose (postgres + world-server)
infra/migrations    Hand-numbered SQL files, run only by Postgres' first-init hook
tests/sim-core.tests  xunit, sim-core only — no netcode/integration tests exist
```

`dotnet` is **not installed in this remote sandbox** — nothing here has been
built or run tonight. All statements below are from reading the source, not
from executing it. This is exactly the caveat the nightly protocol asks to
surface loudly rather than paper over.

## Line counts (apps + packages, `*.cs`, excluding bin/obj)

Total ~5,600 lines. Above 400 (the "god script" line in the nightly brief):

| File | Lines | Note |
|---|---|---|
| `apps/client/scripts/world3d/SurvivalHud.cs` | 940 | All touch UI built in code (no `.tscn`); meters, hotbar, craft/build/travel sheets, item grid. Single class, single responsibility ("the HUD"), but knows about `WorldConnection`, `SurvivalRules`, `CraftingRules`, `PlacementRules`, `ItemCatalog` and `VirtualJoystick`. |
| `apps/client/scripts/world3d/World3D.cs` | 735 | World build (terrain mesh + foliage from seed), day/night clock, tap-to-harvest input, structure rendering, HUD wiring. One `_Ready` wires ~6 systems together. |
| `apps/world-server/Program.cs` | ~480 | Top-level statements: connection lifecycle, all wire message handling, tick loop. Not a class, so not unit-testable as-is. |
| `apps/client/scripts/WorldConnection.cs` | 421 | UDP link: send/receive for every message type. Thin per-message methods, not tangled — reads more like a protocol adapter than a god script. |

None of these are spaghetti — they're large because they're the single place
a whole vertical (HUD, world-build, netcode) lives, and the repo has
deliberately avoided splitting things prematurely per `CLAUDE.md`. Still,
`SurvivalHud.cs` and `World3D.cs` are past the point where a second occurrence
of "add one more tab / one more system" should trigger a split. See
`BACKLOG.md`.

## Netcode shape

- LiteNetLib UDP, one `NetManager` per side. Server ticks at `Tuning.TicksPerSecond`
  (15 Hz); client reports its own state at `Tuning.ClientStateHz` (15 Hz),
  unreliable — a dropped report costs nothing because the server budgets
  movement by elapsed wall time (`Player.TryAccept`), not by counting packets.
- Reliable-ordered is used for anything that must not be lost or reordered
  (Hello/Welcome, chop/craft/place/eat requests, tile/structure changes,
  corrections, voyage grant/deny). Unreliable is used only for the two things
  that self-heal on the next tick: `PlayerStates` and `StatsUpdate`.
- Movement authority: client simulates physics and reports; server never runs
  a physics engine, only bounds-checks against the shared height field
  (`MovementRules.Check` in sim-core). This matches `docs/architecture.md`
  exactly.
- Join backfill: a joining client needs three things replayed before it
  matches server state — its own Welcome, every placed **structure**, and
  every **tile diff** (harvested nodes). Only the first two existed before
  tonight; see the fixed bug in `LOG.md` — tile diffs were never backfilled,
  so a joining player saw already-harvested nodes as pristine forever.
- **Interest management is declared but not implemented.** `Tuning.InterestRadiusChunks`
  exists and the `PlayerStates` doc comment says "every player in interest
  range," but the server broadcasts every connected player's position to every
  other connected player unconditionally — there is no distance filter
  anywhere in `Program.cs`. Harmless at today's player counts; see `BACKLOG.md`.

## Persistence shape

- `world-server` never persists chunks — only `tile_diff` rows (keyed by
  chunk + local coord, upserted) and `structure` rows. Replayed into memory
  once at startup (`WorldStore.LoadDiffsAsync` / `LoadStructuresAsync`).
- `gateway` owns the `character` row (inventory as JSONB, four survival
  meters as ints) plus `voyage_ticket` for in-flight transfers. Voyage
  handoff is a three-step claim/release dance with a TTL and self-healing
  reclaim (`ReclaimExpiredTicketAsync`) — this part is careful, tested-by-reading,
  and matches `docs/voyage-transfer.md`.
- **No schema version column, no migration runner.** `infra/migrations/*.sql`
  is mounted at `/docker-entrypoint-initdb.d`, which Postgres only executes
  on a *fresh* data volume. `004_warmth.sql`'s `ALTER TABLE ... ADD COLUMN IF
  NOT EXISTS` only ever ran automatically for people who spun up Postgres
  after that file existed; anyone with an older `pgdata` volume never got it
  applied unless they ran it by hand. Every future migration has the same
  problem. See `BACKLOG.md` — this is a real "no migration path" finding,
  just not urgent while there's no real player data yet.

## Testing shape

- `tests/sim-core.tests` (10 files, ~1,000 lines) covers determinism, terrain
  shape/height, movement rules, harvest/placement/crafting rules, survival
  meters, world clock and world diff/replay semantics. This is genuinely
  good coverage for the deterministic core, and it's the part the repo is
  strictest about (`CLAUDE.md`: determinism tests are a compatibility
  contract, never "fixed" by updating expected values).
- **Nothing outside `sim-core` is tested.** `apps/world-server/Program.cs`
  (all wire handling), `apps/gateway/Program.cs` (all HTTP endpoints,
  including the voyage claim race-safety), and the client's `WorldConnection`
  have zero automated coverage. The bug fixed tonight (missing diff backfill)
  lived in exactly this untested gap, and would have been caught by even a
  thin integration test that joined two fake clients against one server.

## Dead / vestigial code

- `MessageId.RequestChunk` / `ChunkData` and the client's `RequestChunk()` /
  `ChunkReceived` event are still wired up end-to-end, but nothing calls
  `RequestChunk` — `World3D.Build()` generates its whole radius locally from
  the seed instead. Leftover from the pre-3D tilemap client
  (see git history: `b0101f2` "Pivot the client to 3D"). Harmless, but it's
  dead protocol surface that could confuse the next person who assumes chunk
  streaming is how the client gets terrain.

## What's solid

Worth saying explicitly so it isn't re-litigated by a future night: the
server-authoritative model, the fire-and-forget-off-the-hot-path save
pattern (character saves, tile diffs, structures), the voyage ticket
claim/release/reclaim dance, and the reliable-vs-unreliable channel choices
are all deliberate, already match the docs, and read as correct. Tonight's
audit went looking for exactly this kind of bug and found one real one
(diff backfill) — the rest of the netcode held up under a close read.
