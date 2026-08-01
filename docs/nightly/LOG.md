# Nightly log

Newest entry first. Each entry is the morning report from that night's
session — see the routine's prompt for the required shape.

---

## 2026-08-01 — AUDIT + FIX

**Chose:** Added shared-secret authentication (`X-Ashfall-Key`, env
`ASHFALL_GATEWAY_KEY`, fixed-time compared) to the gateway's `/characters/*`
and `/voyage*` endpoints — including the `POST /characters/{id}/claim`
endpoint the 2026-07-24 session introduced. `/health` and `/worlds` stay
open; the client's travel menu reads `/worlds` directly.

**Because:** this session's own audit (before discovering the 2026-07-24
history — see below) independently found that the gateway's character and
voyage endpoints had **no authentication at all**: anyone who could reach
the gateway's HTTP port could call `POST /characters/{id}/claim`,
`PUT /characters/{id}`, `POST /voyage`, or `POST /voyage/claim` for *any*
character UUID, with nothing checking the caller was a trusted world-server.
Scored Severity 5 × Blast radius 4 = 20, above the fix-tonight threshold.
This is a distinct hole from the one the 2026-07-24 session closed: that fix
made ownership *exclusive* (only one world-server can hold a character at a
time) but never checked *who* was allowed to call the ownership-claiming
endpoint in the first place — an unauthenticated attacker could still claim,
read, or overwrite any character directly, ticket or no ticket. Confirmed
this was still open by reading current `main`'s `apps/gateway/Program.cs`
before writing the fix, so the finding is current, not stale.

This session started from an older base of the repo (before the blueprint-
building system and the 2026-07-24 fix existed on this branch) and only
discovered the existing `docs/nightly/` history — including that a nightly
session had already run and this exact area of code had already changed
underneath it — while merging `main` into its branch after opening its PR.
Per "never repeat or undo a previous night's work without explicit
justification": this session's fix does not undo the 2026-07-24 fix, it
composes with it (both are now in effect — ownership is exclusive *and* only
trusted world-servers can claim/save/voyage at all), so no conflict to
justify.

**Changed:**
- `apps/gateway/Program.cs` — auth middleware gating `/characters*` and
  `/voyage*` (covers the claim endpoint too) on a fixed-time-compared shared
  secret; `/health`/`/worlds` unauthenticated by design.
- `apps/world-server/GatewayClient.cs` — constructor now takes the key and
  sends it as `X-Ashfall-Key` on every request (including `ClaimCharacterAsync`).
- `apps/world-server/Program.cs` — reads `ASHFALL_GATEWAY_KEY` (default
  `ashfall`, same convention as the existing `ASHFALL_CONNECT_KEY`) and
  passes it to `GatewayClient`.
- `apps/gateway/API.md` — documents the new required header.
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — this entry, and a new
  "2026-08-01 audit" section in `BACKLOG.md`. `ARCH.md`'s existing content
  (from 2026-07-24, which is more current than this session's own
  independently-drafted map) was kept rather than overwritten.

**Risk:** Both sides default the key to `"ashfall"` for local dev, matching
the existing `ASHFALL_CONNECT_KEY` pattern — a fresh `docker compose up` /
`dotnet run` setup keeps working unmodified. Anyone running the gateway and
world-server in a shared or production environment **must** set
`ASHFALL_GATEWAY_KEY` to a real secret on both processes (matching values)
or the default is a known, public secret and provides no protection. If the
two processes' keys ever drift, every character load/save and every voyage
starts returning 401 — loud and immediate, not silent corruption, but it
will look like an outage; watch world-server logs for
`EnsureSuccessStatusCode` exceptions from `GatewayClient`.

**Verified:** Read every touched file back after editing and traced control
flow by hand: the middleware runs before route mapping and checks
`Path.StartsWithSegments`, matching this file's existing minimal-API style;
grepped the whole repo for other `GatewayClient` constructions or direct
callers of `/characters`/`/voyage*` and found only the one call site, now
updated. After merging `main`, re-read the auto-merged
`apps/gateway/Program.cs`, `GatewayClient.cs`, and `apps/world-server/Program.cs`
in full to confirm the 3-way merge composed correctly with the
`POST /characters/{id}/claim` endpoint and the same-process duplicate-connection
guard the 2026-07-24 session added — it does; the auth middleware wraps the
claim endpoint (same `/characters` path prefix) without needing any change
to that logic.

**Not verified:** **Could not build or run anything** — no `dotnet` SDK in
this session's sandbox (same limitation the 2026-07-24 session hit). Could
not run `dotnet build`, `dotnet test tests/sim-core.tests`, start the
gateway/world-server for an end-to-end `Hello` → claim round trip, or open
the Godot client. This change touches only server-to-server C# request
plumbing — sim-core, client scripts, and the wire protocol are untouched —
so no determinism test should be affected, but a human or CI must confirm
`dotnet build apps/gateway` and `dotnet build apps/world-server` actually
compile. **CI on the PR is the first real verification this gets** —
watching it and will fix anything it flags.

**Rejected tonight:**
- Requiring `ASHFALL_GATEWAY_KEY` with no default (fail loudly like
  `ASHFALL_DB`) — would break the README's local dev flow for a security
  property already no worse than the existing `ASHFALL_CONNECT_KEY` default;
  consistency with that convention won.
- Protecting `/worlds` behind the same key — the client calls `GET /worlds`
  directly for the travel menu, so it can't hold the server-to-server
  secret.
- Rewriting `ARCH.md`'s process map from scratch against this session's own
  (older, now-superseded) reading of the codebase — the existing entry from
  2026-07-24 is more current and accurate than a fresh one drafted before
  this session discovered the blueprint-building work and the prior fix
  already on `main`; overwriting it would have been a regression in the
  document's own accuracy, not an improvement.

**Added to backlog:** New section in `BACKLOG.md` for tonight's audit —
interest management (invariant #6) declared in `Tuning` but never actually
implemented (broadcasts go to every client regardless of distance); several
fire-and-forget world-server DB writes (tile diffs, structures) swallow
failures silently, unlike the character-save path right next to them which
logs on failure; `PlacementRules.Blocks` bypasses the `ItemCatalog`
single-source-of-truth pattern used everywhere else in that file; the
`RequestChunk`/`ChunkData` protocol path is dead code the client never
exercises; `World.HasWarmthNear` is an unindexed per-player, per-tick linear
scan over every structure. The blocking-gateway-calls finding (20) and the
`SurvivalHud`/`World3D` god-script findings (9 each) already in `BACKLOG.md`
from 2026-07-24 were independently re-derived by this session's own audit —
left as-is rather than duplicated with a second score.

**Question for the human:** None blocking — but please confirm CI is green
on the PR before merging, since this session couldn't build locally, and
please double check nothing about tonight's branch-start being stale
(working from an older base than current `main`, discovered only mid-session)
caused anything to be missed beyond what's noted above.

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
