# Nightly backlog

Ranked by Severity(1-5) × Blast radius(1-5), highest first. Per-night process:
audit, compute scores, fix the single highest scorer that clears threshold
(≥15 combined, or ≥9 on a multiplayer-correctness finding), log the rest here.

## Open

1. **No server-side cooldown on gather requests — Score 16 (Sev 4 × Blast 4).**
   `apps/world-server/Program.cs` `ChopRequest` handler only checks
   `player.IsWithinReach`; `World.TryHarvest` tracks strike *count* but never
   elapsed time between strikes. A client can fire `ChopRequest` as fast as
   the socket allows and fell any node in one network burst, bypassing the
   intended multi-hit pacing the whole gathering economy is balanced around.
   This is client-authoritative-by-omission on a core survival loop and was
   the second-highest-scored finding in the 2026-08-13 audit — it also
   clears the mandatory-fix threshold on its own; picked for a future night
   because only one fix ships per night and interest management (score 20)
   was worse. Fix: track last-strike server time per player (or per node)
   and reject strikes inside a minimum interval, mirroring how
   `MovementRules` budgets elapsed time rather than update count.

2. **Zero automated test coverage outside sim-core — Score 12 (Sev 3 × Blast 4).**
   `apps/world-server/*`, `apps/gateway/Program.cs` have no tests, including
   the voyage claim/expire race (`ReclaimExpiredTicketAsync`) — exactly the
   kind of concurrency logic most likely to silently break (duplicate or
   lose a character) and least likely to be caught by manual testing.

3. **`TileChanged` / `HarvestProgress` still broadcast to everyone — Score 12
   (Sev 3 × Blast 4).** Same interest-management gap as the fix shipped
   tonight, but not included in that scope: `apps/world-server/Program.cs`
   `ChopRequest` handling still calls the unfiltered `Broadcast()` for both
   messages. Lower frequency than the old `PlayerStates` problem (event-driven
   on a chop, not per-tick), so lower severity, but the same fix shape
   applies — gate by `InterestRules.IsWithinRange` the same way
   `SendPlayerStates`/`SendStructureIfInRange` now do. Natural next bite of
   the same invariant.

4. **`World3D.cs` god script — Score 9 (Sev 3 × Blast 3).** 735 lines in one
   `Node3D`: terrain/foliage mesh building, tap-to-harvest input, day/night
   clock, structure rendering, all network-event wiring. No test surface,
   hard to touch one concern without risking another.

5. **`SurvivalHud.cs` god script with rebuild-on-every-update churn — Score 9
   (Sev 3 × Blast 3).** 940 lines, one `Control`, four menu tabs.
   `RefreshGrid`/`RefreshHotbar` `QueueFree()` and fully reconstruct every
   button/icon on *every* `InventoryUpdate` rather than diffing — GC churn on
   a mobile target for a screen that updates on every gather tick.

6. **`SurvivalRules.IsDead` never checked — Score 6 (Sev 2 × Blast 3).**
   `packages/sim-core/SurvivalRules.cs`. Health can hit zero with no
   consequence — death has no effect in `Player.cs`/`Program.cs`.

7. **No schema-version tracking — Score 6 (Sev 2 × Blast 3).**
   `infra/migrations/*.sql` are additive `IF NOT EXISTS`, no version table,
   no payload version tag on `CharacterState`/`tile_diff`. Fine today; no
   path for a future breaking change.

8. **Tool stack size not enforced — Score 4 (Sev 2 × Blast 2).**
   `Player.Give`/`ApplyCraft` never check `ItemDef.StackSize`
   (`ItemCatalog.cs`), so a player can hold multiple axes despite the
   catalog declaring `ToolStackSize=1`.

9. **`RequestChunk` has no proximity check — Score 4 (Sev 2 × Blast 2).**
   `apps/world-server/Program.cs`. A client can request any chunk coordinate
   regardless of position, scraping arbitrary distant terrain.

10. **Structure discovery scan is O(players × structures) every second —
    Score 3 (Sev 1 × Blast 3).** New tonight, self-flagged: the periodic scan
    in `Program.cs` (`Tuning.StructureDiscoveryTicks`) walks every structure
    for every player once a second. Fine at pre-alpha structure counts; needs
    a chunk-indexed structure lookup (mirroring `RequestChunk`) before
    content scales. Not urgent.

11. **`DebugCapture.cs:68` calls the stale `GetCamera2D()` — Score 1
    (Sev 1 × Blast 1).** Leftover from the pre-3D client; always null now, so
    capture framing silently no-ops.

## Also noted, not scored

- `World.RemoveStructure` exists and is unit-tested but nothing in
  `world-server` calls it — no demolish feature is wired up yet.
