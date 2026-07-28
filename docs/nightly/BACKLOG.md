# Nightly backlog

Ranked by `Severity (1–5) × Blast radius (1–5)`. Anything ≥15, or any
multiplayer-correctness finding ≥9, is a mandatory fix per the nightly
process — flagged below. Pick from the top next time unless something new
and higher-scored turns up in that night's audit.

## Open

### 1. Unfiltered `TileChanged` / `StructurePlaced` / `HarvestProgress` broadcasts — Severity 3 × Blast 3 = 9
**Category:** multiplayer correctness (interest management, invariant #6).
`apps/world-server/Program.cs`: every harvest strike, felled tile, and placed
structure is `Broadcast()` to every connected player regardless of distance —
the same invariant-#6 gap tonight's fix closed for `PlayerStates`, just
event-driven rather than per-tick, so lower current severity. **This crosses
the ≥9 multiplayer-correctness bar and could reasonably have been tonight's
fix instead** — I chose the `PlayerStates` broadcast because it fires every
tick regardless of activity (worse steady-state cost) while these fire only
on action; both should use `InterestRules` (now that it exists) or a
structure/tile-specific radius. Pick this one first next session; the
`InterestRules.IsVisible` helper already exists to build it on.
**Fix sketch:** filter each broadcast the same way `SendPlayerStates` does —
recipient loop + `InterestRules.IsVisible(recipient.Position, eventPosition)`
— for chop/place at least; `StructurePlaced` backfill-on-join already loops
per-structure so it is the easiest of the three to convert.

### 2. `SurvivalHud.cs` god script — Severity 3 × Blast 2 = 6
940 lines, one `Control`: survival meters, day/night chip, inventory grid,
item detail panel, crafting/building/travel tabs, and the hotbar all live in
one class. No netcode risk, but every future HUD feature touches this file.
**Fix sketch:** split into composed child scenes/scripts per tab (crafting
sheet, inventory grid, hotbar) that `SurvivalHud` assembles, matching the
"small reusable scenes, no god-scenes" rule in CLAUDE.md. `StripedBar.cs`
is already a precedent for pulling a piece out.

### 3. `World3D.cs` god script — Severity 3 × Blast 2 = 6
735 lines, one `Node3D`: terrain/foliage building, structure rendering, and
every `WorldConnection` event handler in one place. Same shape of problem and
fix as #2 — extract event-handling glue into smaller, single-purpose nodes
(e.g. a `StructureRenderer`, a `FoliageDamageView`) that `World3D` wires up
rather than implements inline.

### 4. `world-server/Program.cs` growing into a god script — Severity 2 × Blast 3 = 6
~510 lines after tonight, all top-level statements: connect/disconnect,
the full `MessageId` switch, and the tick loop. Not urgent — it is still
readable in one sitting — but the next few message types should probably
move handler bodies into named methods (or a small `MessageHandlers` static
class) rather than growing the `switch` further.

### 5. `World.HasWarmthNear` — O(structures) per player per tick — Severity 2 × Blast 2 = 4
`packages/sim-core/World.cs`. Called once per connected player every server
tick, scans every structure in the world linearly. Cheap today (few
structures per world). Once bases get large, replace with a spatial
bucket keyed by chunk, same shape as `tile_diff`'s chunk keying.

### 6. No migration-tracking table — Severity 2 × Blast 3 = 6
`infra/migrations/*.sql` are hand-numbered and idempotent
(`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`), applied with no
recorded version anywhere in the database. Works by luck of idempotency at 4
files. A real migration runner (even a 20-line "has this filename run"
table) avoids a future migration silently reordering or double-applying.

### 7. No multi-client integration test for netcode — Severity 3 × Blast 2 = 6
Everything network-facing (`Program.cs`, `WorldConnection.cs`) is untested
except indirectly through the pure `sim-core` rules it calls. A real bug in
the socket/broadcast glue itself (not the rules) would not be caught by
`dotnet test tests/sim-core.tests`. Tonight's `InterestRules` tests cover the
filtering logic, not the fact that `SendPlayerStates` actually calls it
correctly end to end. Worth a lightweight harness (spin `NetManager` twice
in-process against a `Program.cs`-equivalent) if this becomes a recurring
class of bug.

### 8. Gateway world registry is hardcoded — Severity 2 × Blast 2 = 4
`apps/gateway/Program.cs`: `worlds` is a literal `Dictionary` of two entries.
Adding a third world-server means editing and redeploying the gateway.
Fine for two dev worlds; move to config or a `world` table read from
Postgres once there is a reason to add a third world.

## Resolved

- ~~Unfiltered `PlayerStates` broadcast (every player, every tick,
  regardless of distance)~~ — fixed 2026-07-28, see `LOG.md`.
