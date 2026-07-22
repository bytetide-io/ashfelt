using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// Guards the properties the renderer depends on. A Forest tile is one tree
/// that blocks movement, so woodland must contain gaps — otherwise the map is
/// a wall of trunks a player can neither cross nor see through.
/// </summary>
public class TerrainShapeTests
{
    [Theory]
    [InlineData(TileType.Grass, true)]
    [InlineData(TileType.Sand, true)]
    [InlineData(TileType.Forest, false)]
    [InlineData(TileType.Rock, false)]
    [InlineData(TileType.Water, false)]
    [InlineData(TileType.DeepWater, false)]
    public void Walkability_MatchesWhatIsDrawnStandingUp(TileType tile, bool walkable)
    {
        Assert.Equal(walkable, TerrainGenerator.IsWalkable(tile));
    }

    [Fact]
    public void Woodland_ContainsClearings()
    {
        var gen = new TerrainGenerator(1337);

        // Any 8x8 window that contains trees should also contain walkable
        // ground, or the forest has become impassable.
        int windowsWithTrees = 0;
        for (int wy = -96; wy < 96; wy += 8)
        {
            for (int wx = -96; wx < 96; wx += 8)
            {
                int trees = 0, walkable = 0;
                for (int y = wy; y < wy + 8; y++)
                    for (int x = wx; x < wx + 8; x++)
                    {
                        var tile = gen.TileAt(x, y);
                        if (tile == TileType.Forest) trees++;
                        else if (TerrainGenerator.IsWalkable(tile)) walkable++;
                    }

                if (trees == 0) continue;
                windowsWithTrees++;
                Assert.True(walkable > 0,
                    $"window at ({wx},{wy}) is {trees} trees with no walkable tile");
            }
        }

        Assert.True(windowsWithTrees > 20, $"expected plenty of woodland, saw {windowsWithTrees} windows");
    }

    [Fact]
    public void TreesAreScattered_NotSolidWoodland()
    {
        var gen = new TerrainGenerator(2024);

        int trees = 0, total = 0;
        for (int y = -128; y < 128; y++)
            for (int x = -128; x < 128; x++, total++)
                if (gen.TileAt(x, y) == TileType.Forest) trees++;

        double share = trees / (double)total;
        // Sparse enough to walk through, common enough to be worth harvesting.
        Assert.InRange(share, 0.01, 0.25);
    }
}
