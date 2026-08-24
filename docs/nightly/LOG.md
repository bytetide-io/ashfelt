# Ashfall — nightly log

One entry per overnight session, newest first.

---

## 2026-08-24 — AUDIT-ONLY (fixed the one thing the audit's decision rule required)

**Chose:** Closed a character-duplication hole in the voyage/ownership
handshake: a normal (non-voyage) join to a world-server no longer silently
loads a character another world currently owns.

**Because:** `docs/voyage-transfer.md` states the invariant "a character is
owned by exactly one world-server at a time," enforced by
`character.owner_world_id`. In the actual code, `owner_world_id` was only
ever *read or set* by the voyage-ticket path
(`POST /voyage`, `POST /voyage/claim`). The plain join path — `Hello` with an
empty ticket, the default and by far the most common case — called
`GET /characters/{id}` in `apps/gateway/Program.cs`, which returned the
stored inventory and meters with **no ownership check whatsoever**. Any
client (or just a stray reconnect race, or a player who edits
`WorldConnection.Host`/`Port` to point at `continent-b` directly instead of
voyaging) could join a second world-server with the same device-UUID
character while it was still active on the first. Both worlds would then
run independent, diverging in-memory inventories and each would save
(`PUT /characters/{id}`) on disconnect — last write wins, with a full window
for item duplication or loss in between. Severity 5 (breaks the core
crafting/survival economy's integrity, and defeats the entire reason the
voyage-ticket system exists) × blast radius 5 (every character, on every
world, any time a client reconnects without going through a voyage) = 25,
comfortably over the "fix tonight, skip Phase 2" threshold, and squarely a
multiplayer-correctness finding on its own. Full scored list of everything
else the audit turned up is in `docs/nightly/BACKLOG.md`.

**Changed:**
- `apps/gateway/Program.cs` — replaced `GET /characters/{id}` with
  `POST /characters/{id}/claim` (body: `{ worldId }`). One atomic SQL
  statement (`INSERT ... ON CONFLICT (id) DO UPDATE ... WHERE
  owner_world_id IS NULL OR owner_world_id = $worldId RETURNING ...`) either
  claims-and-returns the character (including a brand-new one, created with
  defaults on first claim) or returns 0 rows — mapped to `409 Conflict` —
  when another world currently owns it. Kept the existing
  `ReclaimExpiredTicketAsync` stranded-voyage self-heal, now run before the
  claim is evaluated instead of before a plain read.
- `apps/gateway/API.md` — documented the new endpoint in place of the old
  `GET`.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` renamed
  `ClaimCharacterAsync(Guid id, string worldId)`; `null` now specifically
  means "owned elsewhere, refused" rather than "never saved before."
- `apps/world-server/Program.cs` — the `Hello` handler now calls
  `ClaimCharacterAsync(player.CharacterId, worldId)` unconditionally (after
  an optional voyage-ticket claim, which already sets ownership, so this is
  a no-op confirmation in that case). A `null` result disconnects the peer
  before `Welcome` is ever sent, the same pattern already used for a
  protocol-version mismatch or a bad voyage ticket. A gateway-unreachable
  *exception* still falls back to letting the player join unloaded, matching
  the prior behaviour — only the "another world owns this" case is new and
  fails closed.

No wire-protocol change (`ProtocolVersion.Current` untouched) — this is
gateway↔world-server REST only, invisible to the client. No schema change —
`character.owner_world_id` already existed (migration `003_voyage.sql`), so
no new migration and no save-compatibility concern.

**Risk:** If the atomic claim query has a typo or the `WHERE` clause logic is
inverted, the failure mode is either (a) every join gets falsely rejected as
"owned by another world" — obvious immediately, every player disconnects at
Hello — or (b) the check becomes a no-op and the original bug persists
silently. Watch world-server logs for `"rejecting join for ... owned by
another world"` appearing for *normal* single-world play (would indicate (a));
absence of any such log ever, even when deliberately pointing a second client
at `continent-b` with the same `user://character_id`, would indicate (b).

**Revert:** `git revert <this commit's hash>` — the change is confined to
the four files above and reverts cleanly (no migration to roll back).

**Verified:** Read every call site of the changed methods
(`grep -rn GetCharacterAsync` after the rename returns nothing left behind).
Manually traced: fresh character join, same-world reconnect, voyage-claim
then Hello, and the two-world-without-a-voyage case, against the new SQL —
all four resolve the way the design intends. Confirmed the JSON casing
matches the existing convention in this file (other endpoints already bind
records like `VoyageClaim(Guid CharacterId, Guid Ticket, string WorldId)`
from camelCase anonymous-object bodies the same way).

**Not verified:** **This session's container has no `dotnet` SDK on `PATH`
and none could be found on disk, so `dotnet build`, `dotnet run`, and
`dotnet test tests/sim-core.tests` could not be executed at all.** The
change was reviewed by hand for type and signature correctness, not
compiled. A human must run a real build (and ideally the two-world manual
repro above against `docker compose -f infra/docker/docker-compose.yml up`)
before trusting this on a device. `sim-core` itself is untouched, so the
determinism test contract is not at risk, but that is inference, not a test
run.

**Rejected tonight:** Did not also touch the "death has no consequence" gap
(`SurvivalRules.IsDead` unread anywhere) even though it also scores ≥15 on
its own read — the audit's rule is to fix *one* thing, and the ownership bug
is a live exploit against the shipped anti-duplication design, not a missing
feature; also declined to write a gateway test project tonight to cover this
fix, since that's a larger, separate piece of work than a one-night budget
and would compound the "can't run dotnet" risk with untestable new test
code. Both are logged to BACKLOG.md, ranked above everything else found.

**Added to backlog:** No automated tests for `gateway`/`world-server` (16);
survival death has no consequence, stamina is never spent (16); migrations
have no apply-to-running-database path (9); `World3D.cs`/`SurvivalHud.cs`
are 700-900 line multi-concern scripts (9); `ClientState` sent every render
frame uncapped, bandwidth never measured (4). Full detail in
`docs/nightly/BACKLOG.md`.

**Question for the human:** None blocking — but worth a deliberate answer
before backlog item #2 is picked up: when a character reaches 0 health,
should the design be a hard respawn-with-loss (classic survival-game), or a
"downed" state a nearby player can revive (more consistent with the
"multiplayer-interesting" design value called out for new features)? That
choice changes the shape of the fix enough that I'd rather a human pick the
direction than have a future night guess.
