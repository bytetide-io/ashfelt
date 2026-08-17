# Nightly backlog

Findings from the audit phase that scored below the fix-tonight threshold
(`Severity × Blast radius ≥ 15`, or any multiplayer-correctness finding
`≥ 9`), ranked by score. Pick the top unclaimed one first unless it's stale
(re-check the score — the codebase moves).

| # | Finding | Severity | Blast | Score | Category |
|---|---------|----------|-------|-------|----------|
| 1 | No automated tests for `world-server`/`gateway` | 4 | 4 | 16 | Testing |
| 2 | Interest management not implemented (flat broadcast) | 3 | 4 | 12 | Multiplayer/scaling |
| 3 | `World3D.cs` god script (735 lines, 5 concerns) | 3 | 4 | 12 | Maintainability |
| 4 | `SurvivalHud.cs` god script (940 lines, 49 methods, 5 concerns) | 3 | 4 | 12 | Maintainability |
| 5 | `world-server/Program.cs` growing (~550 lines after tonight) | 2 | 4 | 8 | Maintainability |
| 6 | `gateway`'s world registry is a hardcoded 2-entry dict | 2 | 3 | 6 | Architecture |

## 1. No automated tests for `world-server`/`gateway` (16)

Every `sim-core` rule has a test; nothing that touches the network loop,
Postgres persistence, or the gateway's voyage/character endpoints does.
Tonight's Finding 1 fix (async gateway calls off the packet loop) was
verified by hand — a real gateway + Postgres, a throwaway LiteNetLib test
client, and a black-hole TCP listener standing in for an unreachable gateway
— but that verification doesn't survive as a regression test. A future
night's fix to, say, `CraftRequest`'s dispatch could silently reintroduce a
blocking call and nothing would catch it.

Worth scoping as its own night: `world-server` runs entirely against
in-process LiteNetLib peers (client + server in the same test process, no
real sockets needed — LiteNetLib supports this) and an in-memory or
Testcontainers Postgres. Start with: join/Welcome round trip, harvest →
`TileChanged` → persists across a simulated restart, and a repro of
tonight's stall bug (point `GatewayClient` at a server that never responds,
assert other players still receive `PlayerStates` within one tick interval).

## 2. Interest management not implemented (12)

`docs/architecture.md` invariant #5 is written as decided, but every
`Broadcast()` in `world-server/Program.cs` sends to every connected peer.
Chunks are pulled on request (fine), but `PlayerStates`, `TileChanged`,
`HarvestProgress`, and `StructurePlaced` are flat broadcasts. At today's
player counts this is invisible; `PlayerStates` bandwidth is O(players²) and
will show up in a profiler once a world holds dozens of concurrent players.
Fixing it needs a design decision (chunk-radius-based interest sets keyed
off `Tuning.InterestRadiusChunks`, which already exists and is currently
unused for entity traffic) more than a quick patch — good candidate for a
dedicated night once player counts are actually a concern.

## 3–4. Client god scripts (12 each)

`World3D.cs` (terrain mesh, foliage, sky, tap-to-gather input, structure
rendering) and `SurvivalHud.cs` (meters + craft/build/items/travel tabs) each
mix well past the "~3 systems" guideline in CLAUDE.md. Not broken, not
urgent, but each new HUD tab or world-rendering feature added to these files
raises the cost of the next change. A split should follow the seams that
already exist in the code (e.g. `SurvivalHud`'s `CraftCard`/`Meter` nested
types suggest sub-components waiting to be pulled out) rather than an
abstract redesign.

## 5. `world-server/Program.cs` growing (8)

Top-level-statements script, ~550 lines after tonight's async refactor:
network dispatch, the tick loop, and two background-task orchestration
functions (`HandleHelloAsync`, `HandleReleaseAsync`) all in one file. Works
today; extracting a `NetworkHandlers` or `GameLoop` type would make it
testable in isolation (see Finding 1) but is a bigger, riskier change than
fits in one night — do it together with the testing work above, not before.

## 6. Gateway world registry is hardcoded (6)

`worlds` dict in `gateway/Program.cs` has two literal entries. Fine while
there are two dev worlds; needs to become live world-server registration
(a world-server announces itself to the gateway on boot) before a third
region can be added without a gateway code change + redeploy. Not urgent —
flagging so it's not a surprise when Phase 4 wants a third world.
