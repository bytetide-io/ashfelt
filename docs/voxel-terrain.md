# Ashfall — cube terrain & digging (design record)

Status: **proposed**, not yet implemented. Records the decision to move terrain
from a continuous heightfield to a **column-of-cubes** model, so the world can be
dug, moved and rebuilt one 1×1 cube at a time. Companion to `architecture.md`
(owns the invariants) and `gameplay-roadmap.md` (owns what/why). Read those first.

## Why this exists

The vision: everything earthen is built of **1×1×1 cubes**. You dig the ground,
move dirt, and find resources (ore) by digging down. Organic matter — trees,
bushes — is *not* cubes; it grows on top of the cube surface and is harvested,
not dug.

Today terrain is a **continuous heightfield**: `TerrainGenerator.HeightAt` is one
smooth elevation field driving both the mesh and the tile classification. That is
the opposite data model from cubes, and the naive fix (full sparse 3D voxels à la
Minecraft) is the most expensive thing we could put on a mobile GPU *and* on a
seed-and-diffs server. This record picks the middle path that keeps every
invariant.

## Decision: column voxels, not free 3D voxels

Keep the existing tile grid. Quantize the vertical axis into integer cube layers.
Each `(x, y)` tile becomes a **column**: a surface height plus, below it, a stack
of cube materials that are **derived from the seed, not stored**.

- **Surface = quantized `HeightAt`.** Round the continuous height to integer
  cube-tops. The mesh becomes cubes, which suits the low-res surface-texel pixel
  look better than smooth terrain did.
- **What's underground is deterministic.** A new `MaterialAt(x, y, z)` classifies
  every cube below the surface from `Noise.Hash`: topsoil → dirt → stone → ore
  veins deeper down. "Find resources by digging" is therefore deterministic and
  testable — same seed, same vein, same cube — with no RNG, honouring the
  determinism contract.
- **A dig is one diff.** Removing the top solid cube of a column is a single diff
  entry. Because every *unedited* cube regenerates from the seed, storage stays
  **seed + sparse diffs** (invariant #2). No column is ever persisted whole.

### Why columns before true 3D voxels

Column voxels give: dig down, terraform, stack, place cubes, reveal ore — without
overhangs, floating islands or cave ceilings. That is ~90% of the fantasy for a
fraction of the cost: a column is one surface height + a short diff list, not a 3D
array. Real 3D carving (caves, roofed tunnels) is a **later phase**, scoped only
where diffs are dense, riding the same diff path. We do not build sparse-voxel
infrastructure until digging-straight-down proves fun.

## The organic distinction is a rule, not just art

- **Cubes = mineral / earth.** Dug and placed. Obey **conservation of mass**:
  digging yields a `Dirt`/`Stone`/ore *item*; placing it raises a column
  elsewhere. Terraforming is moving matter, never creating it.
- **Organic = props on the cube surface.** Trees, shrubs, berry bushes
  (`Forest`, `Shrub`, `BerryBush`) stay anchored to a cube top and are harvested
  by the existing `HarvestRules`, not dug. Wood regrows; stone is
  finite-until-you-move-it. Two resource philosophies the player learns to read.

## Guardrails (realistic *and* fun)

1. **Depth gates tools.** Topsoil by hand, stone needs a pickaxe, deep ore needs
   a better one — encoded as `ToolClass`/tier on the material def, reusing the
   harvest tool path. This is what finally makes tools *for* something.
2. **Material behaviour is data.** Each cube material is an `ItemDef` in
   `ItemCatalog`: dig time, required tool class/tier, dropped item, placeable?.
   A new ore is a couple of rows, not a new `switch`.
3. **No cave-ins in v1.** Structural support / collapse is heavy and frustrating
   on mobile; column voxels sidestep it (no unsupported ceilings). Revisit only
   with true 3D voxels.
4. **Diffs ride interest management.** Edited columns stream to nearby clients
   through the path players already use (invariant #5); only edited chunks re-mesh
   (greedy meshing).

## Concrete sim-core shape

Grounded in the current types (`TerrainGenerator`, `World`, `TileType`,
`ItemId`/`ItemCatalog`). Sketch, not final signatures.

### 1. Cube material — a new deterministic field

```csharp
public enum CubeMaterial : byte
{
    Air = 0,      // dug out, or above the surface
    Topsoil = 1,  // grass-capped dirt; hand-diggable
    Dirt = 2,
    Sand = 3,
    Stone = 4,    // needs a pickaxe
    // ore veins live in the stone band, keyed off Noise.Hash
    CopperOre = 5,
    IronOre = 6,
}
```

```csharp
// TerrainGenerator: the underground counterpart to HeightAt.
// z is an integer cube layer; SurfaceCubeTop(x,y) is the highest solid layer.

public int SurfaceCubeTop(int wx, int wy) =>
    (int)Math.Floor(HeightAt(wx * TileMetres, wy * TileMetres) / CubeMetres);

public CubeMaterial MaterialAt(int wx, int wy, int z)
{
    int top = SurfaceCubeTop(wx, wy);
    if (z > top) return CubeMaterial.Air;

    int depth = top - z;                       // 0 = surface cube
    if (depth == 0) return SurfaceMaterial(wx, wy);   // topsoil/sand from biome
    if (depth < StoneDepth) return CubeMaterial.Dirt;

    // Ore only exists in the stone band, sparse and deterministic.
    var ore = OreAt(wx, wy, z);
    return ore != CubeMaterial.Air ? ore : CubeMaterial.Stone;
}

private CubeMaterial OreAt(int wx, int wy, int z)
{
    // 3D hash so veins vary with depth; deeper => rarer, richer.
    uint h = Noise.Hash3(wx, wy, z, _seed ^ OreSalt);
    if (z <= IronDepth && h % 100 < IronDensityPercent) return CubeMaterial.IronOre;
    if (h % 100 < CopperDensityPercent) return CubeMaterial.CopperOre;
    return CubeMaterial.Air;
}
```

`Noise.Hash3` is a 3D sibling of the existing `Noise.Hash` — one new deterministic
primitive, covered by a determinism test, not a change to the 2D one.

### 2. Dig diffs — extend `World`, keep the diff model

The surface tile grid stays for movement/harvest/biome. Digging adds a **second,
sparse diff map keyed by 3D cube coord**, storing only removed/added cubes:

```csharp
// removed cube -> Air; placed cube -> its material. Absent key => derive from seed.
private readonly ConcurrentDictionary<(int X, int Y, int Z), CubeMaterial> _cubeDiffs = new();

public CubeMaterial CubeAt(int wx, int wy, int z) =>
    _cubeDiffs.TryGetValue((wx, wy, z), out var m) ? m : _terrain.MaterialAt(wx, wy, z);

// Mirrors TryHarvest: validate reach + tool, apply diff in memory, caller persists.
public DigResult TryDig(int wx, int wy, int z, ToolClass heldTool, int heldTier)
{
    var result = DigRules.Evaluate(CubeAt(wx, wy, z), heldTool, heldTier);
    if (result.Allowed) _cubeDiffs[(wx, wy, z)] = CubeMaterial.Air;   // yields result.Drop
    return result;
}

public PlaceResult TryPlaceCube(int wx, int wy, int z, CubeMaterial material) { /* conservation of mass */ }
```

Persistence gets a `cube_diff` table alongside `tile_diff`/`structure`
(`world_id, x, y, z, material`). `LoadCubeDiff` replays it, exactly like
`LoadDiff`/`LoadStructure`.

### 3. Rules & catalog

- **`DigRules.Evaluate(CubeMaterial, ToolClass, tier)`** — a thin lookup over
  `ItemCatalog`, the digging sibling of `HarvestRules.Evaluate`: allowed?, drop
  item, dig time. No branches; reads the material's `ItemDef`.
- **`ItemCatalog`** gains a cube-material → `ItemDef` mapping (required tool
  class/tier, dropped `ItemId`, placeable-as-cube flag).

## Tests (determinism contract)

- `Noise.Hash3` matches a fixed golden set (new primitive, pinned like `Hash`).
- `MaterialAt` is stable for a fixed seed across a sampled 3D volume, and every
  returned `CubeMaterial` has an `ItemDef`.
- Ore density stays within expected bounds over a large sample (variety, not
  fixed positions — same style as the existing biome-variety tests).
- Conservation of mass: dig-then-place round-trips the item ledger.

## Generation-change note (for `architecture.md` when landed)

Quantizing the surface to cube-tops and introducing `MaterialAt` **changes what a
world looks like** — this is a generation change on record, acceptable pre-alpha
because no world has shipped. Add the entry to `architecture.md` §Determinism when
the first slice lands, same as the Shrub/BerryBush notes.

## Not doing (yet)

- True 3D sparse voxels (caves, overhangs, roofed tunnels).
- Structural support / cave-ins.
- Fluid simulation (water filling dug holes).
- Cube-level lighting/ambient occlusion beyond the existing flat-banded look.
```
