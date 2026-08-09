# Nightly log

## 2026-08-09 — AUDIT-ONLY (fix, not a feature — audit found a must-fix)

**⚠️ Could not build or run the project tonight.** No `.NET SDK` is installed
in this session's sandbox (`dotnet` not found, and the sandbox's network
policy blocks `dot.net` so I could not install one), so `dotnet test
tests/sim-core.tests` — required before any commit per the project's working
agreement — was **not run**, and none of the changed C# was compiled. I
verified the one piece of new logic that actually matters (the SQL in
`apps/gateway/Program.cs`) by hand-building a local Postgres 16 cluster from
the distro package (no Docker daemon available either — `docker` the CLI is
present, the daemon socket is not) and running the *exact* SQL text extracted
from the file against the real `infra/migrations` schema for 8 scenarios (see
Verified, below). Everything else here is careful manual reading, not a
compiler or test runner. A human needs to `dotnet build` the whole solution
and `dotnet test tests/sim-core.tests` before trusting this branch.

Also: this session's branch was pre-assigned by the harness as
`claude/relaxed-pascal-uwcg5y`, not `nightly/2026-08-09` as the standing
nightly instructions specify. I kept the assigned branch rather than creating
a second one, since the harness explicitly said never to push elsewhere
without permission. Flagging the deviation rather than silently picking one.

**Chose:** Fixed a character-ownership race between world-servers: a plain
join (`Hello` with no voyage ticket) never checked or claimed
`character.owner_world_id` in the gateway, so the same character could be
loaded live into *two* world-servers at once — either two ordinary worlds
racing a join for the same device UUID, or (worse, and the case that actually
proves this is real) **a bare Hello landing on any world while the character
is mid-voyage**, snatching it away from the voyage's actual target before
`/voyage/claim` runs. Either way, both worlds independently mutate an
in-memory inventory and both save it back to the gateway on disconnect —
last-write-wins duplication or loss of items, gold-standard netcode bug.

**Because:** Audit score. Multiplayer-correctness, severity 5 (character/item
duplication — this is the game's persistent economy) × blast radius 5 (every
voyage, and every double-join race, forever until fixed) = 25, and it's a
multiplayer-correctness finding well over the "any ≥ 9 → must fix, skip
Phase 2" threshold on its own. Per the decision rule, Phase 2 (new feature)
was skipped entirely.

**Changed:**
- `apps/gateway/Program.cs` — `GET /characters/{id}` now takes a required
  `worldId` query param and does an atomic claim-or-create (`INSERT ... ON
  CONFLICT DO UPDATE ... WHERE owner unclaimed-or-ours AND no live voyage
  ticket exists ... RETURNING`) instead of a blind `SELECT`. Returns `409
  Conflict` instead of `404` when another world owns it or a voyage is in
  flight; `404` for "new character" is gone because the row is now created on
  first claim.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` takes `worldId`,
  now returns `null` to mean "claim rejected, reject the join" instead of
  "not found, start fresh" (a fresh character is now just a normal 200).
- `apps/world-server/Program.cs` — `Hello` handler rejects the connection
  (`peer.Disconnect()`) when the claim comes back `null`, instead of silently
  proceeding with an unowned character.
- `apps/world-server/Player.cs` — new `CharacterLoaded` flag, set only when
  `LoadCharacter` actually runs (i.e. the gateway confirmed this world owns
  the character).
- `apps/world-server/Program.cs`, disconnect handler and `RequestRelease` —
  both gated on `CharacterLoaded` now. **This closes a second bug that would
  otherwise have made the first fix actively worse**: a rejected join (bad
  voyage ticket, or the new ownership conflict) still has `CharacterId != 
  Guid.Empty` parsed off the wire, and the old disconnect handler saved
  *any* connection with a non-empty `CharacterId` — meaning a rejected,
  never-loaded connection would still fire a save of the `Player`'s blank
  default state (empty inventory, full meters) over the gateway's real data
  on disconnect. Without this guard, the ownership fix would have turned "two
  worlds might race for a character" into "any rejected duplicate-join
  attempt reliably blanks the real owner's inventory the moment it
  disconnects" — strictly worse. Same guard also closes the pre-existing
  version of this in the bad-voyage-ticket rejection path, which had the
  identical gap before tonight.
- `apps/gateway/API.md` — docs for the changed endpoint (`worldId` param,
  `409` response, updated example including `warmth`, which the doc had
  silently dropped since that meter was added).

**Risk:** This touches the join path for every player, every time. If the SQL
claim logic has an edge case I didn't test, the failure mode is either (a) a
legitimate reconnect gets wrongly rejected (`peer.Disconnect()` — annoying,
loud, easy to spot in server logs as `rejecting player N: character ... is
owned by another world or mid-voyage`), or (b) — much worse, and what I spent
the verification time on — a race still slips through and two worlds claim
the same character. How to spot (b): two `[world] player N character ...
claimed` log lines for the same character UUID from two different
`worldId`s with no `RequestRelease`/voyage log in between. Also watch
`owner_world_id` in the `character` table for a value that doesn't match
whichever world last logged a load for that id.

**Verified:** Hand-built a local Postgres 16 cluster (distro package, no
Docker daemon in this sandbox — ran `initdb`/`pg_ctl` directly as the
`postgres` system user), applied `infra/migrations/*.sql` verbatim, then
extracted the literal SQL string from `apps/gateway/Program.cs` with a small
script (so the tested query is provably the shipped query, not a
hand-transcribed copy) and ran it through 8 scenarios:
1. brand-new character joins world A → claims, returns default state.
2. same character reconnects to world A → idempotent, succeeds.
3. a different world B tries a bare join with no voyage → **correctly
   rejected** (0 rows), `owner_world_id` untouched.
4–5. voyage started (owner → NULL, ticket minted for continent-b); a bare
   Hello races in on world A, and separately on an unrelated world C, while
   the voyage is in flight → **both correctly rejected** (0 rows each) — this
   is the scenario the first draft of the query got wrong (see below).
6–7. the real `/voyage/claim` lands on world B (ticket consumed, owner set);
   world B's own subsequent join-claim (as Hello does after `ClaimVoyageAsync`)
   → idempotent, succeeds.
8. a second voyage is started and allowed to expire unclaimed; manually ran
   the same `ReclaimExpiredTicketAsync` SQL the gateway uses → ownership
   correctly self-heals back to the world the character left, and a plain
   join to that world then succeeds.

Also worth recording honestly: **the first version of the claim query passed
scenarios 1–3 and 6–7 but failed 4–5** — it treated `owner_world_id IS NULL`
as simply "unclaimed," which is also the state of a character mid-voyage, so
a bare Hello could steal a character out from under an in-flight voyage. The
Postgres test caught this before it reached the C#; the shipped query adds
`AND NOT EXISTS (SELECT 1 FROM voyage_ticket WHERE voyage_ticket.character_id
= character.id)` to close it, and re-running the full 8-scenario matrix
against the corrected query passed. I'm recording this because a
"verified with a database" claim is only worth as much as what was actually
tested, and the first attempt at this exact fix was itself wrong.

**Not verified:** No `dotnet build`/`dotnet test` ran at all (see the warning
at the top — no SDK in this sandbox). Specifically not checked by me:
compilation of any of the five changed files (nullable-reference-type
correctness, ASP.NET Core minimal-API query-parameter binding for the new
`string worldId` parameter, `Uri.EscapeDataString` usage); the actual
end-to-end UDP/HTTP round trip (world-server ↔ gateway ↔ Postgres) — the SQL
was proven correct in isolation, but I could not run `apps/world-server` or
`apps/gateway` themselves since that also needs the SDK; the Godot client
build or launch, mobile or desktop, at all; whether ASP.NET Core's minimal-API
model binder actually rejects a request missing the required `worldId` query
param the way I assumed (should 400, did not confirm against a running
instance). **A human should run `dotnet build`, `dotnet test
tests/sim-core.tests`, bring up `docker compose -f infra/docker/docker-compose.yml
up`, and manually exercise: normal join, disconnect/reconnect to the same
world, and (if a second world instance is easy to stand up) an actual
cross-world voyage — before merging.**

**Rejected tonight:** Phase 2 (new feature) was not reached — the audit
found a must-fix multiplayer-correctness bug and the decision rule says skip
straight to fixing it. No feature ideas were designed or rejected.

**Added to backlog:** See `BACKLOG.md` — same-world duplicate-`Player` race on
fast reconnect (Sev 3×3=9), zero test coverage on world-server/gateway
(Sev 3×4=12), no migration runner (Sev 2×3=6), hardcoded world registry
(Sev 2×2=4, already self-flagged in code), two unscored line-count-only
god-script candidates on the client (`SurvivalHud.cs`, `World3D.cs` — not
content-reviewed), and the pre-existing "gateway unreachable at join lets a
player continue with a blank slate for the session" degraded-mode gap
(Sev 3×3=9, left as-is to keep tonight's diff to one change).

**Question for the human:** None blocking — the fix is self-contained and
reversible (see below), and the SQL is verified independently of the
unavailable compiler. But please run the build/test suite before merging;
I could not.

**Revert:** `git revert` the commit(s) on this branch, or `git diff
feature/phase2-3-survival-voyage...claude/relaxed-pascal-uwcg5y --
apps/gateway/Program.cs apps/gateway/API.md apps/world-server/GatewayClient.cs
apps/world-server/Player.cs apps/world-server/Program.cs | git apply -R` to
undo just this change (`feature/phase2-3-survival-voyage` is the repo's
default branch — there is no `main`). No schema migration was added or
changed — the fix uses the existing `owner_world_id` and `voyage_ticket`
columns from `003_voyage.sql`, so reverting the code fully reverts the
behavior with no data cleanup needed.
