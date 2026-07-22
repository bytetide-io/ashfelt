using Ashfall.SimCore;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class DeterminismTests
{
    [Fact]
    public void SameSeedAndCoordinate_AlwaysReturnsSameTile()
    {
        var a = new TerrainGenerator(1337);
        var b = new TerrainGenerator(1337);

        for (int y = -200; y < 200; y += 7)
            for (int x = -200; x < 200; x += 7)
                Assert.Equal(a.TileAt(x, y), b.TileAt(x, y));
    }

    [Fact]
    public void ChunkGeneration_MatchesPerTileGeneration()
    {
        var gen = new TerrainGenerator(42);
        var coord = new ChunkCoord(-3, 5);
        var chunk = gen.Generate(coord);

        for (int ly = 0; ly < TerrainGenerator.ChunkSize; ly++)
            for (int lx = 0; lx < TerrainGenerator.ChunkSize; lx++)
                Assert.Equal(
                    gen.TileAt(coord.X * TerrainGenerator.ChunkSize + lx,
                               coord.Y * TerrainGenerator.ChunkSize + ly),
                    chunk[lx, ly]);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentWorlds()
    {
        var a = new TerrainGenerator(1);
        var b = new TerrainGenerator(2);

        int differences = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (a.TileAt(x, y) != b.TileAt(x, y)) differences++;

        Assert.True(differences > 100, $"expected divergent worlds, got {differences} differing tiles");
    }

    [Fact]
    public void GeneratedTerrain_ContainsMoreThanOneBiome()
    {
        var gen = new TerrainGenerator(2024);
        var seen = new HashSet<TileType>();
        for (int y = -128; y < 128; y++)
            for (int x = -128; x < 128; x++)
                seen.Add(gen.TileAt(x, y));

        Assert.True(seen.Count >= 3, $"only produced biomes: {string.Join(", ", seen)}");
    }
}
