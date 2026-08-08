# Ashfall — nightly engineer's log

Reverse-chronological. Each entry is one night's session. First entry —
`docs/nightly/{LOG,BACKLOG,ARCH}.md` did not exist before tonight; created
per the nightly-engineer brief, with `ARCH.md` as the primary deliverable
alongside the fix below.

---

## 2026-08-08 — AUDIT (fix applied; Phase 2 skipped per the audit rubric)

**Chose:** Closed a character-ownership hole: a normal (non-voyage)
world-server join never checked or claimed `character.owner_world_id` at
all — only the voyage-transfer path enforced single ownership. A character
could be loaded live by two world-servers simultaneously (or twice by the
same world-server on a reconnect race), each with an independent in-memory
inventory, silently duplicating or losing items whichever session saved
last on disconnect.

**Because:** Audit score — Severity 5 × Blast radius 5 = 25 (item-economy
integrity, silent, no error surfaced to anyone, and mobile network flakiness
makes the reconnect-race variant common, not exotic). It's squarely a
multiplayer-correctness finding ("race conditions on join/leave/reconnect")
well above the ≥9 auto-fix threshold on its own, and above the ≥15
any-category threshold. Per the audit rubric this mandated fixing it tonight
and skipping Phase 2 (new feature) entirely — no feature work was attempted.

**Changed:**
- `apps/gateway/Program.cs` — new `POST /characters/{id}/claim` endpoint.
  Atomically creates-or-claims a character row for a `worldId`: succeeds
  when unowned or already owned by that world, 409s otherwise. Also
  tightened `POST /voyage/claim`'s `UPDATE` to require `owner_world_id IS
  NULL`, closing a secondary race the new endpoint would otherwise open (see
  "Verified" below — this was caught by testing, not by inspection).
- `apps/gateway/API.md` — documented the new endpoint.
- `apps/world-server/GatewayClient.cs` — `ClaimCharacterAsync`, the client
  for the new endpoint.
- `apps/world-server/Program.cs` — the `Hello` handler now calls
  `ClaimCharacterAsync` for every non-voyage join (previously it went
  straight to `GetCharacterAsync` with no ownership check), and rejects the
  join on failure exactly like the existing bad-ticket rejection. Also added
  same-server dedup: if a `Hello` arrives for a `CharacterId` already active
  on *this* server (the gateway claim alone can't catch this, since
  reclaiming your own world trivially succeeds), the stale session is
  saved, removed, and disconnected before the new one loads.
- `docs/voyage-transfer.md` — noted that the single-ownership rule is now
  enforced at every join, not only voyage arrivals.
- `docs/nightly/{ARCH,BACKLOG}.md` — created (see below).

**Risk:** A legitimate reconnect could now be rejected (409 → disconnect) if
the gateway still thinks the *previous* world owns the character — this
self-heals today only via the voyage-ticket expiry path (60s TTL), not for a
plain ownership hold. In practice this only bites if a world-server crashes
or is killed **before** a player's `PeerDisconnectedEvent` fires (which
would normally save-and-leave-ownership-as-is, letting the *same* world
reclaim on reconnect fine) *and* the player then tries a **different**
world without ever voyaging — an edge case, but with no automatic recovery
today. Logged to `BACKLOG.md` item 2 is a related, smaller race (not this
one) found during testing. Watch world-server logs for `"rejecting join
for ... owned by another world"` on a world that should be empty of that
character — that's the signature to spot in practice.

**Revert:** `git revert` the commit on `claude/relaxed-pascal-xip7cn` titled
"world-server/gateway: enforce character ownership on every join" — it's a
single self-contained commit touching only the five files listed above, no
schema/migration change (reuses the `owner_world_id` column added in
`003_voyage.sql`), so revert is a straight one-commit rollback with no
follow-up cleanup needed.

**Verified:**
- `dotnet build` clean (0 warnings, 0 errors) for both `apps/gateway` and
  `apps/world-server` after installing `dotnet-sdk-10.0`/`8.0` via `apt`
  (not preinstalled in this sandbox — see `ARCH.md`).
- `dotnet test tests/sim-core.tests` — 84/84 passing, untouched by this
  change (confirms no accidental regression in the shared sim).
- **End-to-end against a live local Postgres** (Docker wasn't available in
  this sandbox — installed `postgresql` via `apt` instead, applied all four
  migrations by hand, ran the real gateway with `ASHFALL_DB` pointed at it):
  - Fresh character claims world A → 200; same character claims world B
    while still live on A → **409** (the bug, confirmed fixed).
  - Same character reclaims world A again (the ordinary reconnect case) →
    200, still works.
  - Full voyage flow: claim A → save → mint voyage A→B → clean claim by B →
    200; A then tries a plain rejoin → **409** (B owns it live, correctly
    rejected); B reconnecting to itself → 200.
  - While testing the above, an earlier run *without* the `owner_world_id
    IS NULL` guard on `/voyage/claim` exposed the race in BACKLOG item 2
    live (A reclaimed mid-voyage, B's claim then wrongly could have
    succeeded too) — that's what prompted tightening `/voyage/claim`'s
    `WHERE` clause; retested after the fix and confirmed B's claim now
    correctly fails instead of double-claiming.
  - `dotnet run --project apps/world-server` against the same live gateway
    + Postgres: boots clean, connects, listens on udp/9050, no errors.

**Not verified:** No live two-client UDP test of the actual `Hello`
handshake (would need a small LiteNetLib test client mimicking
`WorldConnection.cs`'s wire format — didn't build one tonight, scoped the
verification to the gateway HTTP layer where the actual ownership logic
lives, plus a build+boot smoke test of the world-server). The world-server
side of tonight's change (`ClaimCharacterAsync` call, stale-peer dedup) is
straightforward pass-through logic reviewed by hand and confirmed to
compile and boot, but not exercised by an actual duplicate-connection
scenario end to end. A human should: run two client instances (or two
`WorldConnection` nodes) against the same world-server with the same device
UUID and confirm the first is kicked with a clean disconnect rather than a
silent stall. Also not verified: behavior on a real mobile device/network
(this was all localhost).

**Rejected tonight:** N/A — the audit rubric mandated a fix, not a feature
choice, so there was no Phase 2 idea-selection step to reject from. (For
what it's worth: given the engagement-critical gaps already tracked in
`docs/gameplay-roadmap.md` §1 — no food loop is now closed per that doc,
but tools/progression/creatures remain open — a future feature night should
pull from that doc's Phase A/B list rather than this file, which is for
architecture/audit findings.)

**Added to backlog:** See `docs/nightly/BACKLOG.md` — gateway has zero
automated tests (score 12, now more urgent since this fix added load-bearing
SQL there); a smaller residual reconnect-during-voyage race (score 8,
verified live, does not cause duplication); `PlayerStates`/structure
broadcasts ignore the documented interest-management invariant (score 9,
`Tuning.InterestRadiusChunks` is dead code); client god-scripts
`SurvivalHud.cs`/`World3D.cs` continue growing past their already-flagged
size (score 9); unauthenticated character endpoints, likely an intentional
v1 simplification but tracked explicitly now (score 12).

**Question for the human:** None blocking. One worth a decision when
convenient: should the sandbox image be updated to preinstall
`dotnet-sdk-10.0` and a working Docker daemon? Both had to be installed/
substituted by hand tonight (see `ARCH.md` environment notes), which cost a
meaningful chunk of the session and will repeat every night until it's
fixed at the image level.
