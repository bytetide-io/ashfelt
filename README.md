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
- **Tap** a tree, rock, shrub or berry bush within reach to gather wood, stone,
  fiber or berries.
- **Hotbar** (bottom-centre) mirrors what you carry: tap a placeable to build it
  in front of you, tap forage to eat it.
- **Menu** button (top-right, ☰) opens the crafting/building sheet; the stick
  hides while it is open. Craft, Build, Items and Travel are separate tabs. A
  **day/night chip** sits beside it, and the survival meters read top-left.
- **Items** tab lists what you carry; edible forage (berries) shows an **Eat**
  button that restores hunger.
- **Warmth** bar falls at night unless you stand near a lit **Campfire** (build
  one from the Build tab); let it hit zero and your health bleeds.
- Holding the right **tool** gathers more: an Axe boosts wood from trees, a
  Pickaxe boosts stone from rock. Bare hands still work, just for less.

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

- **tap to harvest** in the 3D client: tap gathers the reticled node — the
  nearest tree, rock or shrub in reach — and the server authorises it. Nodes have
  durability: a shrub or berry bush comes away in one tap, a tree takes four
  strikes and a rock five (a matching axe/pickaxe shaves a strike per tier), and
  the node visibly wears down each strike before it finally falls and yields its
  wood, stone or fiber (`Shrub` tiles scattered through grassland are the fiber
  source)
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

**Foundation + Phase A (in progress)** — see `docs/gameplay-roadmap.md`:

- a data-driven **item catalog** (`sim-core/ItemCatalog`): one `ItemDef` table
  is the single source of item behaviour (name, stack, category, food value,
  tool class, placeability). `HarvestRules` and `PlacementRules` are now lookups
  over the catalog, not hand-listed switches — adding content is adding data.
- a **food loop closes survival**: `BerryBush` tiles scattered in grassland are
  foraged for `Berry`, and an `EatRequest` restores hunger via the shared
  `SurvivalRules.Eat` — hunger is finally something a player can act against.
- **night is a threat and the campfire earns its keep**: a fourth survival meter,
  **warmth**, drains when a player is exposed at night and recovers in daylight
  or within a campfire's warmth radius (`ItemDef.WarmthRadiusMetres`,
  `World.HasWarmthNear`). An empty warmth meter bleeds health on top of any
  starvation — so surviving the night means gathering by day and sheltering by
  a fire after dark.

**Design system + pixel-art pass (in progress)** — the client now dresses in the
Ashfall design system (`scripts/ui/DesignSystem.cs`): the Ink/Ember palette,
Silkscreen + Pixelify Sans fonts, and the chunky pixel controls. Item and
resource **icons** are generated at runtime from the design's pixel-grid data
(`scripts/ui/PixelIcons.cs`) — no hand-authored image files. A new **title
screen** (`scenes/Title.tscn`) is the boot scene. The 3D world gets a
**medium pixel-art look** where the pixels live *on the surfaces*: terrain and
foliage carry procedural nearest-filtered pixel textures (ground grain, bark,
leaf, berry — `PixelTextures.cs`) mapped in world space, so the ground you walk on
and the trees are built of texels that stay welded to the geometry as you pan.
On top of that the world renders into a low-resolution `SubViewport`
(`stretch_shrink`, MSAA off) and is nearest-upscaled — which keeps the pixel size
coherent *and* shades far fewer fragments, so the effect *raises* framerate rather
than costing it. Flat toon-banded materials complete the look; the HUD renders
outside the SubViewport so it stays crisp. The Silkscreen/Pixelify fonts live in
`art/fonts/` and import on first editor open.

**Blueprint building (in progress)** — see `docs/building-blueprints.md`: a
player designs a structure from modular **pieces** (foundations, walls, doorways,
windows, pillars, roofs, in wood or stone) and commits it as a private
**buildground**. The design is a hologram only the owner sees; materials are
deposited into on-site storage and each piece is **built up strike by strike** in
support order — and a completed piece becomes public, so others watch the building
rise out of nothing while the owner sees the whole plan.

- a data-driven **`StructureCatalog`** in `sim-core` (piece kind × material →
  layer, cost, strikes, support, solid/shelter): adding a buildable piece is one
  row. Canonical **`PieceSlot`** so the wall shared by two cells is one slot;
  **`BuildingRules`** for support validation and the bill of materials; a
  **`BuildSite`** aggregate that stockpiles materials and raises pieces once their
  supports stand — the whole mechanic is deterministic and unit-tested.
- the world-server commits, supplies and builds sites, persists them
  (`blueprint` / `blueprint_piece` / `build_site_storage`), and streams pending
  pieces only to the owner but built pieces to everyone.
- the client has an **architect mode**: a translucent cursor ghost, tap-to-place
  with live validity (illegal ghosts glow red) and a running cost readout, commit,
  and buildground **deposit/build** actions when standing at an owned site.

A finished building now **matters for survival**:

- **shelter** — standing under a built roof keeps you warm at night, exactly as a
  lit campfire does (`BuildSite.HasBuiltRoofOver`), and a fiber **thatch roof**
  gives a first-night shelter before planks exist.
- **health regenerates** when you are both warm and well-fed
  (`SurvivalRules` `HealthRegenPerMinute` / `WellFedPoints`) — so food and shelter
  recover you, not merely delay death.
- **walls enclose** — obstacles are now solid. `MovementRules` (with
  `IMovementObstacles`) stops a player at trees, built walls and placed
  structures, server-authoritative and matched by the client's own colliders, so
  an enclosed, roofed hut is a real refuge.

Still open: a free-pan **architect camera** (v1 places pieces in front of the
player), multi-storey building, cooking, rock/water terrain collision, and
resource/material depth (ore, clay, refining stations).

See `docs/architecture.md` before adding anything; the invariants there
(server-authoritative, seed+diffs, one shared sim library) are load-bearing.
