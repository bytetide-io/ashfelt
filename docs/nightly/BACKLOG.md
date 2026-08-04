# Nightly backlog

Ranked by `Severity (1–5) × Blast radius (1–5)`, highest first. Anything ≥ 15,
or ≥ 9 in the multiplayer-correctness category specifically, is a "should have
been fixed already" — when more than one clears that bar on the same night,
only one gets fixed (see `LOG.md` for that night's reasoning) and the rest
stay here.

## Fixed

- ~~Client never fetched the server's persisted tile diffs before building
  terrain~~ — **25 (5×5), multiplayer-correctness.** Fixed 2026-08-04, see
  LOG.md. Left here struck through rather than deleted so the score history
  is visible.

## Open

### 1. No interest management on `PlayerStates` / broadcast traffic — 12 (3×4)

`apps/world-server/Program.cs`, the tick loop's `Broadcast(writer, method:
DeliveryMethod.Unreliable)` call for `PlayerStates`. Every connected player's
position is sent to every other connected player, every tick, regardless of
distance — a direct violation of `CLAUDE.md` invariant #6 ("a client only
ever receives entities/chunks near it").

Packet size is `2 + 20*N` bytes; sent to `N` peers at `Tuning.TicksPerSecond`
(15 Hz), so **per-player download is `(2 + 20N) × 15` bytes/sec**:

| players in world | per-player B/s |
|---:|---:|
| 10 | ~3 KB/s |
| 50 | ~15 KB/s |
| 100 | ~30 KB/s |
| 200 | ~60 KB/s |

Not an active problem at the player counts this prototype has ever run with,
which is why it wasn't tonight's pick over the diff-fetch bug (that one was
actively wrong for every player, every session, tonight; this one is
forward-looking debt). It stops being fine well before 200 concurrent players
in one world-server, and it's an explicit, named invariant violation, so
whoever pulls this next should either fix it (grid/radius-based interest
filtering keyed off the same tile coordinates harvesting already uses) or
write down in `docs/architecture.md` why it's being deliberately deferred, per
the "explicit decision recorded in docs/" rule for breaking a load-bearing
invariant.

### 2. No automated test coverage for netcode, world-server, or gateway — 12 (3×4)

`tests/sim-core.tests` is the only test project in the repo and only
exercises `packages/sim-core`. Nothing catches a protocol regression, a
world-server message-handling bug, or a gateway voyage-ticket race by running
the actual server/client code — only by manual play. Tonight's fix (client
chunk-fetch-before-build) has zero test coverage as a result; I verified it by
tracing types and call sites, not by running it. A `world-server` integration
test harness (spin up the server in-process, connect a fake LiteNetLib
client, assert on wire messages) would be the highest-leverage single
addition here, since it's the layer with the most invariants and the least
coverage.

### 3. Two files past the god-script line (400 lines) — 6 (2×3)

- `apps/client/scripts/world3d/SurvivalHud.cs` — 940 lines. All UI
  construction (meters, hotbar, action sheet, tabs, inventory grid, travel
  menu); no tangle of unrelated systems, just a lot of one system. Candidate
  split: one Node/scene per tab panel, hotbar, and gather-prompt, composed
  by a slimmer `SurvivalHud`.
- `apps/client/scripts/world3d/World3D.cs` — grew to ~800 lines tonight
  (chunk-fetch gating added ~65 lines). Candidate split: pull foliage
  building (`CollectTrees`/`CollectShrubs`/`CollectBerryBushes`/
  `BuildFoliage`/`RegisterFoliage`) into a `FoliageBuilder` the world hands
  its terrain and radius to.
- `apps/world-server/Program.cs` — 468 lines of top-level statements
  including a ~200-line `switch` on `MessageId` inline in the network
  receive callback. Each case is small and delegates correctly; it's the
  *file* that's grown large, not any one responsibility. Splitting into a
  per-message dispatcher class is the natural next step once a couple more
  message types land.

### 4. Ground mesh colour doesn't follow tile diffs — 4 (2×2)

`apps/client/scripts/world3d/TerrainMesher.cs` colours every tile from
`terrain.TileAt` — the pristine seed — never from the diff overlay. After
tonight's fix, foliage correctly disappears from an already-harvested tile,
but the ground colour under it still reads as Forest/Shrub/BerryBush biome
instead of whatever `HarvestRules.Becomes` produced. Purely cosmetic; was
already true for diffs applied live during a session (this isn't a new gap
tonight's change introduced). Fix would mean `TerrainMesher.Build` taking a
tile-lookup delegate instead of the raw `TerrainGenerator`.

### 5. World only ever builds a fixed radius around the world's origin — not scored, acknowledged scope

`World3D.Build()`'s radius is centred on `(0,0)`, never on the player, and
nothing re-requests chunks as the player walks away from spawn. The `Radius`
field's own doc comment calls this "this first slice" — it's known,
pre-alpha scope, not a regression. Real chunk streaming (build the chunks
newly in range, free the ones that fell out, as the player crosses chunk
boundaries) is the natural next feature once exploring past the starting
island matters. Left unscored because it's a documented feature gap, not a
newly discovered defect.
