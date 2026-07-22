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

## Status

Phase 0 complete: monorepo scaffolded, deterministic terrain generator with
tests, world-server serving chunks over UDP, client rendering them, Postgres
schema and CI in place. Next is Phase 1 — movement, chopping, and persistence
of the first diff.

See `docs/architecture.md` before adding anything; the invariants there
(server-authoritative, seed+diffs, one shared sim library) are load-bearing.
