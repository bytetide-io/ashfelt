using System.Collections.Concurrent;
using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>A structure a player placed on the world, at a tile coordinate.</summary>
public readonly record struct Structure(long Id, int TileX, int TileY, ItemId Kind);

/// <summary>
/// Generated terrain plus the player-caused diffs layered on top. Diffs and
/// placed structures are the only things ever persisted; everything else is
/// re-derived from the seed.
/// </summary>
public sealed class World
{
    private readonly TerrainGenerator _terrain;
    private readonly ConcurrentDictionary<(int X, int Y), TileType> _diffs = new();
    private readonly ConcurrentDictionary<(int X, int Y), int> _harvestStrikes = new();
    private readonly ConcurrentDictionary<(int X, int Y), Structure> _structures = new();
    private long _nextStructureId = 1;

    public World(uint seed) => _terrain = new TerrainGenerator(seed);

    public uint Seed => _terrain.Seed;
    public TerrainGenerator Terrain => _terrain;

    /// <summary>Effective tile: the diff if one exists, else generated terrain.</summary>
    public TileType TileAt(int wx, int wy) =>
        _diffs.TryGetValue((wx, wy), out var t) ? t : _terrain.TileAt(wx, wy);

    /// <summary>Applies a diff without validation. Used when replaying from storage.</summary>
    public void LoadDiff(int wx, int wy, TileType tile) => _diffs[(wx, wy)] = tile;

    public int DiffCount => _diffs.Count;

    public IReadOnlyCollection<KeyValuePair<(int X, int Y), TileType>> Diffs => _diffs.ToArray();

    /// <summary>A chunk with diffs applied, ready to send to a client.</summary>
    public Chunk GenerateChunk(ChunkCoord coord)
    {
        int size = TerrainGenerator.ChunkSize;
        var tiles = new TileType[size * size];
        int ox = coord.X * size, oy = coord.Y * size;
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                tiles[ly * size + lx] = TileAt(ox + lx, oy + ly);
        return new Chunk(coord, tiles);
    }

    /// <summary>
    /// The outcome of a single harvest strike. <c>Amount</c> of <c>Item</c> is
    /// yielded on every allowed strike; the node only becomes <c>Becomes</c> once
    /// <c>Felled</c>. <c>StrikesLeft</c> and <c>StrikesTotal</c> let a watcher show
    /// how far along the felling is.
    /// </summary>
    public readonly record struct HarvestStrike(
        bool Allowed, ItemId Item, int Amount, bool Felled, TileType Becomes, int StrikesLeft, int StrikesTotal);

    /// <summary>
    /// Applies one harvest strike to a world tile. Every allowed strike yields the
    /// node's amount; the tile only converts to <c>Becomes</c> — and a diff is
    /// recorded — on the strike that fells it. Partial progress lives only here in
    /// memory: it is never a diff, so the seed+diffs invariant is untouched and a
    /// half-chopped tree stands whole again after a restart. When <c>Felled</c> is
    /// true the caller is responsible for persisting the diff.
    /// </summary>
    public HarvestStrike TryHarvest(int wx, int wy, ToolClass heldTool = ToolClass.None, int heldTier = 0)
    {
        var tile = TileAt(wx, wy);
        var yield = HarvestRules.Evaluate(tile, heldTool, heldTier);
        if (!yield.Allowed) return default;

        int total = HarvestRules.HitsToFell(tile, heldTool, heldTier);
        int struck = _harvestStrikes.GetValueOrDefault((wx, wy)) + 1;
        bool felled = struck >= total;

        if (felled)
        {
            _harvestStrikes.TryRemove((wx, wy), out _);
            _diffs[(wx, wy)] = yield.Becomes;
        }
        else
        {
            _harvestStrikes[(wx, wy)] = struck;
        }

        return new HarvestStrike(
            true, yield.Item, yield.Amount, felled, yield.Becomes, Math.Max(0, total - struck), total);
    }

    /// <summary>
    /// The outcome of a placement attempt. When <c>Allowed</c> is true the
    /// structure has been recorded in memory and the caller is responsible for
    /// persisting it; otherwise <c>Structure</c> is meaningless.
    /// </summary>
    public readonly record struct Placement(bool Allowed, Structure Structure);

    /// <summary>
    /// Attempts to place <paramref name="kind"/> at a world tile. Refuses —
    /// without throwing, mirroring <see cref="TryHarvest"/> — when the item is
    /// not placeable, the tile is unwalkable, or a structure already sits there.
    /// </summary>
    public Placement TryPlace(int tileX, int tileY, ItemId kind)
    {
        if (!PlacementRules.IsPlaceable(kind)) return default;
        if (!TerrainGenerator.IsWalkable(TileAt(tileX, tileY))) return default;
        if (_structures.ContainsKey((tileX, tileY))) return default;

        var structure = new Structure(_nextStructureId++, tileX, tileY, kind);
        _structures[(tileX, tileY)] = structure;
        return new Placement(true, structure);
    }

    /// <summary>Records a structure without validation. Used when replaying from storage.</summary>
    public void LoadStructure(Structure structure)
    {
        _structures[(structure.TileX, structure.TileY)] = structure;
        if (structure.Id >= _nextStructureId) _nextStructureId = structure.Id + 1;
    }

    /// <summary>Removes the structure at a tile, if any. Returns whether one was there.</summary>
    public bool RemoveStructure(int tileX, int tileY) => _structures.TryRemove((tileX, tileY), out _);

    /// <summary>
    /// True when <paramref name="position"/> lies within the warmth radius of any
    /// heat-providing structure (a lit campfire). Horizontal distance only, so a
    /// ledge above the fire still counts — mirrors how harvest reach is measured.
    /// This is what lets a player survive the night by sheltering near a fire.
    /// </summary>
    public bool HasWarmthNear(Vec3 position)
    {
        double metres = TerrainGenerator.TileMetres;
        foreach (var structure in _structures.Values)
        {
            if (!ItemCatalog.TryGet(structure.Kind, out var def) || def.WarmthRadiusMetres <= 0) continue;

            var centre = new Vec3((structure.TileX + 0.5) * metres, position.Y, (structure.TileY + 0.5) * metres);
            if (position.HorizontalDistanceTo(centre) <= def.WarmthRadiusMetres) return true;
        }
        return false;
    }

    public int StructureCount => _structures.Count;

    public IReadOnlyCollection<Structure> Structures => _structures.Values.ToArray();

    public static ChunkCoord ChunkOf(int wx, int wy) => new(
        (int)Math.Floor(wx / (double)TerrainGenerator.ChunkSize),
        (int)Math.Floor(wy / (double)TerrainGenerator.ChunkSize));
}
