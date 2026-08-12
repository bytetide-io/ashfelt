# Nightly log

## 2026-08-12 — AUDIT (fix applied)

**Chose:** Closed a character-ownership dupe hole: a normal (non-voyage) join
never claimed exclusive ownership of the character, so the same character
UUID could be loaded live on two world-servers at once.

**Because:** Audit score 25 (Severity 5 × Blast radius 5), and independently
a multiplayer-correctness finding ≥ 9 — both trigger the "must fix tonight,
skip Phase 2" rule on their own.

`character.owner_world_id` already existed and was correctly used by the
voyage flow (`POST /voyage` clears it, `POST /voyage/claim` atomically sets
it, with a TTL'd ticket and self-heal). But the *normal* join path — Hello
with an empty ticket, i.e. every first connection and every reconnect that
isn't a voyage — went straight to `GET /characters/{id}` and never touched
ownership at all. `docs/voyage-transfer.md` already documented the invariant
("a character is owned by exactly one world-server at a time") but the code
only enforced it for one of the two ways a character gets loaded.

Concretely exploitable: connect two client instances (or two devices) with
the same device UUID to two world-servers — or the same one twice — with no
voyage ticket. Both load the same starting inventory independently. Anything
persisted straight to *world* state rather than character state (a placed
structure, via `WorldStore.SaveStructureAsync`) survives regardless of which
session's character-save wins the race on disconnect — so a player could
spend the same stack of wood on a wall on world A and a second wall on world
B, netting two permanent structures from one stock of resources. That's an
unrecoverable economy dupe, not just a lost-write race.

**Changed:**
- `infra/migrations/005_ownership_claim.sql` — adds `character.owner_claimed_at`
  (backs the staleness self-heal, same shape as the existing voyage ticket TTL).
- `apps/gateway/Program.cs` — two new endpoints:
  - `POST /characters/{id}/claim` — atomic claim-or-create-with-defaults,
    `409` when already claimed elsewhere and not stale (>300s since last claim
    with no matching release).
  - `POST /characters/{id}/release` — clears ownership on a clean disconnect,
    guarded by world id so a stale release can't clobber a newer claim.
  - `POST /voyage/claim` now also sets `owner_claimed_at` and returns the
    character state directly (previously just `200 OK`), unifying both join
    paths on "claim atomically, get state back in one call."
  - `GET /characters/{id}` kept as a read-only endpoint (doc comment updated
    to say explicitly that it does not gate ownership) — nothing else called
    it once the world-server's join path moved to `/claim`.
- `apps/world-server/GatewayClient.cs` — replaced `GetCharacterAsync` with
  `ClaimCharacterAsync` (normal join) and `ReleaseCharacterAsync` (disconnect);
  `ClaimVoyageAsync` now returns the claimed `CharacterState?` instead of a
  bare `bool`.
- `apps/world-server/Program.cs` — Hello handler: both the voyage-ticket path
  and the normal-join path now go through an atomic claim and reject the
  connection (`peer.Disconnect()`) on failure, instead of only the voyage
  path checking. `PeerDisconnectedEvent`: after the existing fire-and-forget
  character save, also fire-and-forget a release call so the character is
  rejoinable immediately rather than waiting out the 300s staleness window.
- `apps/gateway/API.md`, `docs/voyage-transfer.md` — documented the new
  endpoints and the closed gap.
- `docs/nightly/{LOG,BACKLOG,ARCH}.md` — created (first run of this routine).

No wire-protocol change — this is entirely gateway↔world-server REST, which
the client never talks to directly. `ProtocolVersion` untouched.

**Risk:** The staleness window (300s) is a judgment call: short enough that a
real crash doesn't lock a returning player out for long, long enough that a
transient network blip during a long session doesn't cause a spurious
reclaim from elsewhere. Nothing today refreshes `owner_claimed_at` mid-session
(no heartbeat) — deliberate, to avoid adding a periodic RPC per the audit's
own guidance against per-frame/per-interval chattiness — but it means a
session that's been open longer than 300s and then has its world-server crash
is reclaimable immediately by the next join, which is the intended behavior
(the whole point of the staleness backstop), not a bug. If a legitimate
long-lived session and a genuine crash need to be told apart more precisely
later, that needs an explicit heartbeat, not a shorter timeout.

One behavior distinction worth being explicit about: a *refused* claim
(gateway reachable, ownership genuinely held elsewhere — 409) is a hard
reject in both join paths, as it must be. An *unreachable* gateway is handled
differently per path. For a voyage arrival it's also a hard reject — the
ticket was minted for a real character with real inventory, so silently
starting that connection fresh would be worse than refusing it. For a normal
join it degrades gracefully to an unpersisted, in-memory-only character,
deliberately preserving the original standalone-dev behavior described in
the README (`dotnet run --project apps/world-server` with no gateway running
at all): nothing else could hold a conflicting claim in that case, so there's
nothing to protect against by rejecting. Missing this distinction on the
first pass would have made the standalone dev workflow silently require a
running gateway — caught on review, not on a build (which wasn't possible —
see below).

**Verified:** Read every changed file end-to-end after editing, including
the full resulting control flow of the Hello handler (both branches'
null-checks correctly `break` out of the `switch` before any use of a
possibly-null `character`, so nullable-reference analysis should accept it
even though it couldn't be confirmed by an actual compile — see below).
Traced the SQL by hand: the `INSERT ... ON CONFLICT ... DO UPDATE ... WHERE`
claim query only returns a row (and only inserts/updates) when the guard
condition holds, confirmed against Postgres's documented UPSERT-with-WHERE
semantics. Confirmed the DB defaults inserted on a first-ever claim
(inventory `{}`, all four meters 100) match `SurvivalRules.SurvivalState.Full`
exactly, so a brand-new character behaves identically to before. Grepped for
any other caller of the removed `GatewayClient.GetCharacterAsync` — none.
Grepped for anything that would need a client-side or protocol-version
change — none, confirmed this is gateway-internal. Traced the
voyage-vs-normal-join exception handling by hand to confirm a genuinely
unreachable gateway degrades to unpersisted play on a normal join (preserving
the README's standalone `dotnet run --project apps/world-server` workflow)
while still hard-rejecting a voyage arrival or an actual 409 refusal on
either path — this took two passes to get right (see Risk).

**Not verified:** **No .NET SDK is available in this environment** (`dotnet`
is not installed, and the outbound proxy returns a policy denial for
`builds.dotnet.microsoft.com`, confirmed via `$HTTPS_PROXY/__agentproxy/status`),
so none of this was compiled, and `dotnet test tests/sim-core.tests` was not
run (sim-core is untouched by tonight's change, so it's unlikely to be
affected, but "unlikely" is not "verified"). No live two-client join test
against a running gateway+world-server+Postgres was possible for the same
reason. A human needs to, before trusting this on a device:
1. `dotnet build` the whole solution (gateway + world-server) — the compiler
   changes I'm least certain of without a build are the nullable-flow
   narrowing in the Hello handler and the `$"""..."""` interpolated raw
   string in the claim SQL (C# 11+ raw-string-with-interpolation syntax; not
   used elsewhere in this file, so unprecedented locally).
2. `docker compose -f infra/docker/docker-compose.yml up`, then connect two
   client instances (or two `dotnet run --project apps/world-server` pointed
   at different ports/world ids, sharing one gateway) with the **same**
   device UUID and confirm the second join is rejected with "already active
   on another world" while the first is still connected.
3. Disconnect the first, confirm the second can then join immediately (no
   300s wait) — exercises the release path.
4. Kill a world-server without a clean shutdown (mid-session), confirm the
   character is still locked out for ~300s and then reclaimable — exercises
   the staleness self-heal.
5. A normal single-player join/leave/rejoin cycle, to confirm nothing
   regressed for the common case.

**Rejected tonight:** N/A — the decision rule ("any finding ≥ 15, or any
multiplayer-correctness finding ≥ 9, must be fixed, skip Phase 2 entirely")
triggered on the very first thing audited, so no feature ideas were
evaluated or rejected this session.

**Added to backlog:** See `BACKLOG.md` — no automated world-server/gateway
tests (16), `SurvivalHud.cs` god-script (9), no version field on persisted
character/world state (9), `World3D.cs` doing too much (6), hardcoded world
registry (4, already self-flagged as a stub in its own comment).

**Question for the human:** None blocking — but please prioritize running
the five verification steps above before this reaches any shared/staging
world-server, since a bug in *this specific* code path fails in the
direction of either wrongly locking players out or (worse, if the guard logic
is subtly wrong) not actually closing the dupe hole it exists to close.
