# Ashfall — architecture map (nightly baseline)

Honest snapshot as of 2026-08-18, written for whoever runs the next nightly
session. This is a map of what exists and where the debt is, not a design
doc — see `docs/architecture.md` and `docs/voyage-transfer.md` for the
decided invariants.

## Layout and size

```
apps/client         Godot 4 client (C#, net8.0)     ~3,000 lines
apps/world-server    UDP authoritative server (net10.0)  ~840 lines
apps/gateway         Accounts/characters/voyages (net10.0) ~210 lines
packages/sim-core    Shared deterministic rules (net8.0)   ~1,000 lines
packages/shared-proto Wire messages + tuning (net8.0)       ~250 lines
tests/sim-core.tests xunit, 88 tests                        ~1,300 lines
```

Total ~6.7k lines of C#. Small enough that one person can hold the whole
system in their head — a real asset; don't let god-scripts erode that.

## Runtime shape

```
Godot client ──UDP (LiteNetLib)──▶ world-server ──REST──▶ gateway ──▶ Postgres
                                        │                      │
                                   Postgres (diffs,        (characters,
                                   structures)               voyage tickets)
```

One world-server process per region (`ASHFALL_WORLD_ID`), authoritative for
movement, harvesting, crafting, placement and survival meters. The gateway
owns character identity and cross-world voyage handoff. No service currently
supervises or restarts a world-server; `docker-compose` runs one instance.

## Server tick loop (`apps/world-server/Program.cs`)

Top-level-statements file, 468 lines. One `while` loop at `Tuning.TicksPerSecond`
(15 Hz): poll network events → advance survival meters → broadcast player
positions (unreliable) → flush dirty inventories (reliable) → heartbeat stats
every 2s. Message handling is a single `switch` over `MessageId` inline in the
network-receive callback. This file is doing three jobs (connection lifecycle,
per-message request handling, tick loop) in one script — see backlog.

## Client-authoritative vs server-authoritative, concretely

- **Client-authoritative (predicted, server-corrected):** movement/physics.
  `MovementRules.Check` (sim-core) bounds speed and terrain clipping; a
  violation snaps the client back via `Correction`.
- **Server-authoritative outright:** harvesting, crafting, placement, eating,
  inventory, survival meters, voyages. The client only ever sends a
  `*Request` message; `sim-core` rules decide the outcome and the server
  applies it. This is the correct split per `docs/architecture.md` invariant
  #1 and matches what movement-vs-everything-else should look like.

## Anti-spam / rate-limiting surface (as of tonight)

| Request        | Reach-checked | Time-paced | Inventory-bounded |
|-----------------|:---:|:---:|:---:|
| `ClientState` (move) | — | ✅ `MovementRules` | — |
| `ChopRequest`   | ✅ | ✅ **added tonight** (`HarvestPacing`) | — |
| `CraftRequest`  | — | ❌ | ✅ (inputs consumed) |
| `PlaceRequest`  | ✅ | ❌ | ✅ (one item spent) |
| `EatRequest`    | — | ❌ | ✅ (one item spent) |
| `RequestChunk`  | — | ❌ | — (cheap to regenerate) |

`ChopRequest` had none of the three until tonight's fix — see `LOG.md`. The
other unpaced requests are lower risk because inventory bounds the abuse (see
`BACKLOG.md`), but none of them have an explicit floor either.

## Persistence

- **World state**: seed + diffs only (`tile_diff`, `structure` tables),
  replayed on `WorldStore.OpenAsync`. No schema version column on either
  table — see backlog.
- **Character state**: `character` table in the gateway DB, JSONB inventory,
  keyed by client-generated device UUID. No auth beyond possessing the UUID.
- **Voyage tickets**: `voyage_ticket`, single-use, TTL 60s, self-healing via
  `ReclaimExpiredTicketAsync` on both character-load and claim-attempt paths.
  This is the one place with real replay/race protection (`DELETE ...
  RETURNING` to make claim atomic and single-use) — good precedent to copy
  when hardening the request handlers above.

## Determinism

`sim-core` is integer-hashed (`Noise.Hash`), no platform RNG, no float
accumulation across frames. `tests/sim-core.tests/DeterminismTests.cs` pins
this. Respect this when adding anything to `TerrainGenerator` or the crafting
tables — see `docs/architecture.md` "Generation changes on record" for the
pattern to follow (log the change, don't fix the test).

## Known debt (files, not yet scored — see BACKLOG.md for scored items)

- `apps/client/scripts/world3d/SurvivalHud.cs` — 940 lines. Owns HUD layout,
  meters, hotbar, the craft/build/items/travel menu tabs, and input for all
  of it. A god-script by the audit's own >400-line bar.
- `apps/client/scripts/world3d/World3D.cs` — 735 lines. Owns terrain mesh
  build, foliage MultiMesh instancing, the gather reticle, tap input, and
  wiring every `WorldConnection` event to a visual effect.
- `apps/world-server/Program.cs` — 468 lines, all message handling inline.
  Not yet at the multi-system-coupling problem `SurvivalHud`/`World3D` have
  (it's one system, network I/O, doing one job per case), but it's the
  obvious next split if more message types land: request handlers could each
  become a method or a small handler type instead of growing the switch.
- `apps/client/scripts/WorldConnection.cs` — 421 lines. Same shape as
  `Program.cs`'s switch, mirrored on the client (event dispatch instead of
  handler logic). Grows in lockstep with the protocol; fine for now.

None of these were god-scripts in the "coupled to >3 unrelated systems via
direct node paths" sense the audit prompt flags — no `get_node("../../X")`
smells were found. They're long because one file is genuinely one system's
whole client-side surface (HUD, or world rendering). Worth a decomposition
pass before they grow further, but none scored high enough to bump tonight's
harvesting fix.

## Test coverage

`tests/sim-core.tests` (88 tests) covers every `sim-core` rule: terrain
determinism, movement, harvest, crafting, placement, survival, item catalog,
and — as of tonight — harvest pacing. There is **no test project for
`apps/world-server` or `apps/gateway`** — the network/DB wiring is untested
except by hand or the ad-hoc harness described in tonight's `LOG.md` entry.
That's a real gap: the voyage claim race, the character load/save path, and
the tick loop's broadcast logic are all exercised only by manual testing.
