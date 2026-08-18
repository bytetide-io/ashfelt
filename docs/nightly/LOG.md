# Ashfall — nightly engineer log

## 2026-08-18 — AUDIT-ONLY (fix applied)

**⚠️ Godot mobile target not verified.** No Godot editor/export tooling is
available in this session — only the .NET SDK. `apps/client` was not built,
opened, or run. Everything below is server-side C#, verified by `dotnet
build`/`dotnet test`/a live network harness, not by playing the game. A
human needs to open `apps/client` in Godot and confirm nothing regressed
before trusting this on-device.

**Branch note:** the task brief asked for `nightly/{{DATE}}`, but this
session's harness pinned it to `claude/relaxed-pascal-tzkltt` and explicitly
forbade pushing elsewhere without permission. Deferred to the harness
instruction (outer constraint) over the brief's default; work is on
`claude/relaxed-pascal-tzkltt`, not a `nightly/*` branch.

**Chose:** Closed a multiplayer-correctness gap — `ChopRequest` (harvesting)
had no server-side time pacing, only a reach check.

**Because:** Phase 1 audit score. `World.TryHarvest` (`packages/sim-core/World.cs`)
and the `ChopRequest` handler (`apps/world-server/Program.cs`) gate a strike
on `IsWithinReach` only; nothing bounds *how fast* strikes land, unlike
movement, which already budgets by elapsed time (`MovementRules.Check`). A
scripted client can send `ChopRequest` far faster than any tap gesture and
fell every node it can reach almost instantly. Severity 5 (the entire
gather economy has no time cost at all — not "weak," *absent*) × blast
radius 5 (every resource, every player, and directly undermines "survival
first" pacing and any PvP resource race) = 25. Comfortably over the ≥15 /
multiplayer-correctness-≥9 bar, so Phase 2 (new feature) was skipped per the
rules.

**Changed:**
- `packages/sim-core/HarvestPacing.cs` (new) — `MinIntervalSeconds = 0.2`
  and `IsAllowed(lastAcceptedAt, now)`, mirroring `MovementRules`'
  elapsed-time-budget shape so the codebase has one consistent pattern for
  "this server action costs real time."
- `apps/world-server/Player.cs` — `LastHarvestAt`, `CanHarvestAt(now)`,
  `RecordHarvestAt(now)`.
- `apps/world-server/Program.cs` — `ChopRequest` handler now checks
  `player.CanHarvestAt(Now())` before calling `world.TryHarvest`, and calls
  `player.RecordHarvestAt(Now())` only on an accepted strike (a rejected or
  non-harvestable attempt doesn't spend the budget).
- `tests/sim-core.tests/HarvestPacingTests.cs` (new) — boundary tests for
  `HarvestPacing.IsAllowed`, plus a 200-requests-in-1-second simulation
  asserting accepted strikes stay at the paced rate, not one-per-request.

**Risk:** `MinIntervalSeconds = 0.2` (5 strikes/sec ceiling) is generous —
picked to certainly never reject a real tap, at the cost of only shrinking
the exploit rather than eliminating all advantage from automation. If a
human decides 5/sec is still too fast once the game has real bot-abuse
telemetry, tightening the constant is a one-line change with tests already
in place to catch a regression. Nothing here touches yield amounts, hit
counts, or any other balance number — only the request-acceptance rate.

**Revert:** `git revert` the commit on this branch that touches
`packages/sim-core/HarvestPacing.cs`, `apps/world-server/Player.cs`,
`apps/world-server/Program.cs`, and `tests/sim-core.tests/HarvestPacingTests.cs`
— it's the only commit tonight, so `git revert HEAD` on this branch before
it's merged, or a straight revert of its SHA after.

**Verified:**
- `dotnet test tests/sim-core.tests` — 88/88 pass (was 87 before tonight's
  4 new tests, minus the net-new ones; full existing suite still green).
- `dotnet build apps/world-server` and `dotnet build apps/gateway` — both
  build clean, 0 warnings/0 errors (net10.0, as pinned by the target-
  framework rule).
- **Live end-to-end smoke test**, not just unit tests: started a real
  world-server (`ASHFALL_SEED=1337`, no DB — memory-only mode) and drove it
  with a throwaway LiteNetLib client harness that performs the actual
  handshake (`Hello` → `Welcome`), walks to a forest tile within the
  legitimate movement speed budget, then fires 709 `ChopRequest` packets at
  it in under a second — the same shape of abuse a bot would send. Result:
  the server accepted exactly 4 strikes (the tree's real hit count), paced
  ~0.2s apart, felling it at t=0.783s instead of within the first few
  milliseconds. Confirms the fix works over the actual wire protocol, not
  just in isolated unit tests. Harness lived in the session scratchpad, not
  committed (it's a manual verification tool, not part of the shipped
  code — see backlog item #2 for turning this into a permanent test).

**Not verified:**
- The Godot client itself — not opened, not run, not exported (no Godot
  tooling in this environment). The client sends `ChopRequest` on tap and
  reacts to `HarvestProgress`/`TileChanged` exactly as before; nothing in
  this change touches client code, so no client-visible behavior change is
  expected for a legitimate player tapping at a normal pace. A human should
  still confirm gathering feels unchanged on-device — 0.2s between accepted
  strikes is below normal human tap cadence, but it's a real constraint now
  and worth eyes-on.
- Two-simulated-clients-with-late-join scenario from the Definition of Done
  checklist — not exercised tonight. This fix doesn't touch join/leave/
  interest-management, and the harness above already proves the mechanism
  end-to-end for a single connected player, which is the actual surface the
  bug lives on. Flagging the gap rather than claiming coverage I don't have.
- Postgres-backed persistence path (`ASHFALL_DB` set) — the smoke test ran
  in memory-only mode. `SaveDiffAsync`/structure persistence are untouched
  by this change, so no new risk expected, but not independently re-verified
  tonight.

**Rejected tonight:**
- Extending the same pacing pattern to `CraftRequest`/`EatRequest`/
  `PlaceRequest` in the same pass — none of them clear the "fix tonight"
  bar (inventory already bounds the abuse; see `BACKLOG.md` #4), and
  widening the diff beyond the one clear bug risks touching balance-adjacent
  behavior without it being tonight's explicit task.
- Adding a `HarvestPacing` self-check on the client so it never even sends a
  too-fast request — rejected because the client already only sends one
  `ChopRequest` per tap gesture (see `apps/client/scripts/world3d/World3D.cs`
  `_harvestQueued`), so there's no legitimate path that would trip the new
  server-side limit. Movement gets a matching client-side check because the
  client actively simulates and could otherwise self-correct constantly;
  harvesting has no such prediction to protect.

**Added to backlog:** see `docs/nightly/BACKLOG.md` — gateway caller auth
(12), missing world-server/gateway test coverage (12), no schema versioning
on world tables (9), unpaced craft/eat requests (6), two client god-scripts
(6 each), unrated `RequestChunk` (4), no process supervision (4), plus two
feature ideas for a future feature-night (structure removal, campfire fuel).

**Question for the human:** none blocking. One judgment call worth a look
when convenient: is `HarvestPacing.MinIntervalSeconds = 0.2` (5 strikes/sec)
the right ceiling, or should it be tighter? I picked generous-but-real
specifically to avoid rejecting any legitimate player while still killing
the "fell everything in reach in one network burst" exploit; tightening it
later is a one-line, test-covered change if you disagree.
