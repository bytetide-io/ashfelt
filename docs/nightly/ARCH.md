# Ashfall — nightly architecture & debt notes

Living notes for the unsupervised nightly-engineer routine. This is **not** a
replacement for `docs/architecture.md` (invariants, source of truth) or
`docs/gameplay-roadmap.md` (content/foundation plan, already contains a sharp
self-audit of the client god-scripts — read it before proposing the same
refactor twice). This file tracks what a from-scratch reading of the repo
found on 2026-08-25, so future nights don't have to re-derive it.

## Toolchain note (read this first)

**No .NET SDK is installed in the sandbox this session ran in**, and neither
`apt-get install dotnet-sdk-{8,10}.0` nor `dotnet-install.sh` could reach a
working package mirror (both hit `404`/`403` through the proxy). `dotnet` is
not on `PATH` at all. Consequence: nothing this session touched was
compiled, and `dotnet test tests/sim-core.tests` — mandatory before any
commit per `CLAUDE.md` — could not be run. **This session made no `.cs`
changes for exactly that reason**: an unverifiable code change is worse than
no change. If a future night hits the same wall, do the same — audit and
document, don't guess at compiling C# blind. A human should check whether
the nightly sandbox image can be given a working SDK (vendored, or a mirror
that actually serves the packages `apt-cache` already indexes).

## Repo map (by line count, `*.cs` only, 2026-08-25)

```
apps/client/scripts/world3d/SurvivalHud.cs    940   touch HUD: meters, hotbar, craft/build/travel tabs, joystick wiring
apps/client/scripts/world3d/World3D.cs        735   world root: terrain+foliage build, sky/clock, tap-to-gather, structures
apps/world-server/Program.cs                  468   connection lifecycle, message dispatch, tick loop (top-level statements)
apps/client/scripts/WorldConnection.cs        421   LiteNetLib client: connect/retry, send helpers, message dispatch
apps/client/scripts/ui/DesignSystem.cs        252   design tokens (palette, fonts, chunky-control theming)
apps/world-server/Player.cs                   220   authoritative per-connection state: position, inventory, survival
apps/gateway/Program.cs                       210   minimal-API routes: character CRUD, voyage mint/claim (top-level)
packages/shared-proto/Protocol.cs             161   MessageId enum, wire structs, ProtocolVersion, Tuning constants
packages/sim-core/SurvivalRules.cs            160   hunger/stamina/health/warmth tick math (integer-deterministic)
apps/world-server/WorldStore.cs               151   Postgres persistence: diffs + structures, memory-only if no ASHFALL_DB
packages/sim-core/World.cs                    150   in-memory world state: tiles, diffs, structures, harvest/place entry points
apps/client/scripts/ui/PixelIcons.cs          147   runtime-baked icon textures from DesignSystem pixel grids
packages/sim-core/TerrainGenerator.cs         135   height field + tile classification from Noise.Hash
apps/client/scripts/world3d/TerrainMesher.cs  126   chunk mesh + world-space UV baking
apps/client/scripts/ui/TitleScreen.cs         121   boot scene
packages/sim-core/ItemCatalog.cs              109   ItemDef table (name, stack, food value, tool class/tier, placement)
apps/client/scripts/world3d/VirtualJoystick.cs 104  touch stick input
packages/sim-core/MovementRules.cs            100   speed/height/terrain bounds shared by client prediction + server check
```

Everything else is under 100 lines. Total: ~5.6k lines client, ~1.5k
world-server, ~0.2k gateway, ~1k sim-core, ~1k tests (sim-core only — see
below).

## What's already solid (verified by reading, not by running)

- **Movement**: `MovementRules.Check` bounds speed/rise/fall/height against
  the shared terrain field; budget is elapsed-time-since-last-*accepted*-
  position, so packet spam can't buy distance (`Player.TryAccept`,
  `apps/world-server/Player.cs:167`). No float-accumulation or platform-RNG
  smell in the check itself.
- **Persistence is fire-and-forget off the hot path, deliberately**: tile
  diffs and structure placements are saved via `_ = store.SaveXAsync(...)`
  so a slow DB write never stalls other players; the in-memory state is
  already authoritative when the write is queued
  (`apps/world-server/Program.cs:213-224`, `:288-291`). Character saves on
  disconnect are the same pattern (`Program.cs:56-64`).
- **Bandwidth**: the per-tick position broadcast is one coalesced
  `Unreliable` packet (2-byte header + 20 bytes/player), not one packet per
  entity — for 10 players that's ~200 B/tick × 15 Hz ≈ 3 KB/s per client,
  well inside a mobile budget. Stats/heartbeat is throttled to every 2s and
  sent `Unreliable` too, with the client interpolating time-of-day locally
  between beats (`Program.cs:424-425`, `WorldConnection.cs`).
- **Voyage handoff** (`apps/gateway/Program.cs:106-187`) is a clean
  save-then-mint / claim-then-delete handshake: ownership is `NULL` while
  in-transit so a mid-transfer crash can never duplicate a character, an
  expired ticket self-heals ownership back to the origin world lazily (on
  next load or claim attempt, no background sweeper needed), and a replayed
  claim finds a deleted row and 409s.
- **Fail-loud invariants**: `Player.ConsumeOne` / `Player.ApplyCraft` throw
  rather than clamp on an impossible negative balance, matching
  `CLAUDE.md`'s "fail loudly on server-side invariant violations" rule.
- **Determinism**: nothing outside `sim-core` touches `DateTime.Now` or
  `new Random(...)` (checked with a repo-wide grep); `sim-core` is
  hash-based throughout and the `DeterminismTests` treat generation as a
  compatibility contract, exactly per `CLAUDE.md`.

## Known debt (cross-referenced, not re-discovered)

`docs/gameplay-roadmap.md` §3.4 already flags the client god-scripts by
name. Both have grown since that doc was last updated — **World3D.cs was
616 lines, is now 735; SurvivalHud.cs was 419, is now 940** (more than
doubled). The roadmap's proposed split (net dispatch / terrain streaming /
local player / remote players / interaction, plus a shared meter/item-slot
widget kit for the HUD) is still the right shape and is not repeated here.
See `BACKLOG.md` for tonight's scoring of it.

## New findings this session (not in the other docs)

- **`apps/world-server` and `apps/gateway` have zero automated tests.**
  Every `sim-core` rule is tested (`tests/sim-core.tests`, ~1k lines); the
  code that turns those rules into authoritative multiplayer state —
  `Player.cs`'s inventory/craft/eat/move-accept invariants, the gateway's
  voyage mint/claim/expire SQL — has none. `Player.cs` in particular is
  pure, engine-independent logic (its only external type, `NetPeer`, is
  stored but never called) and is the highest-value, lowest-risk place to
  start. See `BACKLOG.md` #1.
- **The Hello handshake blocks the tick loop.** `gateway.ClaimVoyageAsync`
  and `gateway.GetCharacterAsync` are called with `.GetAwaiter().GetResult()`
  directly inside `NetworkReceiveEvent`, which fires synchronously from
  `server.PollEvents()` in the single tick loop. This is a **documented,
  deliberate** tradeoff (see the comment at `Program.cs:114-118`) — not an
  oversight — but it means a slow or unreachable gateway stalls movement
  processing for every already-connected player, not just the one joining.
  Worth a decision record if it's ever revisited; not touched tonight
  because reverting or restructuring a documented decision without strong
  evidence would violate the "never repeat/undo without justifying" rule.
- **`structure.owner_id` and `structure.health` columns are unused.** The
  schema (`infra/migrations/001_init.sql`) has both; no query reads or
  writes them. Not urgent — building integrity/ownership isn't scheduled
  until Phase B/C of the roadmap — but flagging so nobody assumes ownership
  is enforced today.
- **No mobile performance numbers are on record anywhere in the repo**
  (frame time, draw calls, texture memory). Nothing in this session's
  static read suggests a specific problem (foliage uses `MultiMesh`,
  terrain bakes world-space UVs instead of per-fragment work, the
  low-res `SubViewport` trick reduces shaded fragments), but "looks fine by
  inspection" is not verified, and no sandbox here can run the client to
  check. Logged as **not verified**, not as a finding.
