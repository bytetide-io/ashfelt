# Codebase tour

A newcomer's map of the repository: what each piece is, how they fit together,
and **where to look when you want to change X**. For the *why* behind the
structure, read [`architecture.md`](architecture.md); this page is about finding
your way around.

## The big picture

Ashfall is a monorepo of C# projects plus a Godot client. Three runnable apps
sit on top of two shared libraries:

```
             ┌─────────────────────────── shared ───────────────────────────┐
             │   packages/sim-core        packages/shared-proto              │
             │   (rules, terrain,         (wire message layouts)             │
             │    crafting, survival)                                        │
             └───────▲───────────────▲──────────────▲───────────────────────┘
                     │               │              │
        apps/client ─┘   apps/world-server ─┘  apps/gateway ─┘
        (Godot, C#)      (authoritative sim)   (accounts, routing)
             │                   │                    │
             │                   └── Postgres ◀───────┘
             └──── UDP ──► world-server        (world diffs, characters)
```

- The **client** predicts and renders; it never decides anything that matters.
- The **world-server** is the authority for one region's world state.
- The **gateway** owns accounts/characters and routes players between
  world-servers.
- **`sim-core`** holds every rule both client and server must agree on.
- **`shared-proto`** holds every message shape both sides must serialize
  identically.

The five [invariants](../CONTRIBUTING.md#load-bearing-invariants-breaking-these-needs-a-recorded-decision)
explain why the lines are drawn exactly here.

## Directory map

```
apps/
  client/            Godot 4 mobile client (C#)
  world-server/      Headless authoritative server; one instance = one region
  gateway/           Auth, character storage, routing between world-servers
packages/
  sim-core/          Shared deterministic simulation (terrain, tile rules, crafting)
  shared-proto/      Wire message definitions
tests/
  sim-core.tests/    The test suite that gates every commit
infra/
  docker/            docker-compose for local dev (Postgres + world-server)
  migrations/        SQL schema, applied in numeric order
tools/               Dev scripts (e.g. placeholder-art generator)
docs/                Architecture, roadmap, this tour, voyage transfer
```

## `packages/sim-core` — the shared brain

If a rule must be identical on client and server, it lives here. This is the
**highest-leverage** part of the codebase and the one with the strictest rules
(determinism — see below).

| File | What it owns |
|---|---|
| `TerrainGenerator.cs` | Procedural terrain: `HeightAt` (elevation field) and `TileAt` (tile classification) |
| `Noise.cs` | `Noise.Hash` — the integer hash **all** randomness must go through |
| `TileType.cs` | The tile enum (grass, water, tree, rock, shrub, berry bush, …) |
| `World.cs` | World-space helpers (e.g. `HasWarmthNear`) |
| `ItemCatalog.cs` | The data-driven `ItemDef` registry — one row per item |
| `HarvestRules.cs` | What a node yields, tool bonuses, hits-to-fell — a lookup over the catalog |
| `CraftingRules.cs` | Recipes (ordered, deterministic) |
| `PlacementRules.cs` | What can be placed where |
| `SurvivalRules.cs` | Hunger / stamina / health / warmth math (integer, predictable) |
| `MovementRules.cs` | The speed/height bounds the server validates client movement against |
| `WorldClock.cs` | The shared day/night clock |

**Determinism is a hard contract here.** No platform RNG, no `float`
accumulation across frames, no `DateTime.Now`, no dictionary iteration order.
See the [determinism section](../CONTRIBUTING.md#the-determinism-contract).

## `packages/shared-proto` — the wire contract

| File | What it owns |
|---|---|
| `Protocol.cs` | `MessageId` enum, `ProtocolVersion`, message layouts |
| `Character.cs` | The character shape passed between gateway and world-server |

Both the client and the world-server hand-serialize these in matching order. If
you change a message, change it here, bump `ProtocolVersion` if the wire layout
moved, and update both readers/writers **and** the docs.

## `apps/world-server` — the authority

Headless. One instance owns one region. It runs the survival tick, authorises
every client request against `sim-core`, and persists diffs.

| File | What it owns |
|---|---|
| `Program.cs` | The UDP loop, tick, and message dispatch |
| `WorldStore.cs` | Seed + diffs persistence (never full chunks — invariant #2) |
| `Player.cs` | Per-connected-player server state |
| `GatewayClient.cs` | Talking to the gateway (character load/save, voyage handoff) |

## `apps/gateway` — accounts and routing

An HTTP service. Owns characters (inventory + stats) in Postgres and routes
players between worlds.

| File | What it owns |
|---|---|
| `Program.cs` | The HTTP endpoints |
| `API.md` | **The API reference — read this** before changing an endpoint |

## `apps/client` — the Godot client

C# scripts under `apps/client/scripts/`. It predicts movement, renders the 3D
world, and draws the touch UI. It decides nothing authoritative.

| Area | Where |
|---|---|
| Server connection | `scripts/WorldConnection.cs` |
| 3D world & main loop | `scripts/world3d/World3D.cs` |
| Terrain mesh from `sim-core` heights | `scripts/world3d/TerrainMesher.cs` |
| Surface pixel textures | `scripts/world3d/PixelTextures.cs` |
| Local player / camera / joystick | `scripts/world3d/PlayerBody.cs`, `OrbitCamera.cs`, `VirtualJoystick.cs` |
| Other players | `scripts/world3d/RemotePlayers.cs` |
| Survival HUD | `scripts/world3d/SurvivalHud.cs` |
| Design system (palette, fonts, controls) | `scripts/ui/DesignSystem.cs` |
| Generated icons (no image files) | `scripts/ui/PixelIcons.cs` |
| Title screen (boot scene) | `scripts/ui/TitleScreen.cs`, `scenes/Title.tscn` |

The client's visual approach (low-resolution SubViewport render, surface-anchored
pixel texels, the Ink/Ember design system) is explained in
[`architecture.md`](architecture.md#presentation-the-ashfall-design-system).

## "I want to change X — where do I start?"

| I want to… | Start in | Also update |
|---|---|---|
| Add a gatherable item / food | `sim-core/ItemCatalog.cs` + `HarvestRules.cs` | a test; `ASSETS.md` if it needs art |
| Add a crafting recipe | `sim-core/CraftingRules.cs` | a test |
| Add a placeable structure | `sim-core/PlacementRules.cs` | a test |
| Change survival balance (drain rates, yields) | `sim-core/SurvivalRules.cs` (and any `Tuning` config) | a test |
| Change terrain generation | `sim-core/TerrainGenerator.cs` | **determinism decision** in `architecture.md` |
| Add or change a network message | `shared-proto/Protocol.cs` | both client + server serializers; docs |
| Change how the server validates a request | `apps/world-server/Program.cs` (+ the matching `sim-core` rule) | — |
| Change a gateway endpoint | `apps/gateway/Program.cs` | `apps/gateway/API.md` |
| Change client UI / controls | `apps/client/scripts/...` | README controls section |
| Change art / icons | see `ASSETS.md` | `ASSETS.md` (licence!) |

When in doubt, grep for an existing example of the thing you're adding — the
codebase is small and consistent, and copying the nearest neighbour is usually
the right instinct.

## Where the docs live

- [`architecture.md`](architecture.md) — the shape and the invariants (source of
  truth).
- [`gameplay-roadmap.md`](gameplay-roadmap.md) — what to build next and why.
- [`voyage-transfer.md`](voyage-transfer.md) — moving a character between worlds.
- [`voxel-terrain.md`](voxel-terrain.md) — terrain/rendering notes.
- [`../ASSETS.md`](../ASSETS.md) — asset pipeline and licensing.
- [`../apps/gateway/API.md`](../apps/gateway/API.md) — gateway HTTP API.

A full index is in [`docs/README.md`](README.md).
