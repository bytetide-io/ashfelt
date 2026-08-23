# Ashfall architecture map (nightly notes)

Honest snapshot as of the first nightly session. Not a substitute for
`docs/architecture.md` / `docs/voyage-transfer.md` (the decided source of
truth) — this is where nightly runs track what actually exists, what's
fragile, and what's still debt. Update it whenever a session's audit or
feature changes the shape of something below.

## Layout (as built, not just as documented)

```
apps/client         Godot 4 mobile client, C#, net8.0
apps/world-server   Headless authoritative UDP server, net10.0, one per region
apps/gateway        ASP.NET minimal API: accounts, character storage, voyage routing, net10.0
packages/sim-core   Shared deterministic rules: terrain, harvest, craft, place, fire,
                    survival, world clock — referenced by both client and server
packages/shared-proto  Wire message ids (MessageId), ItemId, Tuning constants
infra/migrations    Numbered SQL files, applied in order by Postgres's
                    docker-entrypoint-initdb.d on first container start —
                    NOT re-run automatically against an existing volume
infra/docker        docker-compose for local Postgres
tests/sim-core.tests  xUnit; the only automated test suite in the repo
```

There is **no test project for world-server, gateway, or the client** — every
automated test lives in `tests/sim-core.tests`, covering the shared rules
only. Netcode (message framing, the Hello/voyage handshake, tick-loop
broadcast cadence) and the gateway's SQL are exercised only by hand.

## Data flow, tick by tick (world-server)

`apps/world-server/Program.cs` is a single top-level-statements file: one
`NetManager` event loop from LiteNetLib, a `switch` over `MessageId` for
inbound requests, and a `while` tick loop at `Tuning.TicksPerSecond` (15 Hz)
that advances survival meters, drains campfire fuel, and broadcasts player
state + dirty inventories + a periodic stats heartbeat. All the same-tick
work happens on one thread — there is no lock contention to reason about,
but it also means a slow synchronous call (a gateway HTTP round-trip on
`Hello` or `RequestRelease`) stalls every player in the world for its
duration. That trade-off is deliberate and documented inline, not an
oversight.

`Player.cs` holds per-connection authoritative state (position, inventory,
survival meters) and the movement-acceptance logic
(`MovementRules.Check`, shared with the client for prediction).

`World.cs` holds the in-memory terrain diffs, harvest-strike progress, and
placed structures — the seed+diffs invariant lives here. `WorldStore.cs` is
the only thing that touches Postgres from the world-server; every write from
the tick loop is fire-and-forget (`_ = store.SaveXAsync(...)`) so a slow
database never stalls a packet.

## Gateway

`apps/gateway/Program.cs` is minimal-API endpoints only: character
load/save (`/characters/{id}`) and the two-step voyage handshake
(`POST /voyage`, `POST /voyage/claim`). The world registry (`/worlds`) is a
hardcoded `Dictionary` — Phase 3+ is expected to replace it with live
world-server registration; nothing dynamic exists yet. Voyage ownership
races are handled with SQL CTEs (atomic claim-or-409, atomic release), not
application-level locking — this is worth preserving as a pattern for any
future cross-world coordination.

## Client

`WorldConnection.cs` is the only netcode surface on the client: it owns the
LiteNetLib peer, the reconnect/retry loop, and every `MessageId` case as a
C# event. `World3D.cs` builds the 3D scene from the seed (terrain mesh,
foliage as `MultiMesh` instances for batching) and wires those events to
scene updates. `SurvivalHud.cs` builds the entire touch UI — meters, hotbar,
gather/feed reticle, and the full crafting/building/items/travel action
sheet — programmatically in code, with no `.tscn` scenes for any of it.

## Known debt (not fixed tonight — see BACKLOG.md for scores)

- `SurvivalHud.cs` (~940 lines) and `World3D.cs` (~735 lines) are the two
  largest client files and each is more of a "does everything for its
  corner of the game" script than a composition of small components,
  contrary to the "no god-scenes" code standard. Neither crossed tonight's
  mandatory-fix threshold (nothing in them is a multiplayer-correctness or
  severity-15 bug — it's a maintainability cost that shows up as this file
  being the one every future HUD/world feature has to edit).
- `structure.health` (`infra/migrations/001_init.sql`) has existed since the
  first migration and is never read or written by any code. Either wire it
  up (structure damage/decay) or drop it — right now it's schema that lies
  about what the game does.
- No test project touches world-server's message handling or the gateway's
  voyage SQL directly; both are only covered by `sim-core` unit tests for
  the rules they call into, plus manual play-testing per session.

## Netcode surface (for anyone about to add a message)

Every wire message is declared once in `packages/shared-proto/Protocol.cs`
(`MessageId` enum, doc comment states the exact layout) and read/written on
both sides by hand — there is no schema codegen. `ProtocolVersion.Current`
must be bumped whenever a message's layout changes; a mismatched peer is
rejected at `Hello`. Follow the existing request/response naming
(`XRequest` client→server, past-tense or noun server→client) and the
existing convention of refusing invalid requests silently (no ack, no
error) rather than disconnecting, except for a handshake-level violation
(bad protocol version, bad voyage ticket).
