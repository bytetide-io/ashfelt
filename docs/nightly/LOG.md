# Ashfall — nightly log

## 2026-08-02 — AUDIT-FIX (multiplayer correctness)

**Chose:** Fixed a character-ownership race between the gateway and
world-server that let the same character be loaded (and independently
mutated) by two sessions at once, or reloaded with stale pre-session data on
a fast reconnect.

**Because:** Audit finding, scored under the required Severity × Blast-radius
rule:

- `character.owner_world_id` (added in `infra/migrations/003_voyage.sql`) was
  only ever written by the voyage-ticket claim/mint path. The normal
  join (`GET /characters/{id}`) never read or checked it, and the normal
  leave (`PUT /characters/{id}`) never cleared it. Concretely, this meant:
  1. **Cross-server double-load.** Any two world-server processes could load
     the same characterId concurrently with zero coordination — nothing
     stopped it. Whichever session saved last on disconnect silently
     overwrote the other's progress.
  2. **Same-server reconnect race.** Character saves on disconnect are
     fire-and-forget (`Program.cs`, `PeerDisconnectedEvent` — deliberately,
     so one slow write can't stall the other players in the tick loop). A
     player reconnecting quickly (the normal case on mobile: app
     backgrounded, network drop, relaunch) could hit `Hello` before that save
     landed, load the *pre-disconnect* state from the gateway, and then have
     their new session's save clobber whatever the first save eventually
     wrote — silent inventory/meter rollback.
  3. A **pre-existing, adjacent bug** found while fixing the above: rejecting
     a join (bad protocol version was already safe; bad voyage ticket was
     not) left `player.CharacterId` set on a `Player` that was never actually
     loaded. The subsequent disconnect then fired a save of *default, empty*
     state over the real character's row — silently wiping it. Fixed the same
     way as the new ownership-rejection path (see Changed).

  Severity 4 (silent, unrecoverable inventory/meter data loss in a
  survival game — the entire point of a persistent character) × Blast
  radius 4 (every join, every reconnect, every player) = 16, and
  independently qualifies as a multiplayer-correctness finding ≥ 9. Per the
  audit's decision rule this must be fixed tonight; Phase 2 (new feature)
  was skipped.

**Changed:**
- `apps/gateway/Program.cs` — `GET /characters/{id}` now takes a required
  `worldId` query parameter and atomically claims ownership as part of the
  same `UPDATE ... RETURNING` that reads the character (200 claimed/loaded,
  404 never-saved, 409 owned by a different world right now). `PUT
  /characters/{id}` now always clears `owner_world_id` to `NULL` as part of
  the same upsert — every save is a leave, so it must also release the claim
  (previously nothing ever cleared it after a normal, non-voyage session).
- `apps/gateway/API.md` — documents the new query param and the 409 case.
- `apps/world-server/GatewayClient.cs` — `GetCharacterAsync` now takes
  `worldId` and returns a `CharacterLoadResult` (`New` / `Loaded` /
  `OwnedElsewhere`) instead of a bare nullable `CharacterState`.
- `apps/world-server/Program.cs` — Hello handler: denies the join (disconnects
  the peer) on `OwnedElsewhere` instead of silently proceeding; a new
  `pendingSaves: Dictionary<Guid, Task>` tracks each character's in-flight
  disconnect-save, and Hello awaits (only) *that character's own* pending
  save before loading, closing the same-server reconnect race without
  touching the deliberate fire-and-forget behavior for other players. Both
  join-rejection paths (bad voyage ticket, and the new owned-elsewhere case)
  now reset `player.CharacterId` to `Guid.Empty` before disconnecting, so the
  resulting `PeerDisconnectedEvent` doesn't fire a save at all for a
  character that was never actually loaded.
- `infra/migrations/005_reset_character_ownership.sql` — one-time backfill
  clearing any `owner_world_id` left stranded by the old (non-enforcing)
  code, so no existing dev/test character starts out wrongly locked to a
  world under the newly-enforced check. No column changes — `owner_world_id`
  already existed; this only resets data.

**Risk:** The gateway's `/characters/{id}` GET contract changed (now requires
`worldId`, can return 409) — every caller in this repo (the world-server) was
updated in the same change, so nothing internal is broken, but this is a
breaking change to that endpoint's shape if anything external ever called it
directly. Also: a world-server that crashes uncleanly (not a graceful
disconnect) will strand `owner_world_id` on the dead world, locking that
character out of every world-server until manually cleared — this is a
**known, deliberately out-of-scope gap**, logged to BACKLOG.md rather than
silently left undocumented, because closing it properly needs a lease/TTL
mechanism (bigger than one night, see backlog item).

**Revert:** `git revert` the commit on this branch touching
`apps/gateway/Program.cs`, `apps/gateway/API.md`,
`apps/world-server/GatewayClient.cs`, `apps/world-server/Program.cs`, and
drop `infra/migrations/005_reset_character_ownership.sql` (it's a pure data
reset with no schema change, safe to skip if reverting).

**Verified:** Manual line-by-line trace of every changed code path against
the existing call sites (confirmed no other caller of the changed
`GatewayClient.GetCharacterAsync` signature; confirmed the `pendingSaves`
dictionary is only ever mutated from the single-threaded LiteNetLib event
dispatch, so no new race was introduced by adding it; confirmed the SQL in
both the claim `UPDATE...RETURNING` and the reset migration against the
actual `character` table schema in `infra/migrations/002_character.sql` and
`003_voyage.sql`).

**Not verified — a human needs to check this:** **I could not build or run
either project.** This container has no .NET SDK installed, and installing
one is blocked: the org's egress proxy returns 403 for `dot.net` (confirmed
via `/root/.ccr/README.md` — 403 means a policy denial, not to be retried or
routed around). So none of this was compiled, let alone run against a real
Postgres instance or exercised with two simulated clients. Before trusting
this: `dotnet build apps/gateway apps/world-server`, then a manual two-client
test — connect, disconnect, immediately reconnect with the same device UUID
and confirm inventory survives; and a cross-process test — point two
world-server instances at the same gateway/DB and confirm the second `Hello`
for an already-connected character gets refused (log line: `rejecting join
for ... owned by another world-server`) instead of silently joining.
`dotnet test tests/sim-core.tests` was not touched by this change and was
not re-run for the same reason (no SDK available); this change does not
touch `packages/sim-core` at all, so it should be unaffected.

**Rejected tonight:**
- A full lease/heartbeat mechanism for ownership (closing the crash-stranding
  gap completely) — correct long-term fix, but it's a second subsystem (a
  periodic renew call from world-server to gateway) on top of tonight's core
  fix, and "ship one complete, correct, reversible change" argued for landing
  the concrete, common-case bug (mobile reconnect races, cross-server double
  join) now and scoring the crash-recovery gap separately rather than
  shipping both half-verified.
- Reworking the disconnect save to be synchronous/blocking instead of adding
  the `pendingSaves` gate — would have undone an explicit, commented design
  decision in the existing code (fire-and-forget saves so one slow write
  can't stall other players) without a reason strong enough to justify
  reversing it; the gate achieves the same correctness without that cost.

**Added to backlog:** see `docs/nightly/BACKLOG.md` — ownership
liveness/heartbeat gap (12), no gateway/world-server test coverage (12),
client god-scripts (9, pointer to existing roadmap doc), static world
registry (8), unmeasured chunk-request cost (6).

**Question for the human:** This session could not install the .NET SDK
(org proxy blocks `dot.net`) and so could not compile or test this change at
all — please build and run the two-client reconnect scenario described above
before merging; I'm confident in the logic from manual trace but have zero
executed verification.

---

## Process note (this run only)

This repo's standing git instructions for this session pin work to branch
`claude/relaxed-pascal-9jkk78` (already existed, tracks an open PR), which
conflicts with the nightly prompt's `nightly/{{YYYY-MM-DD}}` branch
convention. Followed the repo-level branch assignment since it's an explicit
session/infrastructure setting, not a content instruction — commits for
tonight's fix landed there instead of a new `nightly/2026-08-02` branch.
Flagging so a future run (or a human) can decide whether nightly runs should
get their own dedicated branch going forward.
