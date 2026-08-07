# Ashfall — nightly backlog

Ranked findings not yet built, scored `Severity(1-5) × Blast radius(1-5)`.
Threshold for "must fix that night": any score ≥ 15, or any multiplayer-
correctness finding ≥ 9. Anything below is here for a future night to pick
up. Gameplay/content debt (missing skills, wall collision, creatures, etc.)
is already tracked in `docs/gameplay-roadmap.md` — not duplicated here.

## Open

### Player-entity broadcast has no interest management — 3 × 4 = 12
`Program.cs`'s tick loop sends `PlayerStates` (every connected player's
position + yaw) to **every** connected peer, every tick (15 Hz), regardless
of distance. This directly contradicts the stated invariant
("a client only ever receives entities/chunks near it," `architecture.md`
§Invariants, `CLAUDE.md` #6) — chunk requests already respect a radius, but
player entities don't. `Tuning.InterestRadiusChunks` is declared and never
referenced anywhere.

Cost: per receiving player, ~20 bytes/other-player at 15 Hz. At 20 concurrent
players that's ~6 KB/s/player just for position sync (before inventory/stats
traffic); the *server's* per-tick work is O(n²) `NetPeer.Send` calls. Not
urgent at today's likely playtest scale, but it's silent scope creep past a
load-bearing invariant, and it gets worse quadratically as a world fills up.

Fix shape: cull the per-player state list to players within
`Tuning.InterestRadiusChunks` (or a metres radius) of the recipient before
writing the packet — same pattern chunk streaming already uses.

### No automated tests for world-server netcode/persistence — 3 × 3 = 9
`tests/sim-core.tests` is thorough, but nothing exercises `Program.cs`,
`Player.cs`, `WorldStore.cs`, or `GatewayClient.cs`. The double-connection
fix shipped 2026-08-07 has zero regression coverage — a future refactor of
the `Hello` handler could silently reintroduce the duplication bug. Needs an
integration harness: a real (or `Testcontainers`) Postgres, the world-server
started in-process or as a subprocess, two fake LiteNetLib clients driving
Hello/reconnect sequences. Scope as its own night — this is not a small add.

### No migration runner for an already-initialized deployment — 3 × 3 = 9
`infra/migrations/*.sql` is hand-numbered and additive, but the only thing
that ever runs them is Postgres's `docker-entrypoint-initdb.d`, which only
executes against a brand-new (empty) data volume. A long-lived deployment
that already has `pgdata` populated from `001_init.sql` will never pick up
`002`–`004`, or any future file, automatically. Fine for a fresh dev volume
(today's actual usage); a real problem the first time this ships anywhere
persistent. Needs a migrate-on-boot step in gateway or world-server startup
(or a dedicated migrate job/lightweight runner).

### Gateway character save doesn't check ownership — 2 × 2 = 4
`PUT /characters/{id}` in `apps/gateway/Program.cs` upserts unconditionally;
it never checks `owner_world_id` against the caller. Currently harmless
because callers are trusted infra (world-servers on a private network), not
client-reachable. Worth a defense-in-depth check (reject a save from a
world-server that isn't the current owner) before this API is exposed beyond
a single trusted deployment.

## Resolved

### Same-`CharacterId` double connection could duplicate/drop inventory — 4 × 4 = 16
Fixed 2026-08-07. See `LOG.md` for detail. Nothing previously stopped two
live `Player` objects (same world-server) from loading one `CharacterId` at
once — a fast app relaunch before the old socket timed out, or two devices
sharing a UUID — after which the last connection to disconnect silently
overwrote the other's saved progress (last-write-wins upsert, no
coordination). `Hello` now evicts and flushes any existing connection for the
same `CharacterId` before loading the new one.
