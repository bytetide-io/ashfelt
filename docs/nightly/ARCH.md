# Ashfall — architecture map (nightly engineer's view)

Written night 1 of the nightly-engineer routine, as the required first
deliverable. This is an honest snapshot of what actually exists in the repo,
not a restatement of `docs/architecture.md` (which is the intended design —
read that first). Update this file when the shape of the system changes
meaningfully; it should stay a map you can navigate by, not a diary.

## Size, as of 2026-08-10

| Area | Files | Lines |
|---|---|---|
| `apps/client` (C#, Godot) | 15 | ~3,400 |
| `apps/world-server` | 4 | ~950 (after tonight's change) |
| `apps/gateway` | 1 | ~210 |
| `packages/sim-core` | 12 | ~1,000 (after tonight's change) |
| `packages/shared-proto` | 2 | ~180 |
| `tests/sim-core.tests` | 11 | ~1,120 (after tonight's change) |

Small. One engineer can hold the whole thing in their head in an evening,
which is exactly what made tonight's audit tractable without needing to
fan out research agents.

## Process topology

```
Godot client (net8.0, C#)
   │ UDP, LiteNetLib, port 9050
   ▼
world-server (net10.0)  ──HTTP──▶ gateway (net10.0, ASP.NET minimal API)
   │ optional                         │
   ▼                                  ▼
Postgres (tile_diff, structure)   Postgres (character, voyage_ticket)
```

- One `world-server` process = one bounded region. `ASHFALL_WORLD_ID` names it;
  the gateway's static `worlds` dictionary (`apps/gateway/Program.cs`) is the
  only place that maps a world id to a host:port today — adding a world means
  editing that dictionary and launching another process.
- `world-server` runs a single-threaded tick loop at `Tuning.TicksPerSecond`
  (15 Hz) using `Thread.Sleep`, not a fixed-timestep accumulator — ticks will
  drift slightly under load rather than staying wall-clock exact. Not
  currently a problem: nothing depends on tick i landing at time i/15s exactly,
  only on elapsed real time between accepted client moves (`MovementRules`)
  and on integer tick counts for survival drain (`SurvivalRules`), both of
  which are robust to jitter by construction.
- Gateway persistence is required (`ASHFALL_DB` throws on startup if unset);
  world-server persistence is optional (falls back to memory-only, logged).

## Data flow for the four things that matter

**Movement.** Client runs real physics (`PlayerBody.cs`, Godot
`CharacterBody3D`) and reports position at `ClientStateHz` (15). Server checks
the report against `MovementRules.Check` (bounds speed/rise/fall/terrain
clipping against elapsed time since the *last accepted* position — spamming
updates buys nothing) and either accepts silently or sends `Correction`. No
server-side physics engine. This is a deliberate, documented trade
(`docs/architecture.md` "Movement authority"): cheap, catches every
cheat that matters, does not catch inhuman-but-legal input. Revisit only if
PvP makes it matter.

**Harvest/craft/place.** Client sends a request (`ChopRequest`/
`CraftRequest`/`PlaceRequest`); `sim-core` rules (`HarvestRules`,
`CraftingRules`, `PlacementRules`) decide server-side against the
authoritative `Player.Inventory` / `World` state. The client never mutates
its own inventory speculatively — it waits for `InventoryUpdate`. Genuinely
server-authoritative, no gaps found.

**Persistence.** World: seed + `tile_diff` rows + `structure` rows only
(`WorldStore.cs`) — chunks are regenerated, never stored, correctly honouring
invariant #2. Character: gateway-owned JSONB blob (`inventory`) plus four
survival ints, upserted on leave and loaded on Hello
(`apps/gateway/Program.cs`, `Player.LoadCharacter`/`ToCharacterState`).
Neither store has a schema version column — see backlog.

**Voyages.** Instant v1, exactly as `docs/voyage-transfer.md` describes.
The ticket dance (mint on release, single-use consume on claim, TTL
self-heal via `ReclaimExpiredTicketAsync`) is careful, race-tested-by-reading
SQL (`ON CONFLICT`/`WITH ... RETURNING` used correctly to make claims
atomic) and was the best-designed corner of the codebase on inspection.

## Where the debt actually is

1. **Interest management was declared but not implemented** — fixed tonight
   for player-position broadcasts; tile/structure broadcasts still fan out to
   the whole world. See `LOG.md` 2026-08-10 and `BACKLOG.md` #1.
2. **God-scripts on the client**: `SurvivalHud.cs` (940 lines: procedurally
   builds *and* refreshes every HUD surface — meters, hotbar, gather prompt,
   and all four action-sheet tabs) and `World3D.cs` (735 lines: net dispatch,
   terrain/foliage building, local-player interaction, day/night, structure
   rendering). Both already flagged in `docs/gameplay-roadmap.md` §3.4 with a
   concrete decomposition plan; they have grown since that plan was written
   (419→940 and 616→735 lines) without the split happening. Not touched
   tonight — see backlog for why.
3. **`world-server/Program.cs` is a 500-line top-level-statements file**
   acting as the entire netcode layer: connection lifecycle, one big
   `switch` on `MessageId`, and the tick loop, all as closures over shared
   mutable locals (`players`, `tick`, `writer`). It reads cleanly today
   because the message set is small (9 client→server types), but
   `docs/gameplay-roadmap.md` §3.2 already anticipates this won't hold once
   more message types arrive, and proposes a dispatch table + typed
   read/write helpers instead of hand-paired `writer.Put`/`reader.Get` calls.
4. **All players spawn at the exact same point.** `Player.FindSpawn` is a
   pure function of the world (not of who's asking), so every new
   connection lands on the identical tile. Harmless with 1-2 dev clients;
   worth a look before any real multiplayer session (see backlog).
5. **No schema version field** on either the `tile_diff`/`structure` tables
   or the gateway's `character` table (`infra/migrations`). A future format
   change has no migration path yet. Not urgent pre-alpha (no world has
   shipped, per `docs/architecture.md`'s own "generation changes on record"
   section), but the moment a real world exists this becomes load-bearing.

## Things that are in better shape than a fresh audit usually finds

- `sim-core` is genuinely engine-free, integer/deterministic where it needs
  to be, and every rule file has a test file with a matching name in
  `tests/sim-core.tests`.
- The gateway's voyage-ticket SQL correctly handles the concurrent-claim and
  expired-ticket-recovery races without an operator in the loop.
- Fail-loud invariant checks (`Player.ConsumeOne`, `Player.ApplyCraft`) throw
  rather than silently clamp, exactly per the `CLAUDE.md` standard.
