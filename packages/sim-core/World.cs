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
    /// Attempts a harvest at a world tile. Returns the harvest result; when
    /// <c>Allowed</c> is true the diff has been applied in memory and the
    /// caller is responsible for persisting it.
    /// </summary>
    public HarvestRules.Harvest TryHarvest(int wx, int wy)
    {
        var result = HarvestRules.Evaluate(TileAt(wx, wy));
        if (result.Allowed) _diffs[(wx, wy)] = result.Becomes;
        return result;
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

    public int StructureCount => _structures.Count;

    public IReadOnlyCollection<Structure> Structures => _structures.Values.ToArray();

    public static ChunkCoord ChunkOf(int wx, int wy) => new(
        (int)Math.Floor(wx / (double)TerrainGenerator.ChunkSize),
        (int)Math.Floor(wy / (double)TerrainGenerator.ChunkSize));
}
