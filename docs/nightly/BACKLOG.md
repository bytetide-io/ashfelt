# Ashfall — nightly backlog

Ranked by Severity(1-5) × Blast radius(1-5), highest first. Populated by the
first nightly audit (2026-07-30); one item (dual-session character ownership)
was fixed the same night and is not repeated here — see `LOG.md`.

## 1. `DeterminismTests.cs` has no golden/pinned reference value — score 25 (sev 5 × blast 5)

`tests/sim-core.tests/DeterminismTests.cs` only asserts internal
self-consistency (two fresh instances built from the *same current code*
agree with each other, or a chunk agrees with per-tile lookups). It never
freezes an expected tile/hash value for a fixed seed against a known-good
baseline. Consequence: changing a magic constant in `Noise.Hash`, or an
elevation threshold in `TerrainGenerator.cs:52-61`, passes the entire suite
silently while reshaping every persisted world — directly contradicting the
CLAUDE.md claim that this file is a "compatibility contract." **This was the
other finding that crossed tonight's fix-it threshold** (tied at 25 with the
ownership bug); it lost priority only because it's a missing safety net, not
an active exploit — see LOG.md for the reasoning. Recommended fix: add a test
that pins `TerrainGenerator.TileAt`/`Noise.Hash` output for `seed=1337` at a
handful of fixed coordinates against literal expected values, so any future
change to generation must consciously update (and justify) those constants.

## 2. World-server blocks its single event thread on gateway HTTP calls, no timeout — score 20 (sev 4 × blast 5)

`apps/world-server/Program.cs` calls `GetCharacterAsync`/`SaveCharacterAsync`/
`ClaimVoyageAsync`/`RequestVoyageAsync` via `.GetAwaiter().GetResult()` from
inside `NetworkReceiveEvent`, which LiteNetLib fires synchronously on the same
thread as the tick loop. `GatewayClient`'s `HttpClient` has no explicit
timeout, so one player's slow Hello/Release can stall movement broadcast,
correction, and ticking for **every** connected player for up to the .NET
default HttpClient timeout (~100s). Fix: give `GatewayClient`'s `HttpClient` a
short explicit `Timeout`, and consider moving gateway round trips off the
event thread (e.g. a bounded async queue) if playtest population grows enough
to make even a fast gateway call feel laggy.

## 3. World3D.cs god-script — score 16 (sev 4 × blast 4)

`apps/client/scripts/world3d/World3D.cs` is 735 lines (up from 616 when
`docs/gameplay-roadmap.md` §3.4 flagged it — none of the recommended splits
have happened). One `Node3D` owns network-event wiring, terrain/foliage mesh
generation, day/night lighting, touch input + gather-target physics, and
structure rendering. Recommended split unchanged from the roadmap: net
dispatch, terrain streaming, local player, remote players, interaction/
targeting as separate systems/nodes.

## 4. No interest management — broadcast-to-all on every tick and every world event — score 12 (sev 3 × blast 4)

`Program.cs:392-406` broadcasts `PlayerStates` (every player's position) to
**every** connected peer at 15 Hz with zero proximity/chunk filtering,
violating architecture invariant #6. Rough cost: `(2 + 20·N)` bytes × 15/s per
player — at N=50 that's ~15 KB/s/player egress (~750 KB/s aggregate),
growing O(N²) with population, on a mobile/cellular budget. `StructurePlaced`,
`TileChanged`, and `HarvestProgress` (`Program.cs:145-150,219-236,292-294`)
have the same gap, reliable-ordered so they can also queue up under load.
Needs a real interest-management pass (chunk-radius subscription) before
population grows past a handful of concurrent players per world.

## 5. No schema/format version field on any persisted table — score 12 (sev 3 × blast 4)

Neither `character`, `tile_diff`, `structure`, nor `voyage_ticket` carries a
version column. `CharacterState.Inventory` (`packages/shared-proto/Character.cs:14`)
is keyed by `ItemId` enum *name* specifically to survive a wire renumber — a
real, working piece of design — but `Player.LoadCharacter`
(`apps/world-server/Player.cs:119-130`) silently `Enum.TryParse`s and *drops*
any key that fails to parse, so an enum **rename or removal** is silent data
loss with no thrown error and no migration path. Add a version field before
the first shipped save, and make a failed-parse loud (log at minimum) rather
than silent.

## 6. Walls don't actually block movement — score 9 (sev 3 × blast 3)

`PlacementRules.Blocks(ItemId)` (`packages/sim-core/PlacementRules.cs:31-35`,
Wall = solid) is defined but never consulted by `MovementRules.Check` or
`Player.TryAccept` — the server only checks terrain height. A placed Wall is
currently scenery; a modified (or even a legitimate) client can walk straight
through it. Building has no server-enforced consequence yet — this is also
called out in `docs/gameplay-roadmap.md` Phase B ("wall collision & real
shelter") as a prerequisite for any future threat/hazard content.

## 7. ChopRequest has no cooldown or stamina gate — score 9 (sev 3 × blast 3)

`SurvivalRules.TrySpendStamina` exists but nothing calls it from
`Program.cs:192-242`'s `ChopRequest` handler, and there's no per-strike
cooldown. A client can fire chop requests as fast as the socket allows,
farming resources far above the intended swing cadence and inflating the
shared economy once there's anything to trade or compete over.

## 8. SurvivalHud.cs — UI + direct networking in one 940-line file — score 9 (sev 3 × blast 3)

Grew 124% since `docs/gameplay-roadmap.md` §3.4 measured it at 419 lines.
Calls `_connection.SendCraft/SendEat/SendPlace` directly from UI code, and
hand-builds the "icon + slot frame + count badge" pattern independently three
times (`HotbarSlotFor:269`, `IconTile:717`, `GridSlot:847`) instead of one
shared widget — exactly the reusable item-slot widget §3.4 asked for and
never landed. Per CLAUDE.md's own "third occurrence is a refactor" rule.

## 9. Migrations have no version tracking for live deployments — score 6 (sev 2 × blast 3)

`infra/migrations/*.sql` only auto-applies via Postgres
`docker-entrypoint-initdb.d`, which only runs against an *empty* data
directory. A live volume won't pick up `003_voyage.sql`/`004_warmth.sql`
without a manual operator step, and nothing tracks which migrations have run.
The SQL itself is idempotent and a missing column fails loudly (Npgsql
exception), so this is an availability gap, not silent corruption — but worth
a proper migration runner before a real deployment.

## 10. `World.Diffs`/`World.Structures` exposed via unordered `ConcurrentDictionary.ToArray()` — score 6 (sev 2 × blast 3)

`packages/sim-core/World.cs:36,145`. Harmless today (both are consumed via
keyed lookups, never order-dependent), but it's the one place in sim-core
where dictionary-iteration-order risk is part of the public surface. Worth a
comment or a switch to an ordered structure if anything ever iterates these
for persistence or hashing.

## 11. Sibling coupling via relative `GetNode("../X")` — score 6 (sev 3 × blast 2)

`apps/client/scripts/world3d/PlayerBody.cs:46` and `OrbitCamera.cs:29` reach
sibling nodes by relative path instead of an exported `NodePath` or a signal.
Renaming or reparenting either node in the scene breaks these silently at
runtime with no compile-time signal.

## 12. Fire-and-forget character save has no ordering guard against a fast reconnect — score 6 (sev 3 × blast 2)

Tonight's fix (see LOG.md) closes the *ownership* race — two live sessions for
one character can no longer coexist. It does not close a narrower, lower-harm
race: `PeerDisconnectedEvent` saves via `Task.Run` (fire-and-forget); if the
same character reconnects fast enough, its `GET` claim can land *before* the
old save does, loading data that's one save-interval stale. No item
duplication results (the new session simply starts from slightly older data,
and its own next save overwrites the DB again), but recent pre-disconnect
progress can be silently lost. Fix would be a monotonic version/timestamp
column so an out-of-order write is detected and dropped, or awaiting the save
synchronously before completing a same-world reconnect's claim.

## 13. New residual risk introduced by tonight's fix: a world-server crash mid-session locks the character to that world id — score 4 (sev 2 × blast 2)

Unlike the voyage ticket path (TTL self-heal via `ReclaimExpiredTicketAsync`),
an ordinary join's ownership claim has no expiry. If the world-server process
itself crashes (not a clean player disconnect — LiteNetLib's own
disconnect/timeout detection still fires the save+release path for a merely
dropped connection), the character's `owner_world_id` stays set with nobody
alive to release it, and every future join anywhere is denied until an
operator manually clears the column. Before this becomes a real deployment's
problem: either give ordinary ownership the same TTL self-heal the voyage
ticket already has, or add an admin/ops endpoint to force-clear a stuck claim.

## 14. `RequestChunk` has no bound or rate limit — score 4 (sev 2 × blast 2)

`Program.cs:154-169`. A client can request arbitrarily distant chunk
coordinates repeatedly, forcing unbounded terrain generation with no
interest-based restriction. Low priority until chunk generation cost rises or
interest management (#4) lands, at which point this should be gated by the
same mechanism.

## 15. `ItemCatalogTests` doesn't assert a harvest node's `Becomes` tile is walkable — score 4 (sev 2 × blast 2)

`tests/sim-core.tests/ItemCatalogTests.cs:65-74`. A future harvest node felling
into, say, `Rock` instead of `Grass` would silently trap the tile with no test
catching it.

## 16. WorldConnection.OnReceive is a growing switch, not a dispatch table — score 4 (sev 2 × blast 2)

`apps/client/scripts/WorldConnection.cs:299-420`, 13 cases and counting.
`docs/gameplay-roadmap.md` §3.4 already asked for a table keyed by
`MessageId` instead; every new message type currently means another case.

## 17. Item-slot widget hand-duplicated three times in the client — score 4 (sev 2 × blast 2)

Same underlying issue as #8, filed separately because it's a standalone,
independently fixable unit of work: `HotbarSlotFor` (`SurvivalHud.cs:269`),
`IconTile` (`:717`), `GridSlot` (`:847`) each reimplement icon+badge layout
with slightly different offsets. One shared widget would fix all three call
sites at once.

## 18. Hardcoded dev secrets — score 4 (sev 2 × blast 2)

`infra/docker/docker-compose.yml` (Postgres password) and the default
`ASHFALL_CONNECT_KEY`/LiteNetLib connect key ("ashfall") in both
`apps/client/scripts/WorldConnection.cs:24` and
`apps/world-server/Program.cs:9`. Fine for local dev; needs a check (docs note
or a startup warning) that these are always overridden outside dev before any
real deployment.

## 19. Balance numbers scattered across six files, two packages — score 1 (sev 1 × blast 1)

No single `Tuning`-style table: density/elevation constants in
`TerrainGenerator.cs`, hit counts/tool bonuses in `HarvestRules.cs`, recipe
costs in `CraftingRules.cs`, food value/warmth radius in `ItemCatalog.cs`,
per-minute rates in `SurvivalRules.cs`, and tick rate/day length in a
*different* `Tuning` class in `packages/shared-proto/Protocol.cs:142`. Already
on record in `docs/gameplay-roadmap.md` §3.5; low urgency until an actual
balancing pass is scheduled.

## 20. Dead `structure.health`/`structure.owner_id` columns — score 1 (sev 1 × blast 1)

`infra/migrations/001_init.sql:35-36`. Never read or written by `WorldStore`
or `sim-core.Structure`. Harmless schema drift; drop or wire up whichever a
future structure-durability/ownership feature needs.
