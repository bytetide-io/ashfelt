# Architecture map — nightly baseline (2026-08-04)

Honest snapshot of the codebase as it stands tonight, for whoever runs the
next nightly session. Read `docs/architecture.md` and `docs/voyage-transfer.md`
first — they're the decided source of truth on shape and rationale. This file
is the "what's actually here and what's shaky" companion.

## Layout

```
apps/client/       Godot 4 (C#, net8.0) — mobile client, 3D low-res-render pixel art
apps/world-server/  net10.0 console app — one process per bounded world, UDP (LiteNetLib)
apps/gateway/       net10.0 ASP.NET minimal API — accounts, character store, voyage tickets
packages/sim-core/  net8.0 — terrain gen, movement/harvest/craft/survival rules (shared)
packages/shared-proto/ net8.0 — wire message ids, Tuning constants, CharacterState DTO
tests/sim-core.tests/  xunit, net10.0 — the ONLY automated test project in the repo
infra/              docker-compose (postgres), sql migrations
```

## Data flow, end to end

1. Client boots, loads/creates a device UUID (`WorldConnection.LoadOrCreateCharacterId`,
   `user://character_id`) — this UUID *is* the character identity, no login.
2. Client connects (LiteNetLib, UDP, key-gated) to a world-server, sends `Hello`
   with protocol version + UUID (+ voyage ticket if arriving from another world).
3. World-server loads the character from the gateway (`GatewayClient`, blocking
   HTTP call inline in the packet-receive handler — deliberate, see `Player.cs`
   doc comments), replies `Welcome` with seed + spawn.
4. **As of tonight's fix**: client requests every chunk in its build radius
   (`RequestChunk`/`ChunkData`) and waits for all of them before generating
   any terrain/foliage, so it inherits the server's persisted diffs. Before
   tonight it never did this — see LOG.md.
5. World-server ticks at `Tuning.TicksPerSecond` (15 Hz): advances survival
   meters, broadcasts `PlayerStates` (unreliable) to every connected peer,
   flushes dirty inventories, and heartbeats stats every 2 seconds.
6. Harvesting, crafting, eating, placement are all request → server-validates →
   broadcast-result. Nothing the client does is trusted as fact (invariant #1).
7. Voyage: `RequestRelease` → world A saves character to gateway, gateway mints
   a single-use ticket and marks the character in-transit (owner = NULL) →
   client reconnects to world B with the ticket → B claims it via the gateway
   → B admits and loads. Recoverable if B never claims: ticket expires, gateway
   self-heals ownership back to A (`ReclaimExpiredTicketAsync`), checked lazily
   on the next character GET. This is genuinely well thought through — no
   duplication path was found.

## Where the load-bearing invariants actually live in code

- **Client input is a request** — `world-server/Program.cs`'s big switch on
  `MessageId`; every case either mutates server state and broadcasts, or does
  nothing. No case trusts a client-reported value without a check (position is
  bounded by `MovementRules.Check`, reach by `Player.IsWithinReach`, inventory
  by `Player.Has`/`ConsumeOne` throwing on underflow).
- **Seed + diffs** — `World.cs` (`_diffs`, `_harvestStrikes` are the only
  mutable world state), `WorldStore.cs` (postgres `tile_diff`/`structure`
  tables, replayed at startup). Confirmed correct server-side. The client half
  of this contract was broken until tonight — see LOG.md.
- **Character in gateway, not world-server** — `Player.ToCharacterState()` /
  `LoadCharacter()`, gateway's `character` table. Held.
- **One shared sim library** — `sim-core` has terrain, movement, harvest,
  craft, survival, placement rules; both client (`World3D`, `PlayerBody`) and
  world-server (`Program.cs`, `Player.cs`) reference it, nothing duplicated
  that I found.
- **Determinism** — `Noise.Hash` used throughout `sim-core` for terrain and
  foliage jitter alike; `DeterminismTests.cs` covers it. Held.
- **Interest management** — **not held**. `PlayerStates` and inventory/stats
  broadcasts go to every connected peer on the world-server regardless of
  distance. See BACKLOG.md; not fixed tonight (see LOG.md for why it wasn't
  tonight's pick over the diff-fetch bug).

## Known rough edges (not fixed tonight, not hidden either)

- **World3D.Build() only ever builds a fixed radius around world-origin
  (0,0)**, not around the player, and there is no re-request as the player
  moves. The `Radius` doc comment already calls this "this first slice" — it's
  acknowledged prototype scope, not a regression. Real streaming (build/free
  chunks as the player crosses chunk boundaries) is the natural next step
  once persistent-world play matters more than a single starting island.
- **No automated test touches netcode, world-server, or the gateway.**
  `tests/sim-core.tests` only exercises `sim-core`. Tonight's fix lives in
  Godot client code, which has no test harness in this repo at all (no
  headless Godot test runner configured). I did not stand one up tonight —
  it's a bigger, separate investment and didn't belong bundled into a bug fix.
  Flagged in BACKLOG.md.
- **Ground mesh colour doesn't follow diffs.** `TerrainMesher.Build` colours
  each tile from `terrain.TileAt` (the pristine seed), never from the diff
  overlay. A felled tree's tile now correctly has no foliage (after tonight's
  fix) and is correctly non-harvestable server-side, but the ground underneath
  still renders with Forest-biome vertex colour instead of switching to
  whatever `HarvestRules.Becomes` produced. Cosmetic only — pre-existing even
  for diffs applied live during a session, not something tonight's fix changed
  the status of. Logged to backlog.
- **`SurvivalHud.cs` (940 lines) and `World3D.cs` (now ~800 lines)** are the
  two largest files in the client. Neither is a tangle of unrelated systems —
  `SurvivalHud` is UI construction (meters, hotbar, action sheet, tabs,
  inventory grid, travel menu) and `World3D` is world-build + input + render
  glue — but both are past the 400-line god-script threshold and would
  benefit from splitting into smaller Nodes/components. Neither scored high
  enough to beat the diff-fetch bug tonight.
- **`apps/world-server/Program.cs` is 468 lines of top-level statements**,
  including a ~200-line `switch` on `MessageId` inline in the network receive
  callback. Functionally fine (each case is small and delegates to `Player`/
  `World`), but as more message types are added this file will want to become
  a dispatcher class per message, not more `case` arms.
- **Bandwidth**: `PlayerStates` per tick is `2 + 20*N` bytes broadcast to `N`
  peers, 15×/second, unreliable. At today's expected small per-world player
  counts this is not urgent (see BACKLOG.md for the actual math), but it is a
  real, explicit violation of invariant #6 and should be justified in
  `docs/architecture.md` if it's ever deliberately deferred rather than just
  quietly outgrown.

## Target frameworks (confirmed against .csproj files)

Matches `CLAUDE.md` exactly: `sim-core`, `shared-proto`, `client` → net8.0;
`world-server`, `gateway`, `sim-core.tests` → net10.0.
