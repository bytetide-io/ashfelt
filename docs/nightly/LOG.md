# Nightly log

## 2026-08-04 — AUDIT (fixed one finding, per the "≥15 skips Phase 2" rule)

**Chose:** Made the client fetch and apply the server's persisted tile diffs
*before* generating terrain/foliage, instead of only ever rendering the
pristine seed.

**Because:** Audit score 25 (Severity 5 × Blast radius 5), multiplayer-
correctness category (highest weight), well past the "must fix, skip Phase 2"
threshold of 15 (and past the ≥9 multiplayer-correctness bar on its own).

The bug: `World3D.cs` built its whole visible world — terrain, trees, shrubs,
berry bushes — from `new TerrainGenerator(Seed)` alone. It never called the
already-implemented `WorldConnection.RequestChunk` / consumed the already-
implemented `ChunkReceived` event. Both existed, fully wired on the wire
protocol and the server side (`world.GenerateChunk` correctly layers
`_diffs` — loaded from postgres at startup — over generated terrain), but
nothing on the client side ever called them. The class's own doc comment
said as much: *"derived from a seed the server will supply once networking
is ported."* Networking for the diff/chunk half specifically had not been
ported since the 3D rewrite.

Practical effect: every tree, shrub, or berry bush any player had ever
harvested reappeared as pristine and apparently-choppable on the next join,
reconnect, or voyage arrival — for every player, every session, on every
world with persistence enabled. Tapping one did nothing (the server
correctly refused the harvest against its real, diffed state), which reads
to a player as a broken interaction, not just a cosmetic mismatch. This
directly undermines the survival/persistence pillar in `CLAUDE.md`
("gather, craft, build, endure") and invariant #2 ("world storage is seed +
diffs") — the storage was fine, the client's honoring of it was not.

**Changed:**
- `apps/client/scripts/world3d/World3D.cs` — `OnWelcomed` now requests every
  chunk in the build radius (`RequestChunks`) instead of building
  immediately; `OnChunkReceived` accumulates responses and, once all are in,
  folds every tile that differs from the pristine seed into the existing
  `_tileDiffs` overlay (the same one live `TileChanged` broadcasts already
  used), then calls `Build()`. The three foliage collectors
  (`CollectTrees`/`CollectShrubs`/`CollectBerryBushes`) now check
  `EffectiveTile` (diff-aware) instead of `_terrain.TileAt` (pristine) when
  deciding whether to place a tree/tuft/cluster. `ResetWorld` (the voyage
  path) clears the new chunk-tracking state too. A "Loading world…" status
  string covers the (normally sub-second, LAN-scale) round trip.
- `docs/nightly/ARCH.md`, `docs/nightly/BACKLOG.md`, `docs/nightly/LOG.md` —
  created (first night these existed); `ARCH.md` is an honest map of the
  repo as found tonight, `BACKLOG.md` carries every other finding with its
  score.

No wire protocol change — `RequestChunk`/`ChunkData` already existed on both
ends and were simply unused by the client. No protocol version bump needed.
No save-format change; nothing new is persisted.

**Risk:** World construction now waits on a network round trip (request →
25 `ChunkData` replies for the default `Radius = 2`) before the player can
move, instead of building instantly and locally. On a healthy connection this
is sub-second and was already preceded by a "Connecting…" status of similar
scale; on a very poor connection the "Loading world…" state could be visibly
longer than before. Watch for: a report of a long/stuck "Loading world…"
screen — check that all 25 `ChunkData` replies actually arrived (a dropped
reliable-ordered packet would stall a specific chunk forever; there's no
retry/timeout on the client side for a chunk that never answers). This exact
gap (no retry/timeout) already existed for other reliable messages in this
codebase, so it's a consistent risk level, not a new one introduced tonight.

**Revert:** `git revert 0ac7014` (branch `claude/relaxed-pascal-bl1c9l`,
"client: fetch server chunk diffs before building terrain"). The change is
fully self-contained to `World3D.cs`; reverting drops the client back to
building from the pristine seed only (tonight's bug, not a new one).

**Verified:** Read every touched call site and type by hand — `ChunkCoord`,
`TileType[]`, `WorldConnection.RequestChunk`/`ChunkReceived`,
`World.GenerateChunk`, `WorldStore.LoadDiffsAsync` — and traced the full path
from a harvested tile's diff landing in postgres, through
`WorldStore.LoadDiffsAsync` into `World._diffs`, through
`World.GenerateChunk`'s `TileAt` (which already applies diffs), across the
wire as `ChunkData`, into the client's new `OnChunkReceived`, into
`_tileDiffs`, into `EffectiveTile`, into the three foliage collectors. Each
link already existed and was already correct on its own; tonight only added
the missing link (request + apply) between "server has diffs" and "client
asks for them."

**Not verified — no `dotnet` SDK available in this session's environment, at
all** (checked `dotnet --version`, `/usr/share/dotnet`, `~/.dotnet`, a
filesystem search for a `dotnet` binary — none found). I could not:
- compile `apps/client`, `apps/world-server`, or any other project,
- run `dotnet test tests/sim-core.tests` (though this change doesn't touch
  `sim-core`, so it shouldn't be affected — but "shouldn't" is not "verified"),
- open the project in Godot or run two simulated clients against a
  world-server to watch a previously-harvested tile actually render bare on
  a fresh join,
- confirm there are no new Godot editor warnings/errors.

**A human needs to, on a real dev machine, before trusting this:** run
`dotnet build` across the solution to catch anything a hand-trace missed;
run `dotnet test tests/sim-core.tests` (expected: unaffected, still green);
start a world-server with `ASHFALL_DB` pointed at a real postgres, harvest a
tree, restart the world-server (or just disconnect/reconnect the client),
and confirm the tree is now gone on rejoin instead of reappearing — that's
the actual bug this fix targets and the one scenario that most needs eyes on
a running client.

**Rejected tonight:** Interest management for `PlayerStates` broadcast (score
12, see BACKLOG.md #1) — real invariant violation but not an active failure
at today's expected player counts, and this was a one-fix night. Splitting
`SurvivalHud.cs`/`World3D.cs` (score 6) — pure maintainability, lower score,
and `World3D.cs` was exactly the file already under a scalpel tonight; adding
a structural split in the same session as a correctness fix would have made
the correctness fix harder to review in isolation.

**Added to backlog:** All findings above (interest management, no netcode
test coverage, two god-scripts + one god-file, ground mesh not following
diffs, fixed-radius-at-origin world with no streaming) — see
`docs/nightly/BACKLOG.md` for full detail and scores.

**Question for the human:** None blocking. One non-blocking flag: this
session had no `dotnet` SDK at all, so nothing in this repo could be
compiled or tested tonight regardless of which fix had been chosen — if
nightly sessions are meant to actually build/run the project, the execution
environment needs the SDK installed (or a setup/session-start hook that
installs it) before that's possible.
