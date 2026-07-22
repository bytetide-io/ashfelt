# Ashfall

Open-source, mobile-only 2D pixel top-down survival game. Godot 4 client,
C# authoritative world-servers, many bounded worlds linked by ocean voyages.

## Layout

```
apps/client         Godot 4 mobile client (C#)
apps/world-server   Headless authoritative server; one instance = one region
apps/gateway        Auth, character storage, routing between world-servers
packages/sim-core   Shared deterministic simulation (terrain, tile rules)
packages/shared-proto  Wire message definitions
infra/docker        docker-compose for local dev
infra/migrations    SQL schema
docs/               architecture.md, voyage-transfer.md
```

## Requirements

- .NET SDK 10 (builds the net8.0 libraries too)
- .NET 8 **runtime** only if you want to run client assemblies outside Godot
- Godot 4.3+ **.NET/Mono** build, for the client
- Docker, for Postgres

## Run it

```bash
# tests
dotnet test tests/sim-core.tests

# world-server (defaults: udp/9050, seed 1337)
dotnet run --project apps/world-server

# postgres + world-server together
docker compose -f infra/docker/docker-compose.yml up

# client: open apps/client in the Godot .NET editor and run Main.tscn
```

The client connects to `127.0.0.1:9050` by default — change `Host`/`Port` on
the `WorldConnection` node for LAN or VPS testing.

## Controls

Drag anywhere to move, tap a tree or rock to harvest it. Arrow keys work in the
editor for desktop testing.

## Status

**Phase 0 complete** — monorepo, deterministic terrain generator, UDP
world-server, Postgres schema, CI.

**Phase 1 complete** — the vertical slice runs end to end:

- server-authoritative movement at 15 Hz, with client-side prediction and
  reconciliation
- multiple players on one world, seeing each other move
- harvest a tree → receive wood → tile becomes grass for everyone
- diffs persist to Postgres and are replayed on restart

Next is Phase 2: inventory schema, crafting, building placement, and a
day/night or hunger tick.

See `docs/architecture.md` before adding anything; the invariants there
(server-authoritative, seed+diffs, one shared sim library) are load-bearing.
