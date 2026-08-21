# Nightly log

Newest entry first. Each entry is the morning report from that night's
session — see the routine's prompt for the required shape.

---

## 2026-08-21 — AUDIT-ONLY (completed a backlogged fix, not a new feature)

**Chose:** Made the world-server's gateway calls (character claim on join,
character save + voyage mint on release) non-blocking, so they no longer
freeze the single-threaded tick loop for every connected player.

**Because:** This is the finding the 2026-07-24 session logged to
`BACKLOG.md` and explicitly deferred ("World-server blocks its single
packet/tick thread on gateway HTTP calls", scored **Severity 4 × Blast
radius 5 = 20**, tied with that night's ownership fix but judged to need its
own session rather than being squeezed in alongside another structural
change). Nothing about the finding has changed since — `Hello` and
`RequestRelease` in `apps/world-server/Program.cs` still called the
gateway's REST API via `.GetAwaiter().GetResult()` directly inside the
single-threaded UDP event loop, so every player's movement, harvesting and
building froze for the duration of each HTTP round trip, on every join and
every voyage. `GatewayClient`'s `HttpClient` had no explicit timeout, so a
slow-but-not-down gateway could freeze the whole world for up to the BCL
default of 100 seconds; a hung gateway, indefinitely. Re-scored against
today's code: unchanged at 20 (multiplayer-correctness ≥9 alone means fix
tonight, skip Phase 2 — same rule the 2026-07-24 session applied).

Worth being explicit about process, since this backlog item sat for four
weeks of intervening feature work (blueprint building, roof shelter, the
character-ownership claim fix) between when it was logged and when it was
picked up: this session started by re-auditing the code cold, arrived at
the *same* finding independently (score 25 by this session's own
math — 5×5 rather than 4×5, immaterial, same conclusion), and only
discovered mid-session, while investigating why a first draft PR based on a
stale branch conflicted with `main`, that it was already a known, scored,
written-up backlog item from a prior night. That's a process gap worth
naming: this session's branch was created without fetching latest `main`
first, so the audit ran against a codebase roughly four weeks and 28
commits stale — including the entirety of blueprint building, the roof/
shelter mechanic, and the prior nightly session's ownership-claim fix. The
first draft of this fix was written against that stale code and had to be
discarded and rewritten against current `main` once the gap was caught (see
git history on this session's branch — the branch was hard-reset onto
`origin/main` mid-session, nothing from the stale draft survived). **Future
nightly sessions should `git fetch origin main` and branch from
`origin/main`'s tip before Phase 1, not just from whatever the workflow
handed them.**

**Changed:**
- `apps/world-server/GatewayClient.cs` — explicit 8s `HttpClient.Timeout`
  (defense in depth; the real fix is below, but a call kicked off from a
  background continuation should never be allowed to hang a thread-pool
  worker forever either).
- `apps/world-server/Program.cs` — `Hello` and `RequestRelease` no longer
  block the event handler. Each kicks off an `async Task` local function
  (`HandleHelloAsync` / `HandleReleaseAsync`) that awaits the gateway calls
  off-thread and lands its result through a new `ConcurrentQueue<Action>`
  (`pending`), drained once per tick on the main loop — the only place now
  allowed to touch `writer`, `players`, `buildSites`, or a `NetPeer`. This is
  almost exactly the fix the 2026-07-24 backlog entry sketched ("kick off
  the HTTP call as a real Task, park the connecting peer in a pending state
  that only skips gameplay messages, drain completions from a thread-safe
  queue on the next tick"). It also mirrors the fire-and-forget discipline
  already used for Postgres writes (`_ = store.SaveDiffAsync(...)`), applied
  now to the gateway calls that previously blocked.
- `apps/world-server/Player.cs` — added `Ready` (true once the async Hello/
  claim completes) and `Leaving` (true while a release is in flight) flags.
- Every message type except `Hello` is now rejected until `player.Ready`.
  Necessary once the claim is async: previously it resolved synchronously in
  effectively zero time, so a client's next message could never race it.
  Now there's a real (if usually small) window where a `ChopRequest` or
  `BuildRequest` could otherwise land on a still-default inventory/build
  state the async `LoadCharacter` call is about to overwrite — this closes
  that window rather than opening it. The pre-existing synchronous duplicate-
  connection guard (same `CharacterId` connecting twice) still runs before
  any claim starts, so it isn't weakened by any of this — see the comment at
  its call site.
- A duplicate `RequestRelease` while one is already in flight is now ignored
  (`player.Leaving` guard) instead of racing a second save/mint.
- The disconnect handler's own fire-and-forget character save is skipped
  while `player.Leaving` is true, since `HandleReleaseAsync` already saves
  (before it even requests a ticket) — avoids two concurrent saves of the
  same character potentially landing out of order.

**Risk:** Core join/leave/voyage path, changed without the ability to
compile it — see Verified/Not verified below. If there's a bug, the likely
symptom is one of: a join that never completes (stuck on "Connecting…" —
check for an exception path inside `HandleHelloAsync` that returns without
enqueuing anything onto `pending`), a voyage that hangs after "Voyaging
to…", or (lower probability, would show up as a compile error) a mistake in
the new local functions since none of this was fed through `dotnet build`.

**Revert:** `git revert <this-commit-sha>` — one self-contained commit
touching exactly `Program.cs`, `Player.cs`, `GatewayClient.cs`; the pre-image
is the fully-synchronous handshake and it worked, so a straight revert is
safe if the new version misbehaves. No database migration involved.

**Verified:** Read the full diff twice against the pre-image, confirming:
the wire protocol is unchanged (checked `WorldConnection.cs` on the client —
it has no synchronous-timing assumption anywhere, it already just waits for
whatever arrives); every new failure path (gateway exception, bad ticket,
character owned by another world, missing target world, peer disconnected
mid-flight) either enqueues a denial/log or is a documented no-op, so
nothing silently swallows a failure the old code used to report; every
queued completion (`CompleteHello`, `CompleteRelease`, and the inline
disconnect actions) is guarded by `StillConnected` so nothing sends to, or
mutates state for, a peer that disconnected while its gateway call was in
flight. Re-verified the synchronous duplicate-connection guard still runs
before `player.CharacterId` is set and before any `await`, so two Hello
messages arriving back-to-back on the single-threaded loop still resolve in
order exactly as before. Traced the gateway's own `/characters/{id}/claim`,
`/voyage`, and `/voyage/claim` endpoints (`apps/gateway/Program.cs`) to
confirm they're already race-safe at the SQL level regardless of
client-side timing — this fix doesn't depend on the gateway being any more
careful than it already is, and nothing there needed to change.

**Not verified — a human needs to check this on a real machine before
trusting it:** **The code has not been compiled.** This sandbox has no .NET
SDK and no network path to install one — confirmed by trying `dot.net`
(the official install script), Microsoft's package feed, and Ubuntu's
`security.ubuntu.com` `dotnet-sdk-*` packages; all three are blocked by the
environment's egress allowlist. Before merging: `dotnet build
apps/world-server` (and `apps/gateway`, unaffected but in the same solution
shape), then `dotnet test tests/sim-core.tests` (untouched by this change,
should be unaffected, but is the project's one real test gate). Then the
actual scenario this fix targets: run the gateway + world-server, connect
two clients, and confirm (a) both join and see each other, (b) a client
that connects while the gateway is paused/unreachable eventually times out
cleanly at ~8s instead of hanging forever and doesn't wedge the other
client's movement, (c) a voyage between `continent-a` and `continent-b`
still round-trips a character correctly, (d) blueprint building (commit/
deposit/build/cancel) still works for a player who joined normally, since
those message types are now gated behind the same `Ready` flag. None of
this was possible to exercise here.

**Rejected tonight:**
- *Bound the blocking calls with just a timeout, leave them synchronous.*
  Rejected because it only shrinks the blast radius (100s → 8s) without
  removing it — every player would still freeze for up to 8 seconds on every
  single join under normal gateway latency variance, still a perceptible
  multiplayer-correctness bug, just a smaller one. The non-blocking version
  costs the same 8s worst case but only to the one player who is joining or
  leaving.
- *Move the gateway HTTP calls onto a real message queue / actor mailbox per
  player.* Rejected as over-engineering for a single-world-server process
  with the tiny message volume here (joins and voyages are rare compared to
  the 15Hz tick); the `ConcurrentQueue<Action>` drained once per tick gives
  the same safety property (all shared state touched from one thread only)
  with far less new machinery, and matches the fire-and-forget pattern
  already established in this file.
- Did not start on Phase 2 (new feature) at all — the decision rule says
  skip it entirely when a fix-tonight finding exists, and completing this
  backlogged one counted as exactly that.

**Added to backlog:** No new findings — this session's audit reproduced the
2026-07-24 list rather than surfacing anything new (the codebase moved a
lot since, but in ways that don't change the earlier scoring: the god-script
line counts, the missing gateway/world-server test coverage, and the
migration-runner gap are all still accurate as described in `BACKLOG.md`).
Marked the blocking-gateway-calls entry `[FIXED 2026-08-21]` there rather
than deleting it, matching the convention the prior entry set.

**Question for the human:** Two, neither blocking: (1) should nightly
sessions running in this sandbox type start with a mandatory `git fetch
origin main` + branch-from-tip step before Phase 1, given tonight's process
gap? (2) as the 2026-07-24 entry also asked — should nightly sessions get a
sandbox with the .NET SDK preinstalled? Two sessions in a row have now
shipped C# changes they could not compile.

---

## 2026-07-24 — AUDIT-ONLY (fix, not feature)

**Chose:** Closed a character-duplication hole: the world-server's normal
(non-voyage) join path loaded a character from the gateway with no ownership
check, so the same character UUID could be loaded concurrently by two live
sessions — same world server twice, or two world servers at once — each
free to independently craft/gather against its own in-memory copy of one
inventory.

**Because:** First-ever run of this nightly routine, so `docs/nightly/`
didn't exist yet (created this session, `ARCH.md` populated as an honest map
of the current codebase — see that file). Phase 1 audit ran two parallel
sub-agent reviews (server/gateway multiplayer-correctness; client
architecture) plus my own reading of `world-server/Program.cs`,
`Player.cs`, `gateway/Program.cs`, and the SQL migrations. The ownership gap
scored **Severity 5 × Blast radius 4 = 20** (agent-verified) — independently
reproduced by my own read of the same code before the agent reported back.
That clears both fix-tonight thresholds (≥15 overall, ≥9 multiplayer-
correctness), so per the routine's decision rule Phase 2 (new feature) was
skipped entirely. A second finding scored the same 20
(`world-server` blocks its one packet thread on synchronous gateway HTTP
calls) but needs a real architecture change, not a one-night fix — logged to
`BACKLOG.md` instead of attempted alongside this one, per "ship one thing."

**Changed:**
- `apps/gateway/Program.cs` — replaced `GET /characters/{id}` (loaded a
  character with no ownership check at all) with
  `POST /characters/{id}/claim`, which atomically claims-and-loads in one
  SQL statement (`INSERT ... ON CONFLICT (id) DO UPDATE ... WHERE
  owner_world_id IS NULL OR owner_world_id = $requestingWorld`). A claim
  denied by another world's ownership returns `409 Conflict`; a
  never-before-seen UUID is created fresh and claimed in the same call. No
  schema change — `character.owner_world_id` already existed
  (`infra/migrations/003_voyage.sql`), it just wasn't being enforced on the
  common (non-voyage) join path.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` →
  `ClaimCharacterAsync(id, worldId)`, calling the new endpoint; `null` now
  means "denied," not "never saved" (a never-saved character now comes back
  with default state instead of 404/null).
- `apps/world-server/Program.cs` (`Hello` handler) — two additions: (1) a
  same-process guard rejecting a second connection presenting a `CharacterId`
  already held by a live `Player` (closes the same-world-twice case the
  gateway claim alone can't, since re-claiming by the *same* world id is
  intentionally allowed for ordinary reconnects); (2) the load step now calls
  `ClaimCharacterAsync` instead of a plain load, and — this is a real
  behavior change — a denied or failed claim now disconnects the peer
  instead of silently letting them join with default state. The old
  silent-continue-on-load-failure was itself a smaller footgun (a transient
  gateway blip could reset, then overwrite-on-save, a real character); I
  judged closing that alongside the main fix as directly in scope rather than
  scope creep, since it's the same code path and the same "don't proceed
  without confirmed ownership" principle. Flagging it explicitly in case a
  human disagrees with bundling it in.
- `apps/gateway/API.md` — documented the new endpoint, removed the stale GET
  docs, noted PUT doesn't touch ownership.
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — created (didn't exist before
  tonight).

**Risk:** This changes the *dev workflow* for manually pointing a client at
a different world-server without going through the voyage flow (mentioned in
the root `README.md` as a supported LAN/VPS testing trick): a character
already owned by `continent-a` will now get a `409` if you connect straight
to `continent-b` without voyaging, where before it would silently load a
second copy. That's the invariant working as documented
(`docs/voyage-transfer.md`: "owned by exactly one world-server at a time"),
not a regression, but it's a visible behavior change a human should know
about — to test on a second world, either use a fresh device UUID or run the
actual voyage flow.

Smaller risk: the disconnect-on-claim-failure behavior change above means a
transient gateway outage now blocks joins outright instead of letting
players in with reset state. I believe this is strictly better (no silent
data loss) but call it out since it wasn't explicitly requested.

**Revert:** `git revert` the commit on this branch that touches
`apps/gateway/Program.cs`, `apps/gateway/API.md`,
`apps/world-server/GatewayClient.cs`, and `apps/world-server/Program.cs`.
No database migration was added or needed, so a code revert alone is
sufficient and fully reversible.

**Verified:** Read every touched file end-to-end after editing; balance-
checked braces/parens programmatically; traced the SQL upsert's
`ON CONFLICT ... WHERE` semantics by hand against Postgres's documented
behavior (a false `WHERE` guard skips the update and returns no row — this
is the exact mechanism the pre-existing `voyage_ticket` upsert in this same
file already relies on, so the pattern is proven elsewhere in this codebase,
not novel). Matched the new gateway endpoint's mixed-`IResult`-branch return
style against the three other endpoints in the same file that already do the
same thing successfully. Confirmed the JSON property-casing convention
(`new { worldId }` → binds to a `WorldId` record property) matches the
existing `RequestVoyageAsync`/`ClaimVoyageAsync` calls in the same file.

**Not verified — a human must check on a real setup:** *Nothing in this repo
was built, run, or tested tonight.* This session had no working `dotnet`
SDK (not installed; the network policy denies
`builds.dotnet.microsoft.com`, which blocked installing one) and no Docker
daemon (CLI present, socket absent, so `docker compose up` for Postgres
wasn't possible either). Concretely, a human needs to:
1. `dotnet build` the whole solution — I have not confirmed this compiles.
2. `dotnet test tests/sim-core.tests` — untouched by this change, should
   still pass, but not actually run.
3. Bring up `infra/docker/docker-compose.yml`'s Postgres, run the gateway
   and two world-server instances, and manually reproduce the exact race
   this fix targets: connect two clients with the same device UUID (same
   world, then different worlds) and confirm the second is rejected with a
   log line, not a second live session.
4. Confirm a legitimate voyage (A → B) still works end-to-end: the ticket
   claim sets ownership, and the immediately-following character claim in
   the `Hello` handler is a same-world no-op that succeeds.
5. Confirm the client build still launches for the mobile target — untouched
   by tonight's change (client code wasn't modified at all), but I can't
   personally confirm that from this session.

**Rejected tonight:** Didn't attempt the also-20-scoring blocking-HTTP-call
finding in the same session — bundling two structural server changes in one
unreviewed night, with no way to build or test either, is exactly the
"five speculative changes instead of one complete one" this routine warns
against. Didn't touch the `World3D.UpdateGatherPrompt` mobile-perf finding
(score 12, under the fix-tonight threshold and not multiplayer-correctness).
Didn't start on Phase 2 (new feature) at all — the decision rule says skip
it entirely when a fix-tonight finding exists, and one did.

**Added to backlog:** See `docs/nightly/BACKLOG.md` for the full scored
list — blocking-gateway-calls (20), gather-prompt perf (12), SurvivalHud/
World3D god-scripts (9 each), WorldConnection dispatch switch (9), foliage
triplication (6), node-path coupling (4), plus unscored notes on the missing
migration runner and the total absence of gateway/world-server test
coverage.

**Question for the human:** None blocking — the fix is self-contained and
reversible with a single revert, and the one real workflow change (manual
cross-world testing needs a real voyage or a fresh UUID now) is documented
above rather than something I need a decision on. The one thing worth your
attention regardless: please run the build/test/manual-race verification
listed above before this reaches anything with real player data, since none
of it happened automatically tonight.
