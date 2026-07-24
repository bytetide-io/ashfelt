using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// These rules are the entire anti-cheat surface for movement, so each
/// rejection has a test and — just as importantly — legitimate movement is
/// tested not to trip them.
/// </summary>
public class MovementRulesTests
{
    private readonly TerrainGenerator _terrain = new(1337);

    /// <summary>A point standing on solid ground, in metres.</summary>
    private Vec3 OnGround(double tileX, double tileY)
    {
        double m = TerrainGenerator.TileMetres;
        return new Vec3(tileX * m, _terrain.HeightAt(tileX, tileY), tileY * m);
    }

    [Fact]
    public void NormalWalking_IsAccepted()
    {
        var start = OnGround(4, 4);

        // One tick of ordinary movement at walking pace.
        for (double step = 0; step <= 6.0 * 0.05; step += 0.02)
        {
            var to = OnGround(4 + step / TerrainGenerator.TileMetres, 4);
            Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.05));
        }
    }

    [Fact]
    public void Teleporting_IsRejected()
    {
        var start = OnGround(4, 4);
        var to = OnGround(400, 400);

        Assert.Equal(MoveRejection.TooFast, MovementRules.Check(_terrain, start, to, 0.05));
    }

    [Fact]
    public void Flying_IsRejected()
    {
        var start = OnGround(4, 4);
        var to = start with { Y = start.Y + 200 };

        Assert.NotEqual(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.05));
    }

    [Fact]
    public void HoveringHighAboveTerrain_IsRejected()
    {
        var start = OnGround(4, 4);
        // Arrived slowly enough to pass the speed check, but is still flying.
        var to = start with { Y = start.Y + MovementRules.MaxAirborneHeight + 5 };

        Assert.Equal(MoveRejection.TooHighAboveTerrain,
            MovementRules.Check(_terrain, start, to, 60.0));
    }

    [Fact]
    public void SinkingThroughTerrain_IsRejected()
    {
        var start = OnGround(4, 4);
        var to = start with { Y = start.Y - 40 };

        Assert.NotEqual(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.05));
    }

    [Fact]
    public void Jumping_IsAccepted()
    {
        var start = OnGround(4, 4);
        var to = start with { Y = start.Y + 6.5 * 0.1 };

        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.1));
    }

    [Fact]
    public void FallingOffACliff_IsAccepted()
    {
        var start = OnGround(4, 4) with { Y = 30 };
        var to = start with { Y = 30 - 24.0 * 0.1 };

        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.1));
    }

    [Fact]
    public void LaggySteps_ScaleTheAllowanceRatherThanRejecting()
    {
        var start = OnGround(4, 4);
        // Half a second of walking arriving as one update must still pass.
        double metres = 6.0 * 0.5;
        var to = OnGround(4 + metres / TerrainGenerator.TileMetres, 4);

        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 0.5));
    }

    [Fact]
    public void ReportingFaster_DoesNotGrantMoreDistance()
    {
        var start = OnGround(4, 4);
        var far = OnGround(4 + 5.0 / TerrainGenerator.TileMetres, 4);

        // The same displacement is fine over a long step and cheating over a
        // short one — the budget follows elapsed time, not update count.
        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, far, 1.0));
        Assert.Equal(MoveRejection.TooFast, MovementRules.Check(_terrain, start, far, 0.016));
    }

    /// <summary>A stub obstacle field: exactly the cells and edges a test names are solid.</summary>
    private sealed class StubObstacles : IMovementObstacles
    {
        public readonly System.Collections.Generic.HashSet<(int, int)> Cells = new();
        public readonly System.Collections.Generic.HashSet<(int, int, int, int)> Edges = new();
        public bool Walkable(int tileX, int tileY) => !Cells.Contains((tileX, tileY));
        public bool EdgeBlocked(int fx, int fy, int tx, int ty) => Edges.Contains((fx, fy, tx, ty));
    }

    [Fact]
    public void MovingIntoAFilledTile_IsBlocked()
    {
        var start = OnGround(4.5, 4.5); // tile (4,4)
        var to = OnGround(5.5, 4.5);    // tile (5,4)

        var obstacles = new StubObstacles();
        obstacles.Cells.Add((5, 4));

        Assert.Equal(MoveRejection.Blocked, MovementRules.Check(_terrain, start, to, 1.0, obstacles));
        // The very same move is fine when the tile is clear.
        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 1.0, new StubObstacles()));
    }

    [Fact]
    public void SteppingAcrossAWalledEdge_IsBlocked()
    {
        var start = OnGround(4.5, 4.5); // tile (4,4)
        var to = OnGround(5.5, 4.5);    // tile (5,4)

        var obstacles = new StubObstacles();
        obstacles.Edges.Add((4, 4, 5, 4));

        Assert.Equal(MoveRejection.Blocked, MovementRules.Check(_terrain, start, to, 1.0, obstacles));
    }

    [Fact]
    public void MovingWithinTheSameTile_IsNeverEdgeBlocked()
    {
        var start = OnGround(4.3, 4.3);
        var to = OnGround(4.6, 4.6); // still tile (4,4)

        var obstacles = new StubObstacles();
        obstacles.Edges.Add((4, 4, 5, 4)); // an edge exists, but this move does not cross it

        Assert.Equal(MoveRejection.None, MovementRules.Check(_terrain, start, to, 1.0, obstacles));
    }

    [Fact]
    public void ObstaclesNeverOverrideAPlainSpeedRejection()
    {
        var start = OnGround(4.5, 4.5);
        var far = OnGround(400.5, 400.5);

        Assert.Equal(MoveRejection.TooFast, MovementRules.Check(_terrain, start, far, 0.05, new StubObstacles()));
    }

    [Fact]
    public void TileOf_MapsMetresBackToTheGrid()
    {
        double m = TerrainGenerator.TileMetres;
        Assert.Equal((0, 0), MovementRules.TileOf(new Vec3(0.1, 0, 0.1)));
        Assert.Equal((3, 5), MovementRules.TileOf(new Vec3(3 * m + 0.5, 0, 5 * m + 0.5)));
        Assert.Equal((-1, -1), MovementRules.TileOf(new Vec3(-0.5, 0, -0.5)));
    }
}
