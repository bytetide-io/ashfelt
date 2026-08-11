# Ashfall nightly backlog

Ranked ideas / findings not yet built, most recent audit first. Score =
Severity(1-5) × Blast radius(1-5), per the nightly process's decision rule:
any single finding ≥15, or any multiplayer-correctness finding ≥9, forces a
fix-it-tonight and skips new features that session.

## From 2026-08-11 audit (full findings: see `LOG.md` entry for that date)

Fixed tonight: periodic character autosave (was tied for top score, 20).
Everything below is still open.

| Score | Finding | File:Line |
|---|---|---|
| 20 | **Interest management not implemented.** `PlayerStates`, `TileChanged`, `HarvestProgress`, `StructurePlaced` broadcast to every connected peer with no distance/chunk filter, despite `Tuning.InterestRadiusChunks` existing as a declared constant. Violates invariant #6 outright. Traffic grows ~O(n²) per world. **Not attempted tonight** — correctly scoping every `Broadcast()` call is a real netcode redesign (needs per-player "what am I subscribed to" state and dirty-tracking on join/leave/move-between-chunks), too large to ship *and verify* unsupervised in one session. Recommend a daytime session with a human able to test with 3+ concurrent clients. | `apps/world-server/Program.cs:392-426` (also ~220-236, ~292-294); `packages/shared-proto/Protocol.cs` (`InterestRadiusChunks`) |
| 16 | **`ChopRequest` has no rate limit/cooldown.** Unlike movement (bounded by elapsed-time budget in `MovementRules.Check`), harvest strikes are accepted once per received packet with no timing gate. A client sending `ChopRequest` faster than the UI's tap cadence farms resources at network speed. | `apps/world-server/Program.cs:192-242` |
| 16 | **Synchronous, blocking gateway HTTP calls inside the single-threaded packet/tick loop.** `GetCharacterAsync(...).GetAwaiter().GetResult()` and `ClaimVoyageAsync(...).GetAwaiter().GetResult()` on `Hello` stall movement/harvest/broadcast processing for *every other player in the world* for the duration of the HTTP round-trip. Deliberate per the code comment, but the comment only reasons about the joiner, not the blast radius on everyone else. | `apps/world-server/Program.cs:100-104,121` |
| 12 | **Placed structures never block movement.** `PlacementRules.Blocks(ItemId)` is defined but has zero callers anywhere in the repo. `MovementRules.Check` only tests terrain height. `BuildWall()` on the client has no `CollisionShape3D`. Walls are decorative. | `packages/sim-core/PlacementRules.cs:31-35`; `apps/client/scripts/world3d/World3D.cs:656-661` |
| 12 | **No death/respawn handling.** `SurvivalRules.SurvivalState.IsDead` (Health==0) exists but is never read anywhere under `apps/`. A player who starves/freezes to 0 health keeps playing at 0 forever. | `packages/sim-core/SurvivalRules.cs:77` |
| 12 | **Confirmed god scripts** — see `ARCH.md` for detail. `World3D.cs` (735 lines) and `SurvivalHud.cs` (940 lines) each knowingly own more than 3 systems, against the "no god-scenes" rule. Maintainability debt, not a runtime bug, but nearly every future client feature has to touch one of these two files. | `apps/client/scripts/world3d/World3D.cs`; `apps/client/scripts/world3d/SurvivalHud.cs` |
| 12 | **No chunk streaming.** Client builds one fixed `Radius=2` block at world-build time and never calls `RequestChunk`, though the message exists on both sides. Walking past the built radius drops the player off the collision mesh. Partly acknowledged scope gap ("for this first slice"). | `apps/client/scripts/world3d/World3D.cs:230-266`; `apps/client/scripts/WorldConnection.cs:123-131` |
| 12 | **No schema versioning / migration runner for already-provisioned databases.** `infra/migrations/*.sql` apply only via Postgres's `docker-entrypoint-initdb.d`, which runs only against a brand-new empty volume. Neither `WorldStore.OpenAsync` nor the gateway applies pending migrations at startup. `002_character.sql`, `003_voyage.sql`, `004_warmth.sql` will silently never apply to an already-provisioned deployment. | `infra/docker/docker-compose.yml:14`; `apps/world-server/WorldStore.cs:29-48`; `apps/gateway/Program.cs` |
| 9 | **Fire-and-forget world writes swallow exceptions silently.** `_ = store.SaveDiffAsync(...)` / `_ = store.SaveStructureAsync(...)` have no try/catch/log, unlike the character-save paths. A transient DB hiccup silently and permanently loses a tile diff or structure with no log trace. | `apps/world-server/Program.cs:217,290` |
| 9 | **Full inventory-UI rebuild on every harvest strike.** Every successful chop can broadcast `InventoryUpdate` up to 15×/sec while continuously gathering; the client's `RefreshInventory()` does `QueueFree()` + re-instantiate dozens of Buttons/Labels/PanelContainers every time — node churn on the single most common gameplay action, on mobile hardware. | `apps/client/scripts/world3d/SurvivalHud.cs:819-845,257-267`; `apps/world-server/Program.cs:210` |
| 9 | **No proof-of-possession beyond a raw GUID.** A normal join sends `CharacterId` with an empty ticket; nothing ties the connection to the device that created the character. Leaking/guessing a UUID lets anyone impersonate/drain that character. Acceptable as documented pre-alpha debt before real accounts exist — recording so it isn't forgotten. | `apps/gateway/Program.cs:46`; `apps/world-server/Program.cs:100-129` |
| 6 | **Testing gap.** `HarvestRules.cs` and `Noise.cs` have no dedicated test file (only indirect coverage via `WorldTests`/`DeterminismTests`). `PlacementRules.Blocks` has zero coverage anywhere — notably the one rule that turned out to be dead/unwired (see the structures-don't-block finding above). | `packages/sim-core/HarvestRules.cs`, `Noise.cs`, `PlacementRules.cs` (no test file) |
| 6 | **Stamina system built but never spent.** `SurvivalRules.TrySpendStamina` is implemented and unit-tested but has zero callers in `apps/world-server` — chopping/moving cost 0 stamina, so the meter only ever sits full. | `packages/sim-core/SurvivalRules.cs:144` |
| 4 | **Item stack caps declared but never enforced.** `ItemDef.StackSize`/`DefaultStackSize`/`ToolStackSize` exist but `Player.Give`/`ApplyCraft` never check them — inventory counts grow unbounded; `ToolStackSize=1` (relied on by "best tool" logic) is never actually guaranteed. | `packages/sim-core/ItemCatalog.cs:40,61,63`; `apps/world-server/Player.cs:48-52` |
| 4 | **`Player.Rejections` tracked but unused.** Incremented on every rejected move but never read anywhere — a client spamming illegal moves is corrected forever but never flagged/kicked. | `apps/world-server/Player.cs:40,176` |

### Suggested next-night pick

The two score-16 items (chop rate limit; blocking gateway calls in the tick
loop) are both multiplayer-correctness ≥9 and therefore still mandatory picks
until fixed — whichever night picks this backlog up next does not get to
choose Phase 2 while either is open. Chop rate-limiting is the more
self-contained of the two (add a per-player cooldown timestamp, mirroring how
movement already budgets by elapsed time) and is the recommended next pick;
the blocking-gateway-calls fix is architecturally bigger (needs either an
async admission queue or moving `Hello` handling off the LiteNetLib event
thread) and deserves a session of its own.
