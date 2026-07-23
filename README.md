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

- **Left thumb-stick** — move (bottom-left). **Jump** button, bottom-right.
- **Drag** anywhere else to pan the camera.
- **Tap** a tree, rock or shrub within reach to gather wood, stone or fiber.
- **Actions** button (bottom-right) opens the crafting/building sheet; the stick
  hides while it is open. Craft and Build are separate tabs.

WASD/arrows and the mouse work in the editor for desktop testing.

## Status

**Phase 0 complete** — monorepo, deterministic terrain generator, UDP
world-server, Postgres schema, CI.

**Phase 1 complete** — the vertical slice runs end to end:

- server-authoritative movement at 15 Hz, with client-side prediction and
  reconciliation
- multiple players on one world, seeing each other move
- harvest a tree → receive wood → tile becomes grass for everyone
- diffs persist to Postgres and are replayed on restart

**Phase 2 complete** — the survival loop plays end to end:

- **tap to harvest** in the 3D client: tap a tree, rock or shrub within reach →
  the server authorises it → the tree/shrub vanishes and you receive wood, stone
  or fiber (`Shrub` tiles scattered through grassland are the fiber source)
- deterministic **crafting** (`sim-core/CraftingRules`): recipes turn harvested
  wood/stone/fiber into planks, tools, rope, walls and a campfire; the server
  authorises each craft, the client shows a touch crafting panel
- **building placement** (`sim-core/PlacementRules`, `structure` table): spend a
  Wall or Campfire from inventory to place it in front of you; validated,
  persisted, and backfilled to every joining player
- **survival meters** (`sim-core/SurvivalRules`): hunger drains over time and,
  once empty, health decays; stamina regenerates — integer-deterministic so the
  client can predict what the server holds
- a shared **day/night clock** (`sim-core/WorldClock`) drives the sky; the
  server broadcasts survival meters + time-of-day, the client renders bars and
  moves the sun

**Phase 3 in progress** — persistent characters and voyages:

- **characters persist** (device-UUID model): the client stores a UUID, the
  gateway owns the character (inventory + stats) in Postgres, and the
  world-server loads it on join and saves it on leave — inventory and survival
  now survive a reconnect, per invariant #3. See `apps/gateway/API.md`.

Still open in Phase 3: **voyage transfer** between world-servers (instant v1,
see `docs/voyage-transfer.md`). Then Phase 4: item icons, audio, balancing and a
mobile UI pass.

See `docs/architecture.md` before adding anything; the invariants there
(server-authoritative, seed+diffs, one shared sim library) are load-bearing.
