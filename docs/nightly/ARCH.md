# Ashfall — architecture map (nightly baseline)

Written by the first nightly session, 2026-08-20. This is an honest snapshot
for future unsupervised nights to orient from quickly — read `docs/architecture.md`
and `docs/gameplay-roadmap.md` first; they are the source of truth and this
file does not repeat them. This file records *what a fresh session needs to
know that isn't obvious from a first read*: real line counts, what actually
builds, and where the debt is.

## What this project actually is

Contrary to the generic "Godot survival game" framing in the nightly-routine
prompt, this is **not** a GDScript project. It's a C# monorepo:

- `packages/sim-core` (net8.0) — deterministic shared rules: terrain, harvest,
  crafting, survival, movement bounds, fire. No Godot dependency; this is what
  makes it unit-testable outside the engine.
- `packages/shared-proto` (net8.0) — wire message ids, `ItemId`, `Tuning`.
- `apps/client` (net8.0, `Godot.NET.Sdk/4.7.1`) — the Godot 4 client, C# only.
  **Compiles without the Godot editor** (`dotnet build apps/client/AshfallClient.csproj`
  works via the `Godot.NET.Sdk` NuGet package), but nothing about scene wiring,
  visuals, or touch feel can be verified that way — that needs the actual
  editor/device.
- `apps/world-server` (net10.0) — authoritative UDP server (LiteNetLib), one
  process per world/region.
- `apps/gateway` (net10.0, ASP.NET minimal API) — accounts, character
  persistence, voyage ticket minting/claiming. Postgres via Npgsql.
- `tests/sim-core.tests` (net10.0, xunit) — the only test project. 92 tests
  as of tonight, all in `sim-core`. **Nothing else has tests** — world-server,
  gateway and client are exercised only by manual/integration smoke checks.

Requires **both** .NET 8 and .NET 10 SDKs (README says so; a fresh sandbox
has neither — `apt-get install dotnet-sdk-8.0 dotnet-sdk-10.0` gets you a
working build+test environment with no Godot needed for anything but the
client's own scenes/visuals).

## Runtime shape

```
client (Godot/C#) --UDP(LiteNetLib)--> world-server (C#, one per region)
                                            |
gateway (ASP.NET, C#) <--REST-- world-server (character load/save, voyage)
       |
   Postgres (characters, voyage_ticket, world/tile_diff/structure per world-server)
```

Server loop: single-threaded, event-polled (`NetManager.PollEvents()` inside
one `while` loop at `Tuning.TicksPerSecond` = 15 Hz). This matters: there is
**no cross-request race condition to guard against** on the world-server —
every `NetworkReceiveEvent` handler and every tick-loop body runs serially on
one thread. Don't add locking that isn't needed; do keep every per-tick and
per-message handler cheap, since there's no second thread to pick up slack.

## What's genuinely solid (read before "fixing" it)

- Server-authoritative movement: client simulates, server bounds via
  `MovementRules.Check` (speed/rise/fall/height-vs-terrain), shared so a
  legitimate client is never corrected. Reach checks (`Player.IsWithinReach`)
  gate chop/place/feed identically.
- Voyage ticket flow (`apps/gateway/Program.cs`): single-use tickets via
  `DELETE ... RETURNING` + `UPDATE`, correctly race-safe under concurrent
  claims (verified by reading, not just skimming — the losing claim's DELETE
  affects 0 rows and 409s). Self-healing on expiry
  (`ReclaimExpiredTicketAsync`) so a crash mid-voyage doesn't strand a
  character.
- Wire protocol: every message in `shared-proto/Protocol.cs` is hand-verified
  byte-for-byte between the world-server writer and the client reader
  (checked all ~17 message types tonight). No drift found. The *risk* is
  structural (hand-paired `Put`/`Get` calls, no shared codec — see Backlog),
  not an active bug.
- Persistence follows "seed + diffs" faithfully: `WorldStore` never writes
  full chunks, only `tile_diff`/`structure` rows, replayed on boot. In-progress
  state (harvest strikes, and as of tonight, fire fuel) is deliberately
  **not** persisted — it resets on restart, same tradeoff as a half-chopped
  tree standing whole again. This precedent is why tonight's fire-fuel state
  also went unpersisted rather than adding a migration (see LOG.md).

## Known debt (sized, not yet urgent — see BACKLOG.md for scores)

- `World3D.cs` (809 lines after tonight) and `SurvivalHud.cs` (983 lines) are
  god-scripts mixing net dispatch, terrain streaming, input, and multiple UI
  concerns. `docs/gameplay-roadmap.md` §3.4 already has the decomposition
  plan; nobody's executed it yet.
- No data-driven registry beyond `ItemCatalog`/`HarvestRules`/`CraftingRules`
  (`docs/gameplay-roadmap.md` §3.1 already scoped this).
- Persistence has no version field (`character.inventory` JSONB, `tile_diff.tile`
  as a bare `short` enum ordinal). Fine pre-launch; will bite the day someone
  reorders `TileType` or `ItemId` after a world has shipped. `ItemId`/`MessageId`
  are already documented "append-only, never renumber" — `TileType` has no such
  warning yet.

## Verification reality for a nightly session

- `dotnet test tests/sim-core.tests`, `dotnet build` on world-server/gateway/
  client: all verifiable headlessly, no Godot needed.
- Actually **running** the game (visual correctness, touch feel, frame time,
  draw calls, the mobile performance half of the audit checklist) needs the
  Godot editor or a device — **not available in this sandboxed environment**.
  A nightly session here can ship server/logic changes with real confidence
  and client C# changes with compile-time confidence only. Say so explicitly
  in every morning report rather than claiming an untested UI works.
- A real multiplayer smoke test *is* achievable without Godot: LiteNetLib is
  a plain NuGet package, so a throwaway console harness can drive the actual
  world-server binary as 2+ simulated clients over real UDP. Used tonight to
  prove the fire-feed feature end-to-end, including late-join backfill. Worth
  reaching for again rather than trusting protocol changes to unit tests alone.
