# Nightly backlog

Ranked ideas / findings not yet built. Score = Severity(1-5) × Blast radius(1-5).
Anything ≥15 (or ≥9 for a multiplayer-correctness finding) is a "must fix"
per the nightly decision rule — those get picked up first on future nights.

## Open findings

### 1. Event broadcasts still ignore interest range — Score 3×3=9
`TileChanged`, `HarvestProgress`, `StructurePlaced`, `PlayerLeft` in
`apps/world-server/Program.cs` are all unconditional `Broadcast()` to every
peer in the world, same gap as the `PlayerStates` fix from tonight but for
event-driven messages rather than the per-tick one. Lower bandwidth impact
(these aren't sent every tick — a chop is at most a few Hz per active
harvester, structure placement is rare), but the same invariant violation.
**Suggested fix**: reuse tonight's per-recipient distance filter, or better,
factor a shared `IEnumerable<Player> NearbyTo(Player)` helper on `World`/
`Program.cs` so both the tick broadcast and these event broadcasts share one
definition of "nearby." Small, mechanical, but touches four call sites —
sizing it just under tonight's single-thing budget.

### 2. No world-server test coverage — Score 3×4=12
`tests/sim-core.tests` covers sim-core only. The tick loop, message
handlers, and now the interest-management filtering added tonight have zero
automated tests. A future refactor of `Program.cs` (it's a single top-level
statements file, not even a testable class) could silently break netcode and
nothing would catch it before a human notices in play. **Suggested fix**:
extract the tick-loop body and message handlers into a testable class (e.g.
`WorldServerLoop` taking `IWorldServerNetwork`/`Player` collection), then add
a test project analogous to `sim-core.tests`. This is a real
refactor — worth its own night rather than a rushed add-on.

### 3. `SurvivalHud.cs` is a 940-line god script — Score 2×2=4
Owns six distinct UI subsystems (meters, inventory grid, hotbar, crafting
cards, placement cards, travel list) in one `Control`. Not urgent — it works,
and Godot UI code naturally accretes — but the next HUD feature should split
it into per-tab controllers first. Low severity because nothing is currently
broken by the coupling; flagged so it doesn't grow past 1500 lines before
someone deals with it.

### 4. `World3D.cs` is a 735-line god script — Score 2×2=4
Owns terrain mesh build, foliage placement, day/night sky, input handling,
and structure spawning, plus wiring four other nodes. Same verdict as #3:
functionally coherent as "the world scene's root," but a real god-object by
the audit's line-count rubric. Consider splitting foliage placement and
structure spawning into their own components before the next terrain feature
lands on top of it.

### 5. Migration bootstrap process unverified — Score unscored (needs a human)
Found `infra/migrations/001_init.sql` through `004_warmth.sql` as a flat
numbered SQL sequence, but no migration *runner* in the repo (no `dotnet-ef`,
no custom migrator, nothing invoked from `docker-compose.yml` beyond
presumably mounting the SQL somewhere). Couldn't verify a fresh Postgres
actually bootstraps to 004 — I don't have Postgres or `dotnet` available in
this sandbox to check. **Needs a human to confirm** the migration story is
real and documented somewhere (or to document it if it's tribal knowledge).

### 6. Mobile performance entirely unmeasured — Score unscored (needs a human)
No frame time, draw call, overdraw, texture memory, or battery numbers exist
anywhere in the repo. This sandbox has no Godot editor GUI and no device.
Not a "finding" so much as a standing gap — someone with a device or the
Godot editor should run a profiling pass at some point before Phase 4
("balancing and a mobile UI pass" per README) locks in.

## Rejected ideas (considered, not pursued)

- **Splitting `SurvivalHud.cs` tonight anyway.** Rejected: 940 lines with
  zero test coverage and a hard "ship one thing" mandate is exactly the kind
  of overnight refactor that reads clean in a diff and is subtly wrong in
  the editor — and nobody would notice until morning. Logged instead (see #3).
- **Adding interest filtering to the event broadcasts too, same night as the
  tick-loop fix.** Rejected: would have tripled the surface area of tonight's
  change (four more call sites, a shared-helper design decision) for a much
  smaller bandwidth win, since those messages aren't per-tick. Kept tonight's
  diff to the single highest-bandwidth message and logged the rest (see #1).
