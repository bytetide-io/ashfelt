# Nightly log

Newest entry first. Each entry is the morning report from that night's
session — see the routine's prompt for the required shape.

---

## 2026-08-05 — AUDIT-ONLY (fix, not feature)

**Chose:** Fixed the `world-server` blocking-gateway-calls finding the
2026-07-24 audit deliberately deferred: `Hello` (character claim, voyage
ticket claim) and `RequestRelease` (character save, voyage grant) each
called `.GetAwaiter().GetResult()` on a gateway HTTP round trip from inside
the single packet-receive handler, freezing movement/harvest/tick broadcasts
for *every* connected player, on every join and every voyage, for however
long that call took.

**Because:** Phase 1 audit this session was a targeted re-check rather than
a fresh sweep: I re-read the full `docs/nightly/BACKLOG.md` from the last
run, confirmed the highest-scored open item (Severity 4 × Blast radius 5 =
20, multiplayer-correctness ≥ 9) by reading the current
`apps/world-server/Program.cs` end to end — still four blocking call sites,
unchanged from the July 24 description. That clears both fix-tonight
thresholds, so per the routine's decision rule Phase 2 (new feature) was
skipped again. I did not do a fresh audit pass over the building/blueprint/
architect-mode system that landed in the ~15 commits since the last audit
(`9dc784d`..`6731ba0`) — flagged to `BACKLOG.md`/`ARCH.md` for a future
night rather than squeezed in alongside tonight's fix.

**Changed:**
- `apps/world-server/Program.cs` — `Hello`'s ticket claim
  (`ClaimVoyageAsync`) and character claim (`ClaimCharacterAsync`), and
  `RequestRelease`'s save (`SaveCharacterAsync`) + voyage grant
  (`RequestVoyageAsync`), all now run inside a background `Task.Run(async
  () => ...)` instead of blocking the packet thread. Each background task's
  outcome is enqueued as a closure onto a new `pendingGatewayCompletions`
  (`ConcurrentQueue<Action>`), drained once per tick right after
  `server.PollEvents()` — so `players`, the reusable `writer` buffer, and
  every `NetPeer.Send`/`Disconnect` call still have exactly one thread
  touching them, same as before. Four new local functions
  (`CompleteJoin`/`RejectJoin`/`CompleteRelease`/`DenyRelease`) hold the
  logic that used to run inline in the handlers; each checks
  `players.TryGetValue(peer, ...)` first and no-ops if the peer already
  disconnected while its gateway call was in flight.
- `apps/world-server/Player.cs` — two new flags, `JoinPending` (true from
  `Hello` until the claim resolves — `Inventory`/`Survival` are still
  defaults until then) and `Releasing` (true from `RequestRelease` until the
  save+grant resolves). `NetworkReceiveEvent` now drops every other message
  from a connection while either is true, so nothing acts on a half-loaded
  or about-to-be-handed-off player.
- `apps/world-server/Program.cs`, `PeerDisconnectedEvent` — no longer saves
  the character on disconnect when `JoinPending` (nothing was ever loaded,
  so saving would overwrite the gateway's real state with blanks) or
  `Releasing` (the release's own task already owns saving a pre-captured
  snapshot; saving again would just be a redundant identical write).
- A same-session independent review (a second agent, no context from the
  implementing session, told to hunt specifically for races/compile errors)
  caught a real gap: a peer that disconnects mid-claim is removed from
  `players` immediately, so a fast reconnect for the same `CharacterId`
  could start a second, concurrent claim before the first resolved — no
  in-process corruption (both claims are same-`worldId` and idempotent, and
  the stale completion safely no-ops), but it weakened the single-owner
  guarantee the surrounding comments assert. Fixed in a follow-up commit: a
  `claimingCharacters` reservation (`Dictionary<Guid, Player>`, separate
  from `players`), held from `Hello` until whichever of
  `RejectJoin`/`CompleteJoin`/a mid-join disconnect resolves first, keyed by
  the owning `Player` so a late-resolving claim can't clear a newer
  reservation for the same character. Same pass also fixed two smaller
  findings: the gate no longer logs every dropped packet (would flood the
  console for exactly the slow-gateway duration this exists to tolerate —
  `ClientState` alone arrives at `ClientStateHz`), and restored
  `targetWorldId` to the release-completed log line where it had gone
  missing.
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — this entry; marked the fixed
  finding struck-through in `BACKLOG.md`; added a `## Concurrency` section
  to `ARCH.md` describing the new mechanism; noted the unaudited building
  system and the god-scripts' continued growth (`World3D.cs` 735→1103
  lines, `SurvivalHud.cs` 940→1124 lines) for a future Phase 1.

**Risk:** The core behavior change is timing, not semantics: a client now
waits for `Welcome`/`ReleaseGranted` slightly later relative to when
`Hello`/`RequestRelease` was sent (one background-task hop instead of
inline), and any message it sends before that arrives is now silently
dropped instead of being processed against a half-initialized `Player`. A
well-behaved client (waits for `Welcome` before sending gameplay messages,
per the existing protocol) sees no difference. The `claimingCharacters` fix
closes a race that only this session's own refactor introduced — nothing in
the pre-existing synchronous code had this window, so this is a case of a
fix creating and then closing its own new edge case, not an existing bug.
Smaller risk: `PeerDisconnectedEvent` now has three branches instead of one
before deciding whether to save; a mistake in that branching (e.g.
`Releasing` and `JoinPending` both somehow true) would silently skip a save
that should have happened — traced by hand that the two flags are mutually
exclusive (`Releasing` is only set on an already-fully-joined player, i.e.
`JoinPending` false), but this is exactly the kind of thing a test would
catch and none exists (see `BACKLOG.md`).

**Revert:** `git revert` the two commits on this branch touching
`apps/world-server/Program.cs` and `apps/world-server/Player.cs` (the second
commit is the fast-reconnect-race fix on top of the first; revert both
together or the reservation logic references fields the first commit adds).
No database migration, no protocol/wire change — a code revert alone is
fully reversible.

**Verified:** Read every touched region of both files end-to-end after
editing, twice. Balance-checked braces/parens programmatically after each
edit. Traced every `Task.Run` closure by hand to confirm it never touches
`players`/`writer`/`NetPeer` directly — only gateway calls and
`pendingGatewayCompletions.Enqueue`. Traced the `claimingCharacters` fix by
hand against the specific race scenario the independent review described
(disconnect mid-claim → fast reconnect → stale completion arrives late) and
confirmed the `owner == player` guard prevents it from clearing a newer
reservation. Then had a second agent — fresh context, explicitly told this
session had no compiler and to hunt for races/compile errors/regressions —
read the diff cold; it found one real gap (fixed, above) and two nitpicks
(also fixed), and found no compile errors or unguarded cross-thread
mutation after a careful pass it described in its own words.

**Not verified — a human must check on a real setup:** *Nothing in this
repo was built, run, or tested tonight.* Tried harder than last time to fix
that — `apt-get install dotnet-sdk-10.0` was actually candidate-available
this session (unlike 2026-07-24), but every package fetch 404'd against
both Ubuntu mirrors through the environment's proxy; same failure for
`dotnet-sdk-8.0`. No Docker daemon either. Concretely, a human needs to:
1. `dotnet build` the whole solution — not confirmed to compile.
2. `dotnet test tests/sim-core.tests` — untouched by this change, should
   still pass, but not actually run.
3. Bring up `infra/docker/docker-compose.yml`'s Postgres, run the gateway
   and a world-server, and reproduce the scenario this fix targets: point
   the gateway's `/characters/{id}/claim` at an artificial delay (or just
   watch real latency) and confirm other connected players' movement/
   harvest keeps flowing while one client's `Hello` is still resolving.
4. Reproduce the fast-reconnect race directly: connect, send `Hello`,
   disconnect before `Welcome` arrives, immediately reconnect with the same
   device UUID, and confirm the second `Hello` is accepted (not rejected as
   a duplicate) once the first's abandoned claim is cleaned up.
5. Confirm a legitimate voyage (A → B) still works end-to-end, and that
   `RequestRelease` still denies cleanly when the target world is
   unreachable/unknown.
6. Confirm the client build still launches for the mobile target — client
   code wasn't touched at all tonight, but not personally confirmed.

**Rejected tonight:** Didn't attempt a fresh audit of the building/
blueprint/architect-mode system (landed since the last audit, no
multiplayer-correctness pass yet) — flagged to `BACKLOG.md`/`ARCH.md`
instead of squeezed in alongside tonight's fix, per "ship one thing."
Didn't re-score the god-script findings (`World3D.cs`, `SurvivalHud.cs`,
`WorldConnection.cs`) even though line counts grew substantially — noted the
new counts, left re-scoring for whoever picks the fix up, since scoring
without intent to fix this session felt like busywork. Didn't start on
Phase 2 (new feature) — the decision rule says skip it entirely when a
fix-tonight finding exists, and the carried-over one still did.

**Added to backlog:** Building/blueprint/architect-mode system needs a
dedicated multiplayer-correctness audit pass (unscored — no read yet to
score against). God-script line-count growth noted for re-scoring next time
one is touched. Everything else carried over unchanged from 2026-07-24 (see
`BACKLOG.md`).

**Question for the human:** None blocking. Two things worth your attention:
(1) please run the build/test/manual-race verification listed above before
this reaches real player data, since none of it happened automatically
either of the last two nights; (2) if the dotnet-SDK-install failure keeps
recurring, it might be worth checking whether the sandboxed environment's
network policy can reach the Ubuntu package mirrors these installs need, or
pre-baking an SDK into the session image — two nights in a row losing all
compiler verification to the same infrastructure wall is a real cost.

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
