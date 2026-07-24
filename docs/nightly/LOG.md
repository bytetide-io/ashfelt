# Nightly log

## 2026-07-24 — AUDIT (fix applied)

**Chose:** Backfill tile diffs (harvested trees/rocks/shrubs) to a joining
client, the same way placed structures already are. Fixed in
`a728539b4750bebb63c2f270a9971b52ece3c3f5`.

**Because:** `docs/nightly/{LOG,BACKLOG,ARCH}.md` didn't exist yet, so
tonight is a first-night audit + honest architecture map (see `ARCH.md`).
The audit protocol says compute Severity × Blast radius for every finding and
fix anything ≥15, or any multiplayer-correctness finding ≥9, skipping the
feature phase. Reading `apps/world-server/Program.cs`'s Hello handler against
`apps/client/scripts/world3d/World3D.cs`'s `Build()` turned up a real one:
the server backfills `world.Structures` to a joining player but never
`world.Diffs`. `World3D.Build()` constructs the whole visible world straight
from `TerrainGenerator` with no diff awareness — `_tileDiffs` is populated
only by live `TileChanged` events received *after* the client is already
connected. So any tile another player (or a previous session) already
harvested before this client joined stays a full tree/rock/shrub on the new
client forever: it never collapses, and tapping it silently does nothing,
because the server's `HarvestRules.Evaluate(world.TileAt(...))` correctly
sees the diffed (already-bare) tile and refuses. That's core-loop-breaking
and gets worse the longer a world runs (diffs only accumulate). Severity 4 ×
blast radius 4 = 16 — clears the mandatory-fix bar on its own, and separately
clears "multiplayer-correctness ≥ 9." A second finding (missing interest
management, also scored 9 — see `BACKLOG.md` #1) was real but less urgent and
had no live symptom today, so per "fix exactly one thing" it was logged
instead of fixed.

**Changed:** `apps/world-server/Program.cs` only — added a loop over
`world.Diffs` inside the existing `MessageId.Hello` handler, sending one
`TileChanged` per diff via the same reliable-ordered channel already used for
the structure backfill, positioned right before it. No protocol version bump
(reuses the existing `TileChanged` message; wire layout unchanged). No client
changes needed at all — the client's existing `OnTileChanged` handler
(`World3D.cs`) already does exactly the right thing (collapses the matching
foliage, records the diff) when fed this message; it was only ever missing
the message.

**Risk:** Low, and easy to spot if wrong. The new loop reuses an
already-proven message type and send pattern (identical in shape to the
structure backfill three lines below it), so the main way this could go
wrong is ordering: if `TileChanged` backfill messages ever arrived at the
client *before* `Welcome` (which drives `Build()`), `World3D.OnTileChanged`
guards on `!_built` and would silently drop them — the same latent fragility
the existing structure backfill already depends on (both rely on reliable-
ordered delivery preserving send order, and on Godot's deferred-call queue
preserving enqueue order). I did not change that ordering or introduce a new
instance of it — I placed the diff loop using the identical pattern already
trusted for structures. Worst case if something's off: a joining client is
back to today's status quo (stale terrain) for diffed tiles, not a crash or
a worse desync. Bandwidth cost is one small reliable packet per diff, sent
once at join, scaling with how many tiles a world has ever had harvested —
worth revisiting if worlds run long enough to accumulate thousands of diffs
(noted in `BACKLOG.md` as related to the interest-management gap, not
re-logged separately).

**Revert:** `git revert a728539b4750bebb63c2f270a9971b52ece3c3f5` (single
commit, only touches `apps/world-server/Program.cs`).

**Verified:** Read the full diff against the surrounding handler twice;
confirmed `World.Diffs` (`packages/sim-core/World.cs`) returns
`KeyValuePair<(int X, int Y), TileType>` so `diff.Key.X`/`.Y`/`diff.Value`
compile against the same `writer.Put(int)` / `writer.Put(byte)` overloads
already used two lines below for structures and elsewhere in the same file
(e.g. `writer.Put((byte)strike.Becomes)` in the chop handler) — no new using
directives needed, no LINQ. Confirmed by reading `World3D.OnTileChanged` that
the client-side handling of a `TileChanged` message already does the correct
thing (collapses foliage via `_foliageByTile`, records `_tileDiffs`) whether
the message arrives from a live harvest or this new backfill — the fix
requires zero client changes. Confirmed `world.Diffs` itself is exercised by
existing sim-core tests (`WorldTests.ReplayingStoredDiffs_ReproducesWorldState`,
`DiffOverridesGeneratedTerrain_AndAppearsInChunks`), so the data this fix
reads is already trustworthy.

**Not verified:** **I could not build or run anything tonight** — this
remote sandbox has no `dotnet` SDK installed (`dotnet: command not found`)
and no network path to install one (the dotnet-install script's host is
blocked by the outbound proxy allowlist). `dotnet test tests/sim-core.tests`
was not run, `dotnet build` was not run, and the fix was not exercised
end-to-end against a live server + two clients. A human needs to, before
trusting this further: (1) run `dotnet test tests/sim-core.tests` to confirm
nothing regressed (the change doesn't touch sim-core, so this should be a
formality, but it wasn't actually run), (2) start a world-server, connect one
client, harvest a tree, disconnect, reconnect a *second* client and confirm
the tree is already gone/grass on arrival instead of standing — that's the
concrete repro this fix targets, (3) confirm the mobile export still builds,
which was not touched but also wasn't verified.

**Rejected tonight:** Fixing the interest-management gap (`BACKLOG.md` #1)
in the same session — also scores 9, but has no live symptom today and
fixing two things violates "ship one complete, correct, reversible change."
Splitting `World3D.cs`/`SurvivalHud.cs` — real debt (see `ARCH.md`) but
lower severity than a live core-loop bug, and a structural split is exactly
the kind of change that's hard to verify without a working build in this
sandbox tonight. Standing up an integration test for this exact fix — no
netcode test harness exists yet (`BACKLOG.md` #5); building one properly is
bigger than tonight's slot, and bolting on a single throwaway test without
the harness would be worse than being honest that this is unverified.

**Added to backlog:** Interest management declared but unimplemented (9);
no schema migration path (9); `World3D.cs` god-scene-in-code (6);
`SurvivalHud.cs` UI god-script (4); zero netcode/integration test coverage
(9); dead `RequestChunk`/`ChunkData` wire path (1). Full detail in
`BACKLOG.md`. Also wrote `ARCH.md` as the first honest codebase map, since
none of the three nightly files existed before tonight.

**Question for the human:** None blocking — but flagging non-blocking:
this sandbox has no way to build or run .NET at all, so every future nightly
session inherits the same "verified by reading, not by running" limitation
unless the environment gets a `dotnet` SDK (or network access to install
one). Worth deciding whether that's acceptable for unsupervised nights on a
codebase this careful, or whether nightly runs should get a pre-provisioned
SDK.
