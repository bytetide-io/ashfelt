# Ashfall — nightly engineer log

## 2026-08-10 — AUDIT (fixed a ≥threshold multiplayer-correctness finding)

**Chose:** Filtered the world-server's per-tick player-position broadcast
(`PlayerStates`) and player-departure signal (`PlayerLeft`) by chunk-distance
interest range, instead of unconditionally broadcasting every player's
position to every connected player every tick.

**Because:** `docs/architecture.md` invariant #5 and `CLAUDE.md`'s
load-bearing invariant #6 both state "a client only ever receives
entities/chunks near it" — but `apps/world-server/Program.cs` broadcast
`PlayerStates` (15 Hz, unreliable) to every connected peer with no distance
check at all, and `Tuning.InterestRadiusChunks` — a constant clearly meant
for exactly this — was declared in `packages/shared-proto/Protocol.cs` and
never referenced anywhere. `docs/gameplay-roadmap.md` §3.3 even assumes this
already works ("the interest-management path that already exists for
players... extend it, don't fork it"), which is false in the current code.
Audit score: severity 4 (violates a named load-bearing invariant, docs
actively assert it's already true, and it's the one broadcast that fires
every tick rather than on an event) × blast radius 4 (the entire real-time
state channel, and every future per-tick broadcast would inherit the same
gap) = 16, clearing the "≥15, must fix tonight, skip Phase 2" bar on its own.

**Changed:**
- `packages/sim-core/InterestRules.cs` (new) — pure `InRange(viewer, subject,
  radiusChunks)` square-chunk check, so the rule is shared and testable
  rather than living inline in netcode.
- `tests/sim-core.tests/InterestRulesTests.cs` (new) — boundary tests
  (same chunk, exactly at radius, one past it, diagonal, symmetry).
- `apps/world-server/Player.cs` — added `Chunk` (computed from `Position`)
  and `VisiblePlayerIds` (what this player was sent last tick, so the next
  tick can diff and detect who dropped out of range).
- `apps/world-server/Program.cs` — `PlayerStates` is now built per-viewer
  instead of once and broadcast; a player who leaves another's interest
  range gets a targeted `PlayerLeft`, not silence. Also clears a
  disconnecting player's id out of everyone else's `VisiblePlayerIds` so the
  next tick doesn't send a redundant duplicate on top of the existing
  disconnect broadcast.

**Scope note (read this before assuming the invariant is now fully true):**
This fixes the per-tick player-position channel only — the highest-severity
part, since it's the one that fires every 66ms regardless of activity.
`TileChanged`/`HarvestProgress`/`StructurePlaced` broadcasts and the
join-time structure backfill are still world-wide. I chose not to bolt a
one-off filter onto those tonight: a correct fix needs dynamic per-player
chunk subscription (stream structures into view as a player walks, not just
at join), which is what `docs/gameplay-roadmap.md` §3.3's planned entity
system is explicitly designed to provide ("extend it, don't fork it"). A
narrower patch tonight would either be incomplete (backfill-only, so
structures placed elsewhere while you're away never stream in) or would
pre-empt and likely conflict with that planned design. Logged as
`BACKLOG.md` #1 with its own score (9) so it isn't lost, and pointed at
`InterestRules.InRange` as the rule to reuse when that work happens.

**Risk:** A bug in the per-viewer filtering could make remote players
invisible or stuck. The specific failure mode I was most worried about —
a player walking out of range and freezing in place on other clients
instead of disappearing — is exactly what `VisiblePlayerIds` + the targeted
`PlayerLeft` exists to prevent; it reuses `RemotePlayers.Remove` on the
client, which was already correct and idempotent. To spot a regression:
watch for remote-player capsules that stop moving but never disappear, or
players who report seeing others pop in/out near the interest boundary more
jarringly than expected (radius is currently 1 chunk = 64m either side,
`Tuning.InterestRadiusChunks`).

**Revert:** `git revert` the commit on this branch that touches
`InterestRules.cs`, `InterestRulesTests.cs`, `Player.cs`, `Program.cs` — a
single, isolated commit; no schema or protocol version change, no other
system touched.

**Verified:**
- `dotnet test tests/sim-core.tests` — 89/89 passing (85 pre-existing + 4 new
  `InterestRulesTests`).
- `dotnet build` clean (0 warnings, 0 errors) for `packages/sim-core`,
  `packages/shared-proto`, `apps/world-server`, `apps/gateway`, and
  `apps/client/AshfallClient.csproj` (client assemblies only — see "not
  verified").
- Ran the real `world-server` binary against a throwaway 3-client harness
  (raw LiteNetLib, referencing `sim-core`/`shared-proto` directly — not
  committed to the repo) that joins two clients at the same spawn, confirms
  each sees the other in `PlayerStates`, walks one client ~250m away (a
  legal move given the elapsed-time budget) and confirms the other client's
  `PlayerStates` drops it *and* receives a targeted `PlayerLeft`, then joins
  a third client late near the original spawn and confirms it sees the
  still-nearby player but not the one who walked away, and that the nearby
  player sees the late joiner appear. All 9 checks passed.

**Not verified:** I do not have a Godot editor or headless Godot binary in
this environment, so I could not launch the actual mobile client, click
through the HUD, or confirm this looks right on-screen — only that the wire
protocol and server logic behave correctly under a raw-protocol harness. A
human should do one real two-device (or two-editor-instance) session and
watch a remote player disappear/reappear crossing the interest boundary
before trusting this beyond the harness result. The `.NET` SDKs were not
present in this environment either; I installed `dotnet-sdk-8.0` and
`dotnet-sdk-10.0` from Ubuntu's own package repos (Microsoft's own CDN,
`builds.dotnet.microsoft.com`, is blocked by this session's egress policy)
to be able to build and test at all — worth confirming CI still resolves
its own SDKs fine since it uses `actions/setup-dotnet`, a different install
path than the one I used here.

**Rejected tonight:**
- *Fixing all four broadcast types + backfill in one pass.* Rejected because
  doing structures/tiles correctly needs dynamic chunk subscription, which
  is bigger than "the audit's one thing" and risks exactly the half-finished
  state the brief warns against.
- *A general per-entity interest/subscription framework now, ahead of
  schedule.* Rejected because `docs/gameplay-roadmap.md` §3.3 already plans
  this deliberately, paired with the entity system that doesn't exist yet;
  building the framework in isolation tonight would guess at a shape the
  entity system might not want, and the brief says not to repeat/pre-empt
  planned work without justifying it — better to leave the tested primitive
  (`InterestRules.InRange`) for that work to pick up.

**Added to backlog:** see `docs/nightly/BACKLOG.md` — remaining
interest-management scope (9), `SurvivalHud.cs` god-script (9),
`Program.cs` message-dispatch scaling (6), `World3D.cs` god-script (6), no
schema version on persisted tables (6), identical spawn point for every
player (4), unbounded `RequestChunk` (2), tick-loop drift under load (2).

**Question for the human:** none blocking — the one thing worth a quick
look is confirming on a real device/editor that a remote player actually
disappears cleanly when they cross the interest boundary, per "not
verified" above.

**Process note:** the task prompt for this routine says to work on branch
`nightly/{{YYYY-MM-DD}}`; this session's git integration setup explicitly
assigned branch `claude/relaxed-pascal-eekyf3` for all work in this repo and
said never to push elsewhere without explicit permission. I followed the
more specific, current instruction (`claude/relaxed-pascal-eekyf3`) and
did not create a `nightly/2026-08-10` branch — flagging the mismatch here
rather than silently picking one.
