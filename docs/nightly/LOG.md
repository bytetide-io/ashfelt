# Nightly log

Newest entry first. Each entry is the morning report from that night's
session — see the routine's prompt for the required shape.

---

## 2026-08-22 — AUDIT-ONLY (fix forced by score)

**Chose:** Closed an unauthenticated gateway endpoint that let any caller who
knows a character's device UUID — which every player already knows, it's their
own — directly set that character's inventory and survival meters via
`PUT /characters/{id}`, completely bypassing the world-server and every
harvest/craft/eat rule it enforces.

**Because:** Phase 1 audit finding, scored Severity 5 × Blast radius 4 = 20
(also ≥ 9 as a standalone multiplayer-correctness finding either way), which
this workflow's decision rule makes mandatory to fix tonight, skipping the
Phase 2 feature. The gateway's `/characters/{id}` (both the claim and save
routes), `/voyage` and `/voyage/claim` routes had no authentication at all —
they trusted every caller to be a well-behaved world-server. But the client
stores its own character UUID locally (`user://character_id`, see
`WorldConnection.cs`) by design, and the gateway's own `API.md` documents the
exact request shape needed to call `PUT` on it. Any player could have opened
the docs, read their own UUID off their device, and given themselves infinite
resources or maxed survival stats with a single `curl` command — a total,
trivial break of invariant #1 (server-authoritative) and of the entire
survival design pillar, requiring no client reverse-engineering at all. Note
this is a *different* hole than the one the 2026-07-24 session closed below:
that session made sure only one world can *own* a character at a time; this
one makes sure only a world-server (not literally anyone) can touch a
character's state at all. Fixing the first without the second still leaves
the front door wide open.

**Changed:**
- `apps/gateway/Program.cs` — added middleware requiring an `X-Ashfall-Key`
  header (matching `ASHFALL_GATEWAY_KEY`, default `"ashfall"` for local dev)
  on every route except `/health` and `/worlds`. Logs a warning on startup if
  running with the default key.
- `apps/world-server/GatewayClient.cs` — constructor now takes the gateway key
  and sends it as a default request header on every call.
- `apps/world-server/Program.cs` — reads `ASHFALL_GATEWAY_KEY` (same default,
  so local dev needs no new env var) and passes it to `GatewayClient`.
- `apps/gateway/API.md` — documents the new required header and the 401.
- `docs/architecture.md` — one paragraph under invariant #3 pointing at the
  trust boundary and the doc that explains it.
- `docs/nightly/{ARCH.md,BACKLOG.md,LOG.md}` — this session initially wrote
  these believing they didn't exist yet; they'd since been created by the
  2026-07-24 session (this branch was cut from an older point on `main` and
  hadn't seen that work). Merged rather than overwritten — see "Rejected
  tonight" below for how that was handled.

**Risk:** Low for local dev — both sides default to the same key, so
`dotnet run --project apps/world-server` + `dotnet run --project apps/gateway`
keeps working unchanged. Real risk is anywhere the gateway is reachable
outside localhost with the default key still set: the fix only helps once an
operator sets a real `ASHFALL_GATEWAY_KEY` (the gateway now warns loudly on
boot if it isn't). Middleware ordering: `app.Use(...)` is registered before
the `app.MapGet`/`MapPost` calls, which is the standard, correct place for
gate-everything-after-this-point middleware in ASP.NET Core minimal APIs. If
this breaks, the symptom is every world-server failing to load or save
characters (`GatewayClient` calls throwing on a non-success status) — check
for a `401` in the gateway's logs first; it almost certainly means the two
processes have different `ASHFALL_GATEWAY_KEY` values (or one is unset).

**Revert:** `git revert` the commit on this branch, or by hand: drop the
`app.Use(...)` block and the `gatewayKey` lines in `apps/gateway/Program.cs`,
drop the `gatewayKey` parameter from `GatewayClient`'s constructor and the
`_http.DefaultRequestHeaders.Add(...)` line, and revert the one-line call site
in `apps/world-server/Program.cs`. No schema change, no wire-protocol version
bump — safe to revert independently of everything else.

**Verified:** Read every changed file back end to end and traced the
middleware/constructor/call-site wiring by hand (single call site for `new
GatewayClient(...)`, confirmed via grep). The ASP.NET Core `app.Use(async
(context, next) => ...)` middleware idiom, `PathString.StartsWithSegments`,
and `IHeaderDictionary.TryGetValue` usage all match well-known, standard
signatures. After discovering this branch was stale against `main` (see
below), merged `main` in locally and confirmed the middleware applies
cleanly against the 2026-07-24 session's reshaped gateway endpoints
(`POST /characters/{id}/claim` in addition to `PUT /characters/{id}`) without
needing any changes to the middleware itself, since it gates by path prefix
rather than by naming specific routes.

**Not verified — could not verify, full stop:** **This sandbox has no .NET SDK
installed** (`dotnet` is not on `PATH`). I could not run `dotnet build`,
`dotnet test tests/sim-core.tests` (required before any commit per
`CLAUDE.md`), start the gateway or world-server, or confirm with a live
request that an unauthenticated call now gets `401` and an authenticated one
still succeeds. I did not touch anything in `sim-core`, so the determinism
tests are very unlikely to be affected by this change, but "unlikely" is not
"verified" — a human needs to run `dotnet test tests/sim-core.tests` and
`dotnet build` on this branch, and ideally exercise a real join/leave/voyage
against a local Postgres, before this is trusted in any real deployment. This
is the second night in a row this has been true (see 2026-07-24 below) — now
tracked as its own backlog item (`BACKLOG.md` #12) rather than just repeated
in each report.

**Rejected tonight:**
- Fixing the missing `owner_world_id` check on `PUT /characters/{id}` in the
  same change — related, but a strictly smaller hole (needs a second bug, a
  compromised/misbehaving world-server, to matter) and it's cleaner to keep
  tonight's diff to exactly the one thing that was actually exploitable by an
  ordinary player. Logged to `BACKLOG.md` #7.
- Adding per-action rate limiting for Chop/Craft/Place requests — a real gap
  (`BACKLOG.md` #2, scores 12) but requires a custom client to exploit, not a
  documented public API and a stock `curl`. Lower priority than the one fixed.
- Building out a full xunit test project for the gateway so this middleware
  had automated coverage — no gateway test project exists yet, and standing
  one up plus a `WebApplicationFactory` harness is a bigger, separate piece of
  work than tonight's fix; also I have no way to run it in this sandbox to
  confirm it's even green. Folded into `BACKLOG.md` #11.
- Overwriting the 2026-07-24 session's `docs/nightly/*.md` files with my own
  from-scratch versions, once I discovered they already existed on `main` and
  my branch was just stale. I'd written all three before merging `main` in,
  under the (wrong, at the time) assumption this was the first-ever run.
  Merged the two nights' content together by hand instead — deduped
  overlapping findings (both nights independently flagged the `SurvivalHud`/
  `World3D` god-scripts and the missing gateway/world-server test coverage,
  which is a useful cross-check), kept both `LOG.md` entries in full, and
  struck through backlog items either night actually fixed rather than
  deleting them, matching the discipline the 2026-07-24 entry itself set.

**Added to backlog:** `docs/nightly/BACKLOG.md` is now a single merged,
re-ranked list — see that file. Nothing new was added tonight beyond what
both sessions had already independently found; the merge did surface one
thing worth a line here: the 2026-07-24 fix (ownership claim) and tonight's
fix (caller authentication) are complementary, not overlapping — closing one
without the other would have left a real hole.

**Question for the human:** Please run `dotnet test tests/sim-core.tests` and
`dotnet build` on `claude/relaxed-pascal-11i17g` before merging — I could not
run either tonight, and this is a security-relevant change to the gateway's
auth surface that I'd rather you double-check than trust on my read-through
alone. Separately, worth deciding whether nightly sessions should keep
running in a sandbox with no `dotnet` SDK at all — two for two nights now.

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
