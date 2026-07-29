# Ashfall — nightly log

## 2026-07-29 — AUDIT-ONLY

**Chose:** Full-codebase audit; created `docs/nightly/{ARCH,BACKLOG,LOG}.md`
(none existed before tonight). No gameplay/netcode code was changed.

**Because:** The audit turned up one finding that scores at the "must fix
tonight" threshold (interest management is entirely unimplemented for
player-state and structure broadcasts — Severity 4 × Blast radius 4 = 16;
see BACKLOG #1) — but this session's sandbox has **no .NET SDK and no
running Docker daemon**, and the outbound proxy denies fetching one
(`builds.dotnet.microsoft.com` → 403, confirmed via
`/__agentproxy/status` as an org policy denial, not a transient failure).
Per the proxy's own instructions, a policy denial is to be reported, not
routed around.

That means I could not `dotnet build`, `dotnet test`, or launch anything
this session. This repo's own rule is "`dotnet test tests/sim-core.tests`
must pass before any commit," and the nightly protocol's own escape valve is
"if you run out of session budget mid-change, revert to a clean state rather
than leaving the tree broken." I judged that hand-verifying a change to
`apps/world-server/Program.cs` — the one file in the repo with the *least*
test coverage, doing the trickiest part of the fix (interest-management
netcode) — by eye alone, with no compiler and no test runner to catch a
mistake, was a worse outcome than not shipping it. So I did the safe
equivalent of "revert to clean": audit and log precisely, don't guess at a
fix I can't check.

**Changed:** `docs/nightly/ARCH.md`, `docs/nightly/BACKLOG.md`,
`docs/nightly/LOG.md` (new files, docs only). No `apps/` or `packages/` code
touched.

**Risk:** None to the running game — nothing executable changed. The risk is
entirely opportunity cost: BACKLOG #1 (interest management) and #2 (no
world-server/gateway test coverage) are real and now scored, but still
unfixed, for one more night.

**Verified:** Read every file in `apps/` and `packages/` end to end (line
counts, message flow in `world-server/Program.cs`, gateway voyage SQL,
`sim-core` rule files against their existing tests) by inspection. Confirmed
by grep that `Tuning.InterestRadiusChunks` and `RequestChunk` are each
declared but never actually used where the docs imply they should be.
Confirmed the sandbox has no `dotnet` binary, no Docker daemon
(`docker info` fails to connect), and that `apt-get install dotnet-sdk-8.0`
and the official `dotnet-install.sh` both fail (404 from the Ubuntu mirror
for that exact point version; 403 policy denial from the proxy for the
official installer host).

**Not verified:** Everything that would normally require running the game:
whether the client currently builds/imports cleanly in Godot, whether
`dotnet test tests/sim-core.tests` currently passes (I have no reason to
think it doesn't — the test files read as consistent with the rule files —
but I did not run it), and obviously nothing about the mobile target launch.
A human needs to confirm the repo is still green with the SDK before relying
on anything above beyond "these files are new markdown."

**Rejected tonight:** Attempting the BACKLOG #1 fix anyway and asking the
human to verify it in the morning — rejected because a fix to unbroadcast
logic that's subtly wrong (e.g. filters out a player who should still see a
correction, or a race between a fresh join's backfill and a concurrent
placement) fails silently in exactly the multiplayer-desync way this audit
exists to catch, and I'd have no way to catch that failure myself before
handing it over. Writing gateway/world-server tests without being able to
compile them — rejected for the same reason; an uncompilable test file is
strictly worse than no test file, since it looks like coverage that isn't
real.

**Added to backlog:** See `docs/nightly/BACKLOG.md` — 5 scored findings:
(1) no interest management, player/structure broadcasts unbounded with world
age [16]; (2) zero test coverage for world-server/gateway, notably the
voyage no-duplication guarantee [12]; (3) two client files over the 400-line
guideline [4]; (4) sibling `GetNode("../X")` coupling in 3 call sites [4];
(5) no migration-tracking table [6].

**Question for the human:** Is the sandbox's missing .NET SDK / blocked
`builds.dotnet.microsoft.com` / no Docker daemon expected for this
environment, or is it a setup gap? If a future nightly session is meant to
actually build and ship code, it needs the SDK (and, for gateway/voyage
work, a way to run Postgres) available — otherwise every nightly session
hits this same wall and can only audit, never ship.
