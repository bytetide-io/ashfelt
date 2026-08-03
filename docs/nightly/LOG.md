# Nightly engineer's log

## 2026-08-03 — AUDIT

**Chose:** Wired up interest management for the per-tick `PlayerStates`
broadcast in `apps/world-server/Program.cs` — each connected player now only
receives the positions of players within `Tuning.InterestRadiusChunks` of
themselves, instead of every player in the world. Updated
`apps/client/scripts/world3d/RemotePlayers.cs` to prune a remote player's
body when a snapshot stops reporting them (previously the only removal path
was an explicit `PlayerLeft`, which no longer covers "walked out of range").

**Because:** This is a first-night audit finding, scored against the
project's own decision rule (`Severity x Blast radius >= 15`, or any
multiplayer-correctness finding `>= 9`, must be fixed instead of shipping a
new feature). `docs/architecture.md` and `CLAUDE.md` both list interest
management as a load-bearing invariant ("a client only ever receives
entities/chunks near it"), and the code had visibly drifted from it:
`Tuning.InterestRadiusChunks` was declared but never read anywhere, and the
`PlayerStates` doc comment in `packages/shared-proto/Protocol.cs` already
claimed "every player in interest range" — a comment describing intent, not
what the code did. The actual implementation broadcast every player's
position to every player, unfiltered, every tick (15 Hz). That's O(N)
bandwidth per client and O(N²) total with no cap, growing directly with world
population, in a game whose stated design is many bounded worlds each
potentially holding a meaningful number of concurrent players. Scored
Severity 3 x Blast radius 5 = 15 (every player, every tick, and it only gets
worse as a world fills up) — at or above both thresholds, so this took
priority over designing a new feature tonight.

**Changed:**
- `apps/world-server/Program.cs` — the tick loop's player-broadcast section
  now builds one filtered snapshot per recipient (distance check via the
  existing `Vec3.HorizontalDistanceTo`) instead of one shared broadcast
  buffer sent to everyone.
- `apps/client/scripts/world3d/RemotePlayers.cs` — `Apply()` now tracks which
  ids appeared in the latest snapshot and removes any previously-known body
  that didn't, in addition to the existing add/update path.

**Risk:** The tick loop's player-broadcast work goes from O(N) to O(N²) per
tick (N = players in one world) — irrelevant at today's playtest scale
(single digits), and this is the standard cost of correct interest
management, but worth knowing if a stress test ever puts dozens of players in
one world-server: the naive per-recipient distance scan is the thing to
replace with a spatial index first. The other risk is purely in the diff
itself: if the distance check or the client-side pruning has a bug, the
symptom would be either "I can't see a nearby player" (radius/units wrong) or
"a remote player never disappears" (pruning wrong). Both are visually obvious
in a two-client playtest.

**Verified:** Read the full diff by hand multiple times against the existing
message/event flow — confirmed `PlayersUpdated` → `RemotePlayers.Apply` is
the *only* consumer of the `PlayerStates` message (grepped for other
listeners; there are none), so changing what it contains doesn't silently
break something else. Confirmed the byte-count field (`(byte)visible.Count`)
still fits the pre-existing wire format — the message layout is unchanged,
only which players are included per-recipient changed, so this is not a
protocol version bump. Confirmed `Tuning`, `TerrainGenerator`, and
`Vec3.HorizontalDistanceTo` are already `using`-imported in both edited
files, and that `HorizontalDistanceTo` is the same helper already used
elsewhere in `World.cs`/`Player.cs` for reach and warmth checks, so the units
(metres) match. No other file reads `Tuning.InterestRadiusChunks` or assumes
`PlayerStates` contains every player.

**Not verified:** **The .NET SDK was not available in this sandboxed
session** (`dotnet` was not on `PATH`; no SDK install found on the machine) —
I could not run `dotnet build`, `dotnet test tests/sim-core.tests`, or launch
the world-server/client to actually play-test this with two clients. This
change was reviewed by careful manual reading against the existing codebase
conventions, not compiled or executed. A human needs to: run
`dotnet test tests/sim-core.tests` (should be unaffected — this touches no
sim-core file, but confirm), start two world-server-connected clients, walk
one player more than `Tuning.InterestRadiusChunks` chunks away from the
other, and confirm (a) the far player's body disappears client-side instead
of freezing in place, and (b) walking back into range makes them reappear
cleanly (not duplicated). Also worth a build-warnings check — I'm confident
in the diff's syntax but have not had a compiler confirm it.

**Rejected tonight:** Did not attempt Phase 2 (new feature) — the audit
turned up a finding at/above the "must fix, skip Phase 2" threshold, so per
the standing instructions this session stopped at one complete fix rather
than also proposing a feature. Also considered, and rejected as out of scope
for tonight: (1) fixing the synchronous-gateway-call stall in the same pass —
rejected because it's a larger, riskier redesign (async queue) that deserves
its own recorded decision rather than a same-night bundled change; (2)
extending interest-management filtering to `StructurePlaced`/`TileChanged`
broadcasts in the same commit — rejected to keep tonight's change to exactly
one thing, since those are lower-frequency events and the fix likely wants a
proper spatial index rather than the same naive per-recipient scan, given
structures accumulate over time in a way live players don't.

**Added to backlog:** synchronous gateway calls blocking the tick loop
(12), no automated tests for world-server/gateway (12), `World3D.cs` and
`SurvivalHud.cs` god scripts (6 each), unfiltered structure/tile broadcasts
(6), unbounded `RequestChunk` (4). Full detail in `BACKLOG.md`.

**Question for the human:** None blocking — but flagging non-blocking: this
session had no .NET SDK, so nothing was compiled or run tonight. If nightly
sessions are expected to build/test/playtest (the "Definition of Done"
checklist assumes this), the sandboxed environment needs a .NET SDK install
step, or future nightly reports will keep saying "not verified" for anything
beyond manual code reading.
