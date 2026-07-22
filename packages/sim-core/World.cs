using System.Collections.Concurrent;

namespace Ashfall.SimCore;

/// <summary>
/// Generated terrain plus the player-caused diffs layered on top. Diffs are
/// the only thing that is ever persisted; everything else is re-derived from
/// the seed.
/// </summary>
public sealed class World
{
    private readonly TerrainGenerator _terrain;
    private readonly ConcurrentDictionary<(int X, int Y), TileType> _diffs = new();

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

    public static ChunkCoord ChunkOf(int wx, int wy) => new(
        (int)Math.Floor(wx / (double)TerrainGenerator.ChunkSize),
        (int)Math.Floor(wy / (double)TerrainGenerator.ChunkSize));
}
