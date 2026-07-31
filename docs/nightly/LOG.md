# Nightly log

Newest entry first. Each entry is the morning report from that night's
session — see the routine's prompt for the required shape.

---

## 2026-07-31 — AUDIT-FIX

**Chose:** Added a server-side cooldown on harvest strikes (`ChopRequest`), so
one player can be accepted at most once per `HarvestRules.StrikeCooldownSeconds`
(0.35s), no matter how fast the client sends the packet.

**Because:** Audit found `ChopRequest` had no time-based rate limit at all —
only a spatial reach check (`Player.IsWithinReach`). Every request handler in
`world-server/Program.cs` was reviewed; `ClientState` (movement) already
budgets by elapsed real time (`Player.TryAccept` -> `MovementRules.Check`), but
harvesting did not have an equivalent. A client sending `ChopRequest` faster
than a human can tap could fell nodes and collect wood/stone/fiber/berries at
unbounded speed — gains that persist forever via `WorldStore` diffs and the
gateway-saved character inventory. Scored Severity 4 (breaks the core survival
economy's pacing outright, decisive and permanent unfair advantage, trivial to
script) × Blast radius 4 (every player, every world, every persisted
character) = 16, over the ≥15 auto-fix threshold, so Phase 2 (new feature) was
skipped per the routine's decision rule.

**Process error caught mid-session, corrected before merging:** this session's
branch (`claude/relaxed-pascal-2z6j2w`) had been sitting on an ancestor of
`main` roughly a week and 53 files stale — missing the 2026-07-24 ownership
fix below, an entire building/blueprint system, and (most awkwardly)
`docs/nightly/{LOG,BACKLOG,ARCH}.md` itself, which already existed on `main`.
The audit, the fix, and the first draft of these three docs were all done
against that stale snapshot, so the first version of this entry incorrectly
claimed the nightly docs "didn't exist" and needed creating from scratch. Once
the PR came back with `mergeable_state: dirty` and a diff against `main` showed
50+ files of drift, I fetched `origin/main`, rebased this branch onto it
(`apps/world-server/Program.cs` and `Player.cs` auto-merged cleanly — the code
this fix touches was untouched upstream), and hand-merged the three doc files
to preserve every prior entry rather than overwrite them. **Lesson for future
nights, recorded so it isn't repeated:** `git fetch origin main` and diff
against it *before* auditing, not just before pushing — auditing stale code
risks both missing real fixes that already landed and rediscovering
already-fixed bugs as if they were new.

**Changed:**
- `packages/sim-core/HarvestRules.cs` — added `StrikeCooldownSeconds` (const,
  0.35) and `CanStrike(lastStrikeAt, now)`, a pure time-budget check mirroring
  `MovementRules`'s "budget by elapsed time, not request count" pattern so the
  same anti-spam shape lives in the same shared library. Unmodified by any
  other work since this branch's original (stale) base, so no merge conflict.
- `apps/world-server/Player.cs` — added `LastChopAt` and
  `TryConsumeChopCooldown(now)`, following the exact shape of the existing
  `LastAcceptedAt`/`TryAccept` pair for movement. Merged cleanly against
  main's unrelated additions (`Take`, the `TryAccept` obstacles parameter).
- `apps/world-server/Program.cs` — `ChopRequest` now calls
  `player.TryConsumeChopCooldown(Now())` right after the existing reach check
  and before evaluating the harvest; a throttled request is silently dropped
  (not a disconnect — mirrors how out-of-range chops are already handled).
  The `ChopRequest` case itself was untouched by everything that landed on
  `main` since this branch's original base, so this merged cleanly too.
- `tests/sim-core.tests/HarvestRulesTests.cs` — new file. Direct tests for
  `CanStrike` (first-ever strike allowed, too-soon rejected, on-time allowed)
  plus a spam-simulation test mirroring
  `MovementRulesTests.ReportingFaster_DoesNotGrantMoreDistance`: ten requests
  per cooldown window over ten windows yields exactly ten accepted strikes.
  `HarvestRules` had no direct test file before tonight (only indirect
  coverage via `WorldTests.cs`); this is the first one.
- `docs/nightly/{ARCH,BACKLOG}.md` — updated in place (not created — see the
  process-error note above). Added this date's findings to both without
  disturbing the 2026-07-24 entries, refreshed line counts that had grown
  (`World3D.cs` 735→1103, `SurvivalHud.cs` 940→1124) and noted where the
  building/blueprint system now sits in the process map.

**Risk:** Low. The change adds one `double` field and one guard clause on a
single request type; it cannot affect movement, crafting, placement, eating,
survival meters, voyages, building, or persistence format. The only
user-visible effect is that spamming taps/packets faster than ~2.9/s now has
extra taps silently dropped instead of extra strikes landing —
indistinguishable from normal play at any human tap cadence. If 0.35s turns
out to feel laggy for legitimate fast tapping, that's a one-line constant
change in `HarvestRules.cs` (no protocol version bump needed — nothing about
the wire format changed).

**Revert:** `git revert` the commit on `claude/relaxed-pascal-2z6j2w` titled
"Add server-side harvest strike cooldown" — it is the only commit this session
added on top of current `main` after the rebase, so reverting it fully undoes
the code change (the doc updates would need a separate manual revert if you
want those back too, since they're informational, not behavior).

**Verified:**
- Read every line changed and its call sites by hand; confirmed
  `HarvestRules.CanStrike`/`Player.TryConsumeChopCooldown` are called with
  consistent units (both `Now()` in `Program.cs` and `StrikeCooldownSeconds`
  are seconds; matches the existing `LastAcceptedAt`/`MovementRules.Check`
  pattern which uses the same `Now()` clock).
- Confirmed by reading (not running) that `HarvestRulesTests.cs` matches the
  existing `MovementRulesTests.cs` pattern and namespace, and that the math in
  `SpammingRequests_CannotBuyStrikesFasterThanTheCooldown` is correct: 100
  requests at `StrikeCooldownSeconds/10` apart spans exactly 10 cooldown
  windows, so exactly 10 should be accepted.
- Traced the full `ChopRequest` handler in `Program.cs` before and after the
  change (and again after the rebase) to confirm the new check sits after the
  reach check and before any state mutation (`world.TryHarvest`,
  `player.Give`), so a throttled request has zero side effects — no partial
  strike, no dirty inventory flag.
- After rebasing, re-read the full post-merge `ChopRequest` case and the
  `Player.cs` diff against `origin/main` end to end to confirm the merge
  didn't silently drop or duplicate anything from either side.

**Not verified — a human needs to check these on a real setup:**
- **The project was not built or run.** This sandbox has no `dotnet` SDK
  installed, and the one attempt to install one (`dotnet-install.sh` against
  `builds.dotnet.microsoft.com`) was blocked by this session's network egress
  policy (403 from the proxy) — not something to route around. Every prior
  night logged the identical limitation (see 2026-07-24 below); it has not
  been resolved across sessions. `dotnet test tests/sim-core.tests`, `dotnet
  build`, and any Godot editor/mobile-export step were **not run**. The
  change is small and mechanically reviewed, but "compiles and the test suite
  passes" is genuinely unverified.
- No live two-client harvesting session (legitimate or spam) was run against
  the actual server — the fix was validated by reading, not by playing.
- The 0.35s cooldown value is a reasoned guess (roughly a human tap-and-lift
  cadence), not tuned against real play.
- The rest of tonight's audit (bandwidth estimate, mobile frame-time,
  god-script line counts) was done by reading source, not by profiling a
  running client.

**Rejected tonight:** N/A — the audit crossed the mandatory-fix threshold
(score 16 ≥ 15), so Phase 2 (new feature design) was skipped per the routine's
decision rule; no feature ideas were designed or rejected.

**Added to backlog:** Interest management missing for the `PlayerStates`
broadcast (score 12 — and a fix already exists unmerged as PR #14, stacked
under #15/#16), gateway character endpoints have no per-character secret
beyond knowing the UUID (score 9, distinct from the ownership-*claim* fix
below which only prevents concurrent double-loading), `CraftRequest`/
`PlaceRequest` have no rate limit (score 4, low priority — bounded by
inventory, unlike chopping). Refreshed line counts for the already-known
`World3D.cs`/`SurvivalHud.cs` god-script debt. Full detail in `BACKLOG.md`.

**Question for the human:** None blocking. Two things worth attention: (1)
please run `dotnet test tests/sim-core.tests` before merging — nobody has
run it, tonight or on any prior night in this repo's history; (2) PRs #14,
#15, #16 (interest-filtering, save-reconnect-race, gateway-timeout) are open,
unmerged, and stacked on each other as of this session — worth reviewing
those alongside this PR since they touch overlapping territory
(`Program.cs`, `Player.cs`) and a future night will otherwise keep
re-discovering the same gaps they already close.

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
