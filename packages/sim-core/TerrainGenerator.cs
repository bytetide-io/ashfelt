namespace Ashfall.SimCore;

public readonly record struct ChunkCoord(int X, int Y);

/// <summary>
/// A generated chunk of tiles. This is never persisted — it is always
/// re-derivable from (seed, coord). Only player diffs are stored.
/// </summary>
public sealed class Chunk
{
    public ChunkCoord Coord { get; }
    public TileType[] Tiles { get; }

    public Chunk(ChunkCoord coord, TileType[] tiles)
    {
        Coord = coord;
        Tiles = tiles;
    }

    public TileType this[int lx, int ly] => Tiles[ly * TerrainGenerator.ChunkSize + lx];
}

/// <summary>
/// Deterministic, chunk-based terrain. Same seed + coordinate always yields
/// the same tile, on client and server alike.
/// </summary>
public sealed class TerrainGenerator
{
    public const int ChunkSize = 32;

    /// <summary>Share of the woodland band that is actually a tree.</summary>
    private const int TreeDensityPercent = 34;

    private const uint TreeSalt = 0x7BEE7BEEu;

    private readonly uint _seed;

    public TerrainGenerator(uint seed) => _seed = seed;

    public uint Seed => _seed;

    /// <summary>Tile at absolute world tile coordinates.</summary>
    public TileType TileAt(int wx, int wy)
    {
        double elevation = Noise.Fbm(wx, wy, _seed, octaves: 5, frequency: 1.0 / 64.0);
        if (elevation < 0.32) return TileType.DeepWater;
        if (elevation < 0.40) return TileType.Water;
        if (elevation < 0.44) return TileType.Sand;
        if (elevation > 0.74) return TileType.Rock;

        double moisture = Noise.Fbm(wx, wy, _seed ^ 0xA5A5A5A5u, octaves: 3, frequency: 1.0 / 40.0);
        if (moisture <= 0.55) return TileType.Grass;

        // A Forest tile is one tree, and trees block movement. Scattering them
        // through the woodland band leaves clearings and paths, instead of a
        // solid wall of trunks nobody can walk into.
        return Noise.Hash(wx, wy, _seed ^ TreeSalt) % 100 < TreeDensityPercent
            ? TileType.Forest
            : TileType.Grass;
    }

    public Chunk Generate(ChunkCoord coord)
    {
        var tiles = new TileType[ChunkSize * ChunkSize];
        int ox = coord.X * ChunkSize, oy = coord.Y * ChunkSize;
        for (int ly = 0; ly < ChunkSize; ly++)
            for (int lx = 0; lx < ChunkSize; lx++)
                tiles[ly * ChunkSize + lx] = TileAt(ox + lx, oy + ly);
        return new Chunk(coord, tiles);
    }

    /// <summary>
    /// Forest and Rock block movement: they are trees and outcrops, drawn
    /// standing up off the ground. Chop or mine them to open a path.
    /// </summary>
    public static bool IsWalkable(TileType t) =>
        t is TileType.Sand or TileType.Grass;
}
