using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class WorldTests
{
    /// <summary>Finds a tile of the given type so tests don't depend on hardcoded coordinates.</summary>
    private static (int X, int Y) FindTile(World world, TileType type)
    {
        for (int y = -64; y < 64; y++)
            for (int x = -64; x < 64; x++)
                if (world.TileAt(x, y) == type) return (x, y);
        throw new Xunit.Sdk.XunitException($"no {type} tile found near origin");
    }

    [Fact]
    public void UnmodifiedWorld_MatchesGeneratedTerrain()
    {
        var world = new World(99);
        var terrain = new TerrainGenerator(99);

        for (int y = -20; y < 20; y++)
            for (int x = -20; x < 20; x++)
                Assert.Equal(terrain.TileAt(x, y), world.TileAt(x, y));
    }

    [Fact]
    public void ChoppingForest_YieldsWoodAndLeavesGrass()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Forest);

        var result = world.TryHarvest(x, y);

        Assert.True(result.Allowed);
        Assert.Equal(ItemId.Wood, result.Item);
        Assert.Equal(1, result.Amount);
        Assert.Equal(TileType.Grass, world.TileAt(x, y));
        Assert.Equal(1, world.DiffCount);
    }

    [Fact]
    public void ChoppingWater_IsRefusedAndRecordsNoDiff()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Water);

        var result = world.TryHarvest(x, y);

        Assert.False(result.Allowed);
        Assert.Equal(TileType.Water, world.TileAt(x, y));
        Assert.Equal(0, world.DiffCount);
    }

    [Fact]
    public void DiffOverridesGeneratedTerrain_AndAppearsInChunks()
    {
        var world = new World(7);
        var (x, y) = FindTile(world, TileType.Forest);
        world.TryHarvest(x, y);

        var coord = World.ChunkOf(x, y);
        var chunk = world.GenerateChunk(coord);
        int size = TerrainGenerator.ChunkSize;

        Assert.Equal(TileType.Grass, chunk[x - coord.X * size, y - coord.Y * size]);
        // The underlying generator is untouched — diffs are a layer, not a rewrite.
        Assert.Equal(TileType.Forest, world.Terrain.TileAt(x, y));
    }

    [Fact]
    public void ReplayingStoredDiffs_ReproducesWorldState()
    {
        var original = new World(2024);
        var (x, y) = FindTile(original, TileType.Forest);
        original.TryHarvest(x, y);

        // Simulates a server restart: same seed, diffs replayed from storage.
        var restored = new World(2024);
        foreach (var (pos, tile) in original.Diffs) restored.LoadDiff(pos.X, pos.Y, tile);

        Assert.Equal(original.TileAt(x, y), restored.TileAt(x, y));
        Assert.Equal(TileType.Grass, restored.TileAt(x, y));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(31, 31, 0, 0)]
    [InlineData(32, 32, 1, 1)]
    [InlineData(-1, -1, -1, -1)]
    [InlineData(-32, -32, -1, -1)]
    [InlineData(-33, -33, -2, -2)]
    public void ChunkOf_HandlesNegativeCoordinates(int wx, int wy, int cx, int cy)
    {
        var coord = World.ChunkOf(wx, wy);
        Assert.Equal(new ChunkCoord(cx, cy), coord);
    }
}
