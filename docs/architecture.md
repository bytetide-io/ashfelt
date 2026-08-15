# Ashfall architecture

## Shape

Many independent, bounded **world-servers** (one per region/continent), plus a
**gateway** that owns accounts and routes players between them. There is no
single seamless map, and there will not be one.

```
mobile client ──auth──▶ gateway ──▶ postgres (accounts, characters)
      │                    │
      │  connection info ◀─┘
      ▼
 world-server A (udp)      world-server B (udp)
      └── postgres (world diffs, structures) ──┘
```

## Projection: 3D

The game is third-person 3D with physics-based movement. The client renders
terrain as a mesh built from `sim-core` heights; the tile grid still exists
underneath as the unit of world data (chunks, diffs, biomes, harvesting), but
movement is no longer grid-constrained.

`TerrainGenerator.HeightAt` is the bridge: one continuous elevation field
drives both the surface classification and the mesh, so the two can never
disagree. It is in `sim-core` precisely because the server will need identical
heights for collision.

### Presentation: the Ashfall design system

**Decided.** All client visual style flows from one place, `scripts/ui/DesignSystem.cs`
— the Ink/Ember palette, the Silkscreen (display) + Pixelify Sans (body) fonts,
and the chunky pixel controls (flat fills, a hard 2px outline, a solid offset
"shadow" for depth). HUD and menus build from these tokens; nothing re-declares
a shade or a button box. Item/resource **icons are data, not files**: the pixel
grids from the design system's `PixelIcon` component are baked to nearest-filtered
textures at runtime by `scripts/ui/PixelIcons.cs`, keeping the mobile binary
free of per-icon PNGs and honouring the "use the design's assets, don't hand-roll
SVG" rule.

The "medium pixel-art, still 3D" look is done as a **low-resolution render**, not
a post-process — the distinction matters. The world renders into a `SubViewport`
sized down by `stretch_shrink` (with MSAA/AA off) and is nearest-upscaled by its
`SubViewportContainer`. Because geometry is rasterised natively at that low
resolution, the pixels are real and grid-aligned (authentic pixel art), and the
GPU shades roughly `shrink²` fewer fragments — so the effect *improves* mobile
framerate instead of costing it. A fullscreen shader that downsamples an already
full-res image was tried and rejected: it looks like low resolution rather than
pixel art, and it adds cost on top of full-res shading.

The pixels themselves live **on the surfaces**, not on the screen. Terrain and
foliage carry procedural, nearest-filtered pixel textures generated from
`Noise.Hash` (`World3D/PixelTextures.cs`) — ground grain, bark, leaf and berry
dapple — multiplied over each surface's colour. The terrain mesh bakes
**world-space UVs** (`TerrainMesher.Uv`) so the grain tiles seamlessly across
chunks and stays welded to the ground as the camera pans; that world-anchored
texel motion is what stops the look reading as a screen-space filter. A
fullscreen shader that downsamples an already full-res image was tried and
rejected for exactly that reason: with untextured surfaces it only blocks up
smooth shapes, which looks like low resolution rather than pixel art — and it
adds cost on top of full-res shading.

Flat toon-banded, specular-disabled materials (`World3D.FlatMaterial`) complete
the banded look. The HUD lives on a `CanvasLayer` *outside* the SubViewport so it
composites at full resolution and stays crisp; `World3D` reaches the HUD and its
status label by exported `NodePath` across the SubViewport boundary. Input is
routed and coordinate-remapped into the SubViewport by the container, so the
`GetViewport().GetCamera3D()` the gather reticle unprojects through resolves
against the SubViewport's camera as expected. A tap gathers the reticled node —
the nearest harvestable tile in reach, resolved each physics frame — rather than
raycasting the tapped pixel, so hitting a tree never demands pixel-accurate aim.

### Movement authority: the client simulates, the server validates

**Decided.** The client runs physics and reports where it ended up. The server
runs no physics engine; it checks each reported position against the height
field it already computes, and corrects anything implausible
(`MovementRules.Check` in sim-core, shared by both sides).

Why not run physics on the server: it costs an engine dependency and a second
implementation that will disagree with the first at the margins — and
disagreement surfaces as rubber-banding on slopes, exactly where players
notice. Bounding what physics can *possibly* produce is far cheaper and
rejects every cheat that matters: speed hacks, teleports, flight, and moving
through terrain all violate a speed bound or the height field.

What this does not catch: a client moving legally but in ways a human could
not, such as perfect aim-walking or subtly favourable collision resolution.
That is an accepted trade. If PvP ever makes it matter, a headless Godot
world-server sharing the client's physics engine is the upgrade path, and
nothing in the protocol has to change.

The rules live in `sim-core` so the client can check itself against the same
bounds before sending, and a legitimate player is never corrected.

## Invariants

1. **Server-authoritative.** The client predicts movement for responsiveness.
   The world-server is the only source of truth for position, inventory,
   building and combat. Client input is a request, never a state change.
2. **World storage = seed + diffs.** Full chunks are never persisted. Each
   world-server stores its procgen seed and only player-caused modifications,
   keyed by chunk coordinate (`tile_diff`, `structure`).
3. **Character is global, map is not.** Inventory, stats and skills live in the
   gateway database. World-servers hold world state only.
4. **One shared simulation library.** `packages/sim-core` defines terrain
   generation, tile rules and (later) crafting. Client and world-server both
   reference it. Logic is never duplicated across the boundary — if the client
   needs to predict, it calls `sim-core`.
5. **Interest management.** A world-server sends each client only the entities
   and chunks near them. **Implemented for `PlayerStates`** (`Player.IsWithinInterestOf`,
   world-server): each player's 15 Hz position snapshot is filtered to peers within
   `InterestRadiusChunks` (64m) instead of broadcasting everyone to everyone, which
   was unbounded O(playerCount²) bandwidth. **Not yet implemented** for
   `StructurePlaced`/`TileChanged` (still a flat broadcast to every connected
   player) or chunk delivery (client explicitly requests the chunks it wants, so
   there is no unsolicited push to bound) — see `docs/nightly/BACKLOG.md`.

## Determinism

`sim-core` uses integer hashing (`Noise.Hash`) rather than any platform RNG, so
the same seed and coordinate yield the same tile on every device. This is
covered by `tests/sim-core.tests/DeterminismTests.cs`; treat those tests as a
compatibility contract — changing generation changes every existing world.

### Generation changes on record

- **Shrub tile (pre-alpha).** A walkable `Shrub` tile was scattered into the
  open-grass band so fiber has a gather source (and rope becomes craftable).
  This alters `TerrainGenerator.TileAt` output, so worlds generated before it
  differ — acceptable because no world has shipped. The determinism tests still
  pass: they assert consistency and biome variety, not fixed tile values.
- **BerryBush tile (pre-alpha).** A walkable `BerryBush` tile was scattered into
  the open-grass band (rarer than shrubs, placed on grass the shrub pass left)
  so forageable food exists and the survival loop is winnable — you can eat.
  Same rationale and same test guarantees as the shrub change: it alters
  `TileAt` output but no world has shipped, and the determinism tests assert
  consistency and variety, not fixed values.

## Target frameworks

- `sim-core`, `shared-proto`, `client` → **net8.0** (Godot 4's runtime).
- `world-server`, `gateway` → **net10.0** (they never load into Godot).

A net10 server referencing net8 libraries is supported and intentional.
