# Nightly log

## 2026-07-24 — AUDIT-ONLY

**⚠️ Could not build, run, or test anything tonight.** This container has
no .NET SDK (`dotnet` is not on `PATH`, and there is no `dotnet` binary
anywhere on the filesystem). CLAUDE.md requires
`dotnet test tests/sim-core.tests` to pass before any commit, and the
nightly Definition of Done requires multiplayer-touching code to be
tested with 2+ simulated clients — neither was possible. Given that, and
given the audit found nothing whose bug is severe enough to outweigh
shipping a change I have zero ability to compile-check against a
server-authoritative codebase, I did not touch any code. This is a
docs-only night: `docs/nightly/{LOG,BACKLOG,ARCH}.md` created (none
existed before tonight) and populated.

**Chose:** Audit-only — survey the codebase, write the three nightly
state files, fix nothing.
**Because:** No finding cleared the "must fix tonight" bar in a way I'd
stand behind without being able to build it. Two findings scored 12
(interest management gap, blocking gateway calls on the tick loop) —
close to the ≥15/≥9-multiplayer threshold that would normally mandate a
fix — but both are pre-existing, *working* tradeoffs (one documented
in-code as deliberate, the other harmless at current player counts), not
active bugs. Landing an unverified change to the authoritative tick loop
risked breaking a currently-correct system for zero verified benefit.
Audit-only is the reversible choice; a half-verified netcode change is
not.

**Changed:** `docs/nightly/ARCH.md` (new), `docs/nightly/BACKLOG.md`
(new), `docs/nightly/LOG.md` (this file, new). No application code
touched.

**Risk:** None — no code changed. The only risk is this log being wrong
about something (see Verified/Not verified below); the fix is a re-read,
not a revert.

**Verified:** Read every non-trivial file in `apps/world-server`,
`apps/gateway/Program.cs`, `apps/client/scripts/WorldConnection.cs`,
`RemotePlayers.cs`, relevant slices of `World3D.cs`, all four SQL
migrations, and `packages/shared-proto/Protocol.cs` end to end.
Cross-checked the `PlayerStates` broadcast against CLAUDE.md invariant #6
and the wire-format doc comment — confirmed the mismatch by reading the
actual tick-loop broadcast code, not by inference. Confirmed no `dotnet`
binary exists anywhere on this filesystem (`which dotnet`, filesystem
search) before deciding not to attempt a build.
**Not verified:** Everything that requires actually running the stack:
whether the client/server/gateway build cleanly right now, whether the
existing `sim-core` test suite currently passes, whether the game
launches on a mobile target, and whether the two backlog items I scored
12 actually manifest the way I predict under real load. A human (or a
future session with `dotnet` available) should run
`dotnet test tests/sim-core.tests` and a manual two-client session before
trusting this audit's severity numbers over a live repro.

**Rejected tonight:** Did not consider a Phase 2 feature — the nightly
prompt only reaches Phase 2 "if the audit found nothing above threshold,"
and while nothing here strictly cleared the threshold, shipping unverified
netcode changes felt like the wrong risk regardless of which phase it's
filed under, and a *new feature* is strictly riskier to ship blind than a
scoped bug fix would have been. Considered attempting the interest-management
fix anyway on the theory that it's additive/filtering-only and hard to get
badly wrong — rejected because "hard to get badly wrong" is exactly the
kind of confidence that doesn't survive contact with an unbuilt C# change.

**Added to backlog:** See `docs/nightly/BACKLOG.md` — 5 items, scores
12, 12, 9, 6, 4.

**Question for the human:** Can this environment get a .NET SDK
installed (or pointed at one) for future nightly sessions? Without it,
every future night is audit-only by necessity, which wastes the "ship one
complete change" half of this workflow.
