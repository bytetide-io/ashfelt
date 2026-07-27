# Ashfall — nightly log

## 2026-07-27 — AUDIT + FIX

**Note on branch:** the nightly process spec asks for `nightly/{{YYYY-MM-DD}}`,
but this session's actual git instructions (outside the nightly prompt)
assign a fixed designated branch, `claude/relaxed-pascal-yfkm0n`. Followed
the designated-branch instruction since it's the harness-level requirement
for this run, not a nightly-specific one — flagging the mismatch here so it
doesn't read as an unexplained deviation from the process as written.

**Chose:** Fixed character-ownership enforcement on normal join
(`GET /characters/{id}` in `apps/gateway/Program.cs` + the Hello handler in
`apps/world-server/Program.cs`). Audit-only otherwise — no new feature
tonight, per the decision rule.

**Because:** Audit score 25 (Severity 5 × Blast radius 5), and 25 on the
multiplayer-correctness dimension alone — both well past the "must fix
tonight, skip Phase 2" threshold. `character.owner_world_id`
(migration 003) exists specifically so "a character is owned by exactly one
world-server at a time" (`docs/voyage-transfer.md`), and was checked/set by
the voyage-claim flow, but a **normal join** (empty ticket — the common
case) called `GET /characters/{id}` without claiming or checking ownership
at all. Two client processes joining two different world-servers with the
same device UUID (character UUIDs are not secrets, and nothing prevents
someone from copying one) would each load an independent in-memory copy and
each save it back on disconnect — last save wins, silently discarding the
other world's gathering/crafting, with real item-duplication and item-loss
outcomes. This is exactly the bug the ownership column exists to prevent,
just not wired up for the path most joins actually take.

**Changed:**
- `infra/migrations/005_character_ownership_claim.sql` — new
  `character.owner_claimed_at` column.
- `apps/gateway/Program.cs`:
  - `GET /characters/{id}` now requires `?worldId=` and atomically
    get-or-creates-and-claims the row (`INSERT ... ON CONFLICT DO UPDATE ...
    WHERE <ownership guard> RETURNING ...`), returning `409 Conflict` when
    another non-stale world already owns it, `400` when `worldId` is
    missing. The get-or-create half matters: a genuinely brand-new
    character has no row to claim on its very first join, so a plain
    `UPDATE`-based claim (my first pass) still left a first-join race open —
    caught by testing two concurrent first joins, not by inspection.
  - `PUT /characters/{id}` now clears `owner_world_id`/`owner_claimed_at` on
    every save — the two callers (disconnect, and pre-voyage release save)
    are both "this world is done with the character" moments.
  - `POST /voyage/claim` now also stamps `owner_claimed_at`.
  - Added `OwnershipStaleAfterHours` (12h): a claim older than that is
    treated as abandoned, so a world-server that crashes mid-session
    without ever saving doesn't lock the character out permanently — same
    self-heal shape as the existing expired-voyage-ticket handling.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` now takes
  `worldId`, throws a new `CharacterOwnedElsewhereException` on `409`.
- `apps/world-server/Program.cs` — Hello handler passes `worldId` through,
  rejects (disconnects) the join on `CharacterOwnedElsewhereException`. Also
  fixed a bug this change would otherwise have introduced/duplicated: on
  either rejection path (bad voyage ticket, or owned-elsewhere) the `Player`
  already had `CharacterId` set from earlier in the handler, so
  `peer.Disconnect()` would trigger `PeerDisconnectedEvent`'s save-on-leave
  and PUT a **blank** character over the real one. Both rejection paths now
  reset `CharacterId` to `Guid.Empty` before disconnecting, so the
  disconnect handler's existing `if (player.CharacterId != Guid.Empty)`
  guard correctly skips the save. (The bad-ticket path had this latent bug
  already, pre-existing; noted rather than silently fixed elsewhere, since
  it's the same root cause as the code this session was already changing.)
- `apps/gateway/API.md`, `docs/voyage-transfer.md` — updated to document the
  `worldId` requirement, the `409`, and the staleness self-heal.
- `docs/nightly/{ARCH.md,BACKLOG.md}` — created (first night this process
  ran); the rest of the audit's findings are scored and filed there.

**Risk:** The ownership guard is now load-bearing for every join, not just
voyages — a bug in the claim SQL would mean either (a) legitimate
reconnects get wrongly rejected (the `owner_world_id = $2` self-match
clause exists specifically to prevent this — verified with a real
reconnect-to-same-world test), or (b) the guard silently no-ops and the
original bug returns. Watch for: players reporting "can't rejoin" (check
`character.owner_world_id`/`owner_claimed_at` for that UUID — if it's stuck
non-NULL for a world that no longer holds them, and less than 12h old,
that's the staleness window working as designed, not a bug); inventory
appearing to roll back after a session (would mean the ownership guard
failed open, not closed). Revert is a straight `git revert` — nothing else
depends on `owner_claimed_at` existing yet.

**Revert:** `git revert <this commit>` (single commit). The migration is
additive-only (`ADD COLUMN IF NOT EXISTS`) — no down-migration needed to
revert the code; the column would just go unused.

**Verified:**
- `dotnet build` clean (0 warnings, 0 errors) on `sim-core`, `shared-proto`,
  `world-server`, `gateway`.
- `dotnet test tests/sim-core.tests`: 84/84 passing, before and after
  (untouched by this change, confirmed unaffected rather than assumed).
- Installed `dotnet-sdk-8.0`/`dotnet-sdk-10.0` via apt and started a local
  `postgresql-16` (this sandbox ships neither a working Docker daemon nor a
  .NET SDK by default — see `ARCH.md`) and ran the actual migrations
  (001–005) against a real database, then exercised the gateway's HTTP
  contract directly with `curl`: missing `worldId` → 400; fresh character →
  404 pre-claim behavior replaced by get-or-create (verified below);
  world A claims → 200; world B claims while A holds it → **409** (the
  exploit, blocked); world A reconnects → 200 (self-match); world A's PUT
  releases → world B can then claim → 200; a claim backdated 13h → world B
  can claim despite A never releasing (staleness self-heal). Fired 10
  concurrent claims from A and 10 from B at a never-claimed character —
  exactly one world's claims succeeded (all 10), the other's all got 409 —
  confirming Postgres row-locking actually serializes the race, not just
  happens to work in the sequential case.
- Ran **both world-server processes concurrently** (ports 9050/9051,
  `continent-a`/`continent-b`) against the fixed gateway and used a
  throwaway LiteNetLib client harness to send simultaneous `Hello` messages
  with the same brand-new character UUID to both at once — confirmed at the
  wire-protocol level (not just HTTP) that exactly one world admits the
  player (`Welcome`) and the other disconnects it, with the server log
  showing the exact rejection reason
  (`character already owned by 'continent-a'`). Ran this three times with
  fresh UUIDs each time; passed every time (winner varies, which is
  expected — the point is exactly one wins, not which one).
- This is what caught the first-join race: my first implementation used a
  plain `UPDATE` for the claim, which only works if a row already exists.
  The concurrent-Hello test showed both A and B being admitted for a
  never-before-saved character, which traced back to `GET` 404ing for both
  before either had a chance to PUT — fixed by making the claim an
  atomic get-or-create upsert instead.

**Not verified:**
- The Godot client itself — this sandbox has no Godot install, so
  `apps/client` could not be opened, built, or run. This change makes no
  client-side edits, so the client's compiled behavior is unaffected, but
  that claim rests on not having touched those files, not on having run the
  client to confirm.
- Real network conditions (packet loss, real RTT, mobile radio behavior) —
  everything above ran over loopback.
- The full voyage transfer flow end-to-end (mint → claim → arrival) with a
  real client — exercised the gateway's `/voyage` and `/voyage/claim`
  endpoints' SQL logic by reading it carefully against the new
  `owner_claimed_at` column, but did not run an actual voyage through two
  live world-servers the way the double-join scenario was tested. The voyage
  code paths I touched are additive (one extra column write) and covered by
  the same claim logic already proven above, so risk is low, but "low risk"
  is not "tested."
- Long-running/soak behavior — no test ran longer than a few minutes, so
  nothing here speaks to the 12-hour staleness window under real usage
  patterns (e.g., a legitimately very long play session colliding with the
  staleness boundary was reasoned about, not observed).

**Rejected tonight:** N/A — the audit score forced Phase 1 (fix, don't
build a feature); Phase 2 wasn't reached, so no feature ideas were
generated or rejected this session.

**Added to backlog:** All non-fixed audit findings, scored, in
`docs/nightly/BACKLOG.md`: no interest management (16), no rate limit on
gather/craft/place (12, MP-flagged), Hello blocking the tick loop on a
synchronous gateway call (12, MP-flagged), `World3D.cs` god-script (12),
missing world-server/gateway test coverage (12), silent fire-and-forget
persistence-write failures (9), `SurvivalHud.cs` god-script (6), a
per-physics-frame gather-prompt tile scan (2).

**Question for the human:** None blocking. One worth a look when convenient,
not urgent: this sandbox has no .NET SDK, no running Docker daemon, and no
Godot install by default, which meant a meaningful chunk of tonight went to
standing up enough of a toolchain (apt-installed SDKs + a local Postgres) to
verify the fix at all rather than just trusting the diff. If nightly runs
are meant to happen regularly, worth deciding whether the environment should
ship these preinstalled — otherwise every night either re-pays that setup
cost or (worse) skips verification it shouldn't.
