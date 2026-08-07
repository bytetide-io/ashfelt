# Ashfall — architecture map (nightly engineer's notes)

Companion to `docs/architecture.md` (owns the invariants) and
`docs/gameplay-roadmap.md` (owns the content/gameplay audit — read that first
for "what's missing to make this a game"). This file is the **technical**
map: what the runtime actually does, tick by tick and message by message, and
where the netcode/persistence debt is. Written from a cold read of the repo
plus the audit that produced the first nightly fix — treat it as a snapshot,
not a spec; update it when the shape of the system changes.

## Processes

- **`apps/gateway`** (net10, ASP.NET minimal API) — the only thing that talks
  to Postgres for character/voyage state. Stateless between requests; all
  state lives in the DB. No auth beyond "you presented a UUID" — there is no
  login system yet, identity is a client-generated device UUID.
- **`apps/world-server`** (net10, console app, top-level statements in
  `Program.cs`) — one process = one bounded region. Single-threaded: a
  `while` loop calls `NetManager.PollEvents()` (which synchronously invokes
  the LiteNetLib event handlers — connect/disconnect/receive — inline) then
  advances the tick. There is no concurrency inside a world-server despite
  `World` using `ConcurrentDictionary`; that's for cheap thread-safe-looking
  code, not because two threads actually touch it.
- **`apps/client`** (net8, Godot 4 C#) — predicts movement locally
  (`PlayerBody` + `MovementRules`), sends `ClientState` at `Tuning.ClientStateHz`
  (15 Hz), reconciles on `Correction`. Renders remote players by lerping
  toward the last `PlayerStates` snapshot (`RemotePlayers.cs`).

## One world-server tick (`Program.cs`, `tickMs = 1000/Tuning.TicksPerSecond`, 15 Hz)

1. `server.PollEvents()` — drains the socket, runs `Hello`/`ClientState`/
   `ChopRequest`/`CraftRequest`/`EatRequest`/`PlaceRequest`/`RequestRelease`
   handlers synchronously.
2. `AdvanceSurvival` for every connected player (hunger/stamina/health/warmth,
   `SurvivalRules` — integer-deterministic, no float accumulation).
3. Broadcast `PlayerStates` (unreliable) to **every** connected peer,
   unconditionally — see Known debt below.
4. Broadcast `InventoryUpdate` (reliable) to any player whose inventory
   changed since the last tick.
5. Every `StatsHeartbeatTicks` (2s), broadcast `StatsUpdate` to everyone.

## Ownership model

- **Character** (inventory + survival meters): owned by the gateway DB,
  `character.owner_world_id` says which world-server currently holds the live
  copy. `NULL` means in-transit (mid-voyage) — nobody owns it, which is what
  makes a crash mid-transfer recoverable instead of duplicating.
- **Within one world-server**, ownership used to be unenforced: nothing
  stopped two live `Player` objects from loading the same `CharacterId`
  (e.g. a fast app relaunch before the old socket's keepalive timed out).
  Fixed tonight — see `docs/nightly/LOG.md` 2026-08-07. The rule now: a
  `Hello` for a `CharacterId` already attached to a connected peer evicts the
  old peer (flushing its state first) before the new one loads.
- **World tiles/structures**: `World` is the only mutator; `_diffs` and
  `_structures` are the persisted surface, `_harvestStrikes` is deliberately
  never persisted (a half-chopped tree resets on restart, by design).

## Persistence

- World: seed + diffs (`tile_diff`) + structures (`structure`), one row per
  changed tile / placed structure, upserted. Never a full-chunk write.
- Character: one row per UUID (`character`), whole-row upsert on every save
  (leave, or explicit release-for-voyage). No optimistic locking — the last
  writer wins, which is exactly why the same-server double-connection bug
  mattered (two writers racing with no coordination).
- Schema: four hand-numbered SQL files under `infra/migrations`, additive
  (`ADD COLUMN IF NOT EXISTS`) and idempotent, but only ever *applied* by
  Postgres's `docker-entrypoint-initdb.d`, which only runs against an empty
  data volume. There is no migrate-on-boot step for an already-initialized
  deployment. See backlog.

## Known debt (technical, not gameplay — gameplay debt is in gameplay-roadmap.md)

Scored `Severity(1-5) × Blast radius(1-5)`; full detail in `BACKLOG.md`.

| Finding | Score |
|---|---|
| Same-`CharacterId` double-connection could duplicate/drop inventory | 16 — **fixed tonight** |
| `PlayerStates`/entity broadcast has no interest management (sent to all peers every tick, unconditionally) | 12 |
| No automated tests for world-server netcode/persistence (`Program.cs`, `Player.cs`, `WorldStore.cs`) | 9 |
| No migration runner for an already-initialized Postgres volume | 9 |
| Gateway's `PUT /characters/{id}` doesn't check `owner_world_id` before writing | 4 |

God-scripts (`World3D.cs` 735 lines, `SurvivalHud.cs` 940 lines) are real but
already tracked in `docs/gameplay-roadmap.md` §3.4 — not duplicated here.
