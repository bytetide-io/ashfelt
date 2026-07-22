using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// Height drives both the rendered mesh and, eventually, physics collision.
/// Client and server must agree on it exactly, and it must be smooth enough
/// to walk on.
/// </summary>
public class HeightTests
{
    [Fact]
    public void HeightIsDeterministic()
    {
        var a = new TerrainGenerator(1337);
        var b = new TerrainGenerator(1337);

        for (int y = -60; y < 60; y += 3)
            for (int x = -60; x < 60; x += 3)
                Assert.Equal(a.HeightAt(x, y), b.HeightAt(x, y));
    }

    [Fact]
    public void SurfaceTypeAndHeightAgree()
    {
        var gen = new TerrainGenerator(2024);

        for (int y = -80; y < 80; y++)
        {
            for (int x = -80; x < 80; x++)
            {
                var tile = gen.TileAt(x, y);
                double height = gen.HeightAt(x, y);

                // Anything the classifier calls water must sit below sea
                // level, or boats and swimming will disagree with the mesh.
                if (tile is TileType.Water or TileType.DeepWater)
                    Assert.True(height < 0, $"{tile} at ({x},{y}) has height {height}");
                else
                    Assert.True(height >= 0, $"{tile} at ({x},{y}) has height {height}");
            }
        }
    }

    [Fact]
    public void SlopesAreWalkable()
    {
        var gen = new TerrainGenerator(99);

        // A step between neighbouring tiles taller than the player cannot be
        // climbed, and would read as an invisible wall.
        double worst = 0;
        for (int y = -80; y < 80; y++)
            for (int x = -80; x < 80; x++)
                worst = System.Math.Max(worst,
                    System.Math.Abs(gen.HeightAt(x + 1, y) - gen.HeightAt(x, y)));

        Assert.True(worst < TerrainGenerator.TileMetres * 2,
            $"steepest step is {worst:F2}m over {TerrainGenerator.TileMetres}m");
    }

    [Fact]
    public void HeightIsContinuousBetweenTiles()
    {
        var gen = new TerrainGenerator(7);

        // Sampling between tile centres must not jump, or the mesh will crack.
        double a = gen.HeightAt(10.0, 10.0);
        double b = gen.HeightAt(10.5, 10.0);
        double c = gen.HeightAt(11.0, 10.0);

        Assert.True(System.Math.Abs(b - a) <= System.Math.Abs(c - a) + 0.001);
    }
}
