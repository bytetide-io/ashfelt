# Ashfall — architecture notes (nightly engineer's map)

Written on the first nightly run, 2026-08-03. This is a working map for future
nightly sessions, not a replacement for `docs/architecture.md` /
`docs/voyage-transfer.md` — read those first; they are the source of truth for
decided invariants. This file is the honest, occasionally blunt version:
what's actually there, what's thin, what's a trap.

## Shape (as built, not just as documented)

```
apps/client         Godot 4 client, C#, net8.0 — 3D, mobile/touch
apps/world-server    Headless UDP server (LiteNetLib), net10.0 — one per region
apps/gateway         ASP.NET minimal API, net10.0 — accounts, voyage tickets
packages/sim-core    Shared rules: terrain, harvest, craft, placement, survival, movement
packages/shared-proto Wire enum (MessageId), Tuning constants, ItemId, CharacterState
tests/sim-core.tests  The only test project in the repo
infra/                docker-compose (Postgres), SQL migrations
```

Total game code: ~5,600 lines of C# across 30 files. Small, young codebase —
Phase 3 (persistent characters + voyages) is in progress per `README.md`.
Nothing here is legacy; there is no accumulated cruft yet, which is unusual
and worth preserving.

## What's genuinely good here

- **sim-core is real and disciplined.** Deterministic hashing throughout
  (`Noise.Hash`), no platform RNG, `ItemCatalog` as a real data table instead
  of switch statements, and every rule file (`MovementRules`, `HarvestRules`,
  `CraftingRules`, `PlacementRules`, `SurvivalRules`) has a matching test file
  in `tests/sim-core.tests`. This is the part of the codebase safest to build
  on.
- **The voyage/gateway handshake is correctly concurrency-safe.** Ticket mint
  and claim in `apps/gateway/Program.cs` use `WITH ... RETURNING` CTEs so a
  race between two claims resolves via the database, not application logic —
  `rows == 0` after the update means "someone else already claimed this,"
  handled explicitly. Expired-ticket self-heal (`ReclaimExpiredTicketAsync`)
  means a crash mid-voyage doesn't strand a character; the player just
  reconnects and loads normally. This is the most carefully-reasoned code in
  the repo — read it before touching voyage logic.
- **World storage is honestly seed+diffs.** `World.cs` never persists a tile
  unless it was struck; `WorldStore.LoadDiffsAsync` replays diffs over
  regenerated terrain on boot. `TryHarvest` keeps partial-strike progress
  in-memory only (never a diff), so a half-chopped tree stands whole again
  after a restart — correct per the seed+diffs invariant.
- **Fire-and-forget saves are deliberate and explained.** Character saves on
  disconnect and diff/structure writes are `_ = Task.Run(...)` / un-awaited so
  a slow DB write never stalls the tick loop for everyone else. The tradeoff
  is explained in comments, not just assumed.

## What's thin or a trap

- **Interest management existed as a declared intent, not an implementation**
  — `Tuning.InterestRadiusChunks` was defined and the `PlayerStates` message
  doc comment claimed "every player in interest range," but the world-server
  broadcast every player's position to every connected player, unfiltered,
  every tick. Fixed tonight — see `LOG.md`. If a similar gap exists elsewhere
  (structure/tile broadcasts are still global — see `BACKLOG.md`), don't
  assume "there's a constant for it" means it's wired up. Grep for where a
  `Tuning` constant is actually *read*, not just declared.
- **The world-server tick loop blocks synchronously on gateway HTTP calls**
  during `Hello` (character load) and `RequestRelease` (character save +
  voyage mint), via `.GetAwaiter().GetResult()`. This is explained in comments
  as deliberate — join/release are rare, and it keeps inventory free of
  cross-thread mutation — but it means every player in a world stalls for the
  duration of one player's join or voyage if the gateway is slow. Fine at
  today's scale; worth an async queue if/when the gateway is remote and
  latency stops being sub-millisecond. Logged in `BACKLOG.md`, not fixed —
  changing it is a bigger, riskier redesign than one night should attempt
  without a decision recorded first.
- **No automated tests outside `sim-core`.** `Player.cs`, `WorldStore.cs`,
  `Program.cs` (world-server) and all of `apps/gateway` have zero test
  coverage. The gateway's race-safe SQL is exactly the kind of logic that
  silently breaks under refactor with no test catching it. This needs a
  Postgres-backed integration test project, which is more setup than a single
  night justifies starting cold — flagged, not started.
- **Two client files are past the "god script" line**: `World3D.cs` (735
  lines: terrain build, foliage, day/night clock, structure rendering, and
  tap-to-harvest input all in one `Node3D`) and `SurvivalHud.cs` (940 lines:
  meters, inventory grid, crafting cards, placement cards, travel list and
  hotbar, all hand-built in C# in one `Control`). Neither is *badly* written —
  both are cleanly subdivided into small methods — but each script
  single-handedly owns more than three systems, which is the thing that turns
  into a merge-conflict magnet as the team grows. Splitting them is real work
  (extracting child scenes/nodes with their own scripts) and didn't clear
  tonight's severity bar; logged for a future night that has more budget.
- **`RequestChunk` has no bounds check.** A client can ask for any chunk
  coordinate; the server will generate and send it. Harmless today (terrain
  generation is cheap and deterministic, nothing secret lives in a chunk) but
  worth a sanity bound before this ships publicly, so a malformed or hostile
  client can't force unbounded generation work.

## Target frameworks (unchanged, confirmed against `.csproj` files)

`sim-core`, `shared-proto`, `client` → net8.0. `world-server`, `gateway` →
net10.0. Confirmed in each `.csproj`; matches `CLAUDE.md`.

## Build/test tooling note for future nightly runs

**This sandboxed session had no `.NET SDK` installed** (`dotnet` was not on
`PATH`, and no SDK directory existed under the usual install locations). If a
future nightly session hits the same wall: say so loudly in the report rather
than claiming a build was verified. It wasn't, tonight.
