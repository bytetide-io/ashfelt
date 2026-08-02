# Ashfall — architecture notes (nightly)

First run of the nightly process; this is the initial honest map plus known
debt. Update this file as debt is fixed or newly found, don't just append.

## Layout (as of this run)

```
apps/client         Godot 4 mobile client (C#), net8.0, 3D projection over a
                    tile-grid sim
apps/world-server   Headless authoritative UDP server (LiteNetLib), net10.0,
                    one process per region ("world"), single-threaded tick
                    loop (server.PollEvents() + a fixed-rate sleep)
apps/gateway        ASP.NET Core minimal-API service: accounts/characters
                    (Postgres), voyage ticket coordination, static world
                    registry
packages/sim-core   Deterministic shared rules: terrain, movement bounds,
                    harvest/craft/placement/survival, day/night clock
packages/shared-proto Wire message ids + CharacterState DTO
infra/migrations    Plain numbered SQL files, applied by hand / compose
```

Total non-test C#: ~5.6k lines (Aug 2026). `World3D.cs` (735 lines) and
`SurvivalHud.cs` (940 lines) are the client's two large files — see Backlog.

## How a session actually works right now

1. Client connects (LiteNetLib, pre-shared key), sends `Hello` with proto
   version + a locally-generated device UUID + an optional voyage ticket.
2. World-server, on `Hello`: claims voyage ticket if present, then **claims
   and loads the character from the gateway** (`GET /characters/{id}?worldId=`)
   — this claim is new as of tonight's fix, see LOG.md.
3. Movement is client-simulated, server-validated against the same height
   field (`MovementRules.Check`, shared via sim-core) — no server physics
   engine.
4. Harvest/craft/place/eat are server-authoritative request/response over the
   same UDP channel; tile diffs and structures are the only persisted world
   state (seed + diffs, invariant #2).
5. On disconnect, the world-server snapshots the player's character state
   synchronously and saves it to the gateway **fire-and-forget** (so one slow
   HTTP write can't stall the other players' tick). Tonight's fix makes a
   same-character reconnect wait for its own pending save instead of racing it.
6. Voyages (moving between world-servers) are instant: release → gateway
   mints a ticket and clears ownership → target world claims it.

## Known debt (see BACKLOG.md for scored, actionable items)

- **Client god-scripts.** `World3D.cs` and `SurvivalHud.cs` mix net dispatch,
  input, rendering and game-state in one file each. Already called out in
  `docs/gameplay-roadmap.md` §3.4 — not re-scored here, just tracked.
- **No world-server/gateway test project.** `tests/sim-core.tests` covers the
  deterministic rule library well; nothing exercises the gateway's HTTP
  endpoints or the world-server's message handling (netcode, character
  ownership, voyage handshake) in CI. The ownership-claim logic fixed tonight
  is exactly the kind of thing this gap lets regress silently.
- **No world-server liveness/heartbeat.** Character ownership (`owner_world_id`)
  is now claimed on join and released on a graceful leave, but an unclean
  world-server crash (kill -9, OOM) never fires `PeerDisconnectedEvent`, so the
  character stays locked to a dead world-server until someone manually clears
  the column. Pre-alpha, single-operator, so low likelihood/blast right now —
  scored in BACKLOG.
- **Static world registry.** `gateway/Program.cs` hardcodes `continent-a` /
  `continent-b` addresses. Fine for two dev instances, won't survive real
  deployment. Already flagged as Phase 3+ in the code's own comment.
- **Gameplay content gaps.** `docs/gameplay-roadmap.md` is an existing, still
  largely-accurate assessment (no skills/progression, no creatures, tool
  tiers stop at one). Not re-litigated here — read that doc for the gameplay
  plan; this file is architecture only.
