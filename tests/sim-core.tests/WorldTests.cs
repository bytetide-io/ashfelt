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

    /// <summary>Strikes a node bare-handed until it falls, so tests that care about
    /// the resulting diff need not know how many strikes the node takes.</summary>
    private static void FellNode(World world, int x, int y)
    {
        for (int i = 0; i < 64; i++)
            if (world.TryHarvest(x, y).Felled) return;
        throw new Xunit.Sdk.XunitException($"node at ({x},{y}) never felled");
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
    public void ChoppingForest_TakesSeveralStrikesAndOnlyThenLeavesGrass()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Forest);
        int strikes = HarvestRules.HitsToFell(TileType.Forest);
        Assert.True(strikes > 1, "a tree should take more than one strike");

        for (int i = 0; i < strikes - 1; i++)
        {
            var mid = world.TryHarvest(x, y);
            Assert.True(mid.Allowed);
            Assert.Equal(ItemId.Wood, mid.Item);
            Assert.Equal(1, mid.Amount);
            Assert.False(mid.Felled);
            // Still a tree, and nothing persisted until it falls.
            Assert.Equal(TileType.Forest, world.TileAt(x, y));
            Assert.Equal(0, world.DiffCount);
        }

        var felling = world.TryHarvest(x, y);
        Assert.True(felling.Felled);
        Assert.Equal(TileType.Grass, world.TileAt(x, y));
        Assert.Equal(1, world.DiffCount);
    }

    [Fact]
    public void GatheringShrub_YieldsFiberInOneStrikeAndLeavesGrass()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Shrub);

        var result = world.TryHarvest(x, y);

        Assert.True(result.Allowed);
        Assert.True(result.Felled);
        Assert.Equal(ItemId.Fiber, result.Item);
        Assert.Equal(1, result.Amount);
        Assert.Equal(TileType.Grass, world.TileAt(x, y));
    }

    [Fact]
    public void MatchingTool_FellsATreeInFewerStrikes()
    {
        int bare = HarvestRules.HitsToFell(TileType.Forest);
        int withAxe = HarvestRules.HitsToFell(TileType.Forest, ToolClass.Axe, 1);
        Assert.Equal(bare - 1, withAxe);

        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Forest);
        for (int i = 0; i < withAxe - 1; i++)
            Assert.False(world.TryHarvest(x, y, ToolClass.Axe, 1).Felled);
        Assert.True(world.TryHarvest(x, y, ToolClass.Axe, 1).Felled);
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
        FellNode(world, x, y);

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
        FellNode(original, x, y);

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

    [Theory]
    [InlineData(0, 0, 0, 0, 1, true)]   // same chunk
    [InlineData(0, 0, 1, 0, 1, true)]   // adjacent, within radius
    [InlineData(0, 0, 1, 1, 1, true)]   // diagonal, within radius (Chebyshev)
    [InlineData(0, 0, 2, 0, 1, false)]  // just past radius
    [InlineData(0, 0, 5, 5, 1, false)]  // far away
    [InlineData(-1, -1, 0, 0, 1, true)] // negative-coordinate chunks
    public void ChunksWithinInterest_UsesChebyshevDistance(
        int ax, int ay, int bx, int by, int radius, bool expected)
    {
        var a = new ChunkCoord(ax, ay);
        var b = new ChunkCoord(bx, by);
        Assert.Equal(expected, World.ChunksWithinInterest(a, b, radius));
        // Symmetric: whichever chunk is "the viewer" should not change the answer.
        Assert.Equal(expected, World.ChunksWithinInterest(b, a, radius));
    }

    [Fact]
    public void IsWithinInterest_FollowsConfiguredRadiusInWorldSpace()
    {
        double metres = TerrainGenerator.TileMetres;
        int chunkMetres = TerrainGenerator.ChunkSize * (int)metres;
        var viewer = new Vec3(0, 0, 0);

        // One chunk away: within the default interest radius.
        var near = new Vec3(chunkMetres, 0, 0);
        Assert.True(World.IsWithinInterest(viewer, near));

        // Many chunks away: outside it.
        var far = new Vec3(chunkMetres * 10, 0, 0);
        Assert.False(World.IsWithinInterest(viewer, far));
    }
}
