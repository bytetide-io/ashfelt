# Ashfall — nightly backlog

Findings from the nightly audit that were not fixed on the night they were
found, ranked by `Severity(1-5) × Blast radius(1-5)`. Pick from the top
unless a specific finding blocks something else on the roadmap.

## 1. Tile/structure broadcasts and join backfill are still world-wide — 9 (3×3)

**2026-08-10.** The higher-severity half of tonight's interest-management gap
(the per-tick `PlayerStates` fan-out) is fixed — see `LOG.md`. Not fixed
tonight: `TileChanged`, `HarvestProgress` and `StructurePlaced` still
`Broadcast()` to every connected player regardless of distance
(`apps/world-server/Program.cs`), and a joining player's structure backfill
(`Hello` handler, "Backfill the already-built world") still sends *every*
structure ever placed in the world, not just nearby ones.

Why this is lower priority than the player-position fix: these are
event-triggered, not per-tick, so their bandwidth cost grows with world
activity, not with (player count)² every 66ms — the part that actually
scales badly. They're also individually harder to do right: a full fix needs
either (a) per-player dynamic chunk subscription (stream structures into
range as a player walks, not just at join) or (b) the entity/interest system
`docs/gameplay-roadmap.md` §3.3 already plans, which explicitly says new
broadcasts should "extend it, don't fork it." Bolting a one-off filter onto
just these three message types tonight would either be incomplete (backfill
without streaming = structures placed while you were elsewhere never appear
until you reconnect) or would duplicate work §3.3 is going to do properly.
**Recommendation:** fold this into §3.3 when the entity system lands, using
`InterestRules.InRange` (now in `sim-core`, tested) as the shared filter —
don't re-derive the rule.

## 2. `SurvivalHud.cs` is a 940-line god-script — 9 (3×3)

Builds and refreshes meters, hotbar, gather prompt, and all four action-sheet
tabs (items/craft/build/travel) in one `Control`. Already flagged in
`docs/gameplay-roadmap.md` §3.4 with a concrete split plan (reusable
meter/slot widgets, one inventory model the tabs observe). It has grown from
419 to 940 lines since that plan was written without the split happening —
the plan is still right, it just hasn't been picked up. Good candidate for a
future Phase 2 (feature-shaped, no audit-severity trigger, but real
maintainability drag on every future HUD change).

## 3. `world-server/Program.cs`'s message switch will not scale — 6 (2×3)

500 lines, one `switch` on `MessageId`, hand-paired `writer.Put`/`reader.Get`
calls with no shared layout definition between client and server. Already
flagged in `docs/gameplay-roadmap.md` §3.2 (typed read/write helpers) —
worth doing before the next new message type, not urgent before that.

## 4. `World3D.cs` is a 735-line god-script — 6 (3×2)

Net dispatch, terrain/foliage building, local-player interaction/targeting,
day/night, and structure rendering in one `Node3D`. Also flagged in
`docs/gameplay-roadmap.md` §3.4 (split by responsibility, dispatch table
mirroring §3.2). Grown from 616 lines since that plan was written.

## 5. No schema version on persisted tables — 6 (2×3)

Neither `tile_diff`/`structure` (world-server, `infra/migrations`) nor
`character`/`voyage_ticket` (gateway) carry a format version. Not urgent
pre-alpha — no world has shipped yet, so every format change so far has
just been a fresh generation (see `docs/architecture.md`'s "Generation
changes on record"). Add a version column and a migration story *before*
the first world anyone cares about keeping is created — retrofitting it
onto live data later is much more expensive than adding it now while the
tables are empty.

## 6. Every player spawns at the exact same tile — 4 (2×2)

`Player.FindSpawn(World, TerrainGenerator)` is a pure function of the world
only — it doesn't know who else is already there, so every connecting
player lands on the identical point and visually stacks. Cosmetic today
(dev-scale player counts); worth a small jitter/offset before any real
multiplayer playtest.

## 7. `RequestChunk` has no distance/rate bound — 2 (1×2)

A client can request arbitrary or repeated chunk coordinates; generation is
cheap and deterministic so this isn't currently exploitable for much, but
it's an unbounded surface. Low priority; note if abuse patterns ever show up
in logs.

## 8. World-server tick loop drifts under load — 2 (1×2)

`Thread.Sleep(tickMs)` with no accumulator means a slow tick (GC pause, a
burst of harvest requests) delays every subsequent tick rather than being
absorbed. Not currently observable — `MovementRules` and `SurvivalRules`
are both robust to jitter by construction (elapsed-time and whole-tick
based respectively) — but worth a fixed-timestep loop if tick timing ever
needs to be precise (e.g. for a future physics-adjacent system).
