# Ashfall — architecture map (nightly notes)

Honest snapshot as of 2026-07-26. This is a supplement to `docs/architecture.md`
(the source of truth for decided invariants) — this file is where the nightly
process tracks *actual* code shape, hotspots and debt, updated as it's found.
`docs/architecture.md` wins on any conflict.

## What this project actually is

Despite generic "survival game" briefs sometimes assuming a 2D GDScript /
`.tscn`-scene-graph project: this is a **C# Godot 4 client**, **3D**,
**physics-based movement**, with two C# backend services. There is no combat
or PvP implemented anywhere (verified by grep across `apps/` and `packages/`
for `Combat|PvP|Damage|Attack` — zero implementation hits, docs only mention
it as deferred/future). Treat any brief that assumes otherwise as describing a
different, hypothetical codebase.

## Processes and their language/runtime

| Process | Path | Target | Role |
|---|---|---|---|
| client | `apps/client` | net8.0 (Godot 4.3+ Mono) | Renders world, predicts movement, sends requests |
| world-server | `apps/world-server` | net10.0 | One process per region; UDP (LiteNetLib), authoritative sim tick |
| gateway | `apps/gateway` | net10.0 | HTTP REST; owns accounts/characters in Postgres, brokers voyages |
| sim-core | `packages/sim-core` | net8.0 | Shared deterministic rules: terrain, movement, harvest, craft, placement, survival, clock, item catalog |
| shared-proto | `packages/shared-proto` | net8.0 | Wire message IDs, protocol version, `Tuning` constants |

net10 processes referencing net8 libraries is intentional (`docs/architecture.md`).

## world-server: the hot path

`apps/world-server/Program.cs` is a single top-level-statements file running a
single-threaded loop:

```
while (!shutdown.IsSet) {
    server.PollEvents();       // dispatches NetworkReceiveEvent synchronously, inline
    tick++;
    // advance survival meters for every player
    // broadcast PlayerStates (unreliable, every tick, to everyone — no interest mgmt)
    // flush any dirty inventories (reliable)
    // every StatsHeartbeatTicks: broadcast survival+clock stats
    Thread.Sleep(tickMs);       // tickMs = 1000 / Tuning.TicksPerSecond (15 Hz)
}
```

Everything — movement acceptance, harvesting, crafting, placement, voyage
release/claim — is handled inline inside the `NetworkReceiveEvent` lambda, on
this same thread. There is no lock contention because there is only one
thread touching `World`/`Player` state; the cost is that **any blocking call
made from that lambda blocks every other player for its duration.**

Two calls into the gateway happen synchronously on this thread by deliberate
design (see comments at `Program.cs:114-118,315-318`): character load at
`Hello`, and character save + voyage mint at `RequestRelease`. The reasoning
recorded in-code: join/release are rare, not per-tick, and blocking avoids
cross-thread mutation of `World`/`Player`. That tradeoff is sound *if* the
blocking call has a bound. It didn't — see 2026-07-26 log entry.

Saves on disconnect and diff/structure writes are fire-and-forget
(`Task.Run`, not awaited on the tick thread) — correctly, since those aren't
gating anything the requester is waiting on.

## Known debt (see BACKLOG.md for scored detail)

- **No interest management enforcement.** `Tuning.InterestRadiusChunks` is
  declared in `shared-proto` but never read in `world-server` — every
  broadcast (`PlayerStates`, `TileChanged`, `HarvestProgress`,
  `StructurePlaced`) goes to every connected peer regardless of distance.
  Violates invariant #6 in `docs/architecture.md`.
- **No dedupe on character ownership.** `Hello` never checks whether
  `CharacterId` is already loaded by another live `Player` in this process
  (or, implicitly, another world-server); the gateway's character PUT is an
  unconditional upsert with no `owner_world_id`/version guard outside the
  voyage-ticket path. A double-join can silently duplicate or roll back
  inventory.
- **No tests for world-server or gateway.** `tests/sim-core.tests` covers
  `packages/sim-core` well (determinism, movement, harvest, craft, placement,
  survival all have dedicated test files). The netcode/persistence layer —
  where multiplayer correctness bugs actually live — has zero automated
  coverage.
- **No migration tracking.** `infra/migrations/*.sql` is mounted as
  Postgres `docker-entrypoint-initdb.d`, which only runs once against an
  empty volume. A live deployment never re-applies later migration files.
- **Protocol-mismatch UX.** The wire protocol *is* versioned
  (`ProtocolVersion.Current`, checked at `Hello`) and a mismatch is rejected
  correctly — but the client's reconnect loop (`WorldConnection.cs`) can't
  distinguish "server restarting" from "you're on the wrong build" and retries
  forever with a generic message.
- **`SurvivalHud.cs` (940 lines) and `World3D.cs` (735 lines)** are the two
  largest client files and each own several loosely-related UI/render
  responsibilities. Not yet causing bugs, but the natural next thing to
  decompose before a third feature tab gets bolted onto `SurvivalHud`.
- **Day/night phase math duplicated** between `SurvivalHud.UpdateDayChip` and
  `World3D.UpdateSky` (both independently compute the same
  `sin((timeOfDay-0.25)*Tau)` shape).

## Client structure (`apps/client/scripts`)

- `WorldConnection.cs` — LiteNetLib client, protocol de/serialization, request
  helpers (harvest/craft/place/eat/voyage), reconnect loop.
- `world3d/World3D.cs` — top-level 3D scene controller: terrain chunk
  loading/meshing, other-player rendering, gather-target reticle resolution,
  placement targeting, sky/day-night visuals. Correctly treats every
  gather/place as a *request* re-validated by the server — no
  client-authoritative game-state logic found here.
- `world3d/SurvivalHud.cs` — all HUD: meters, day/night chip, hotbar,
  joystick/jump layout, gather prompt, and the four-tab action sheet
  (Items/Craft/Build/Travel).
- `world3d/TerrainMesher.cs`, `PlayerBody.cs`, `VirtualJoystick.cs`,
  `PixelTextures.cs`, `CrtOverlay.cs` — supporting rendering/input pieces.
- `ui/DesignSystem.cs`, `ui/PixelIcons.cs`, `ui/TitleScreen.cs` — the shared
  visual system (Ink/Ember palette, pixel-grid icons baked at runtime, no
  hand-authored PNGs).

## Persistence

- `WorldStore` (Postgres): world seed, tile diffs, structures, keyed by
  world id. Chunks are never persisted — only seed + diffs, per invariant #2.
- Gateway (Postgres): `character` table (inventory + survival meters, keyed
  by device UUID), voyage/ticket tables. Character state is the only
  cross-world-server durable state, per invariant #3.
- Neither store has a schema-version column on its rows; migrations are
  tracked only by file order, not by an applied-migrations table (see debt
  above).
