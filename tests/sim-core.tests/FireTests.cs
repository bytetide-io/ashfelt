using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class FireTests
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
    public void PlacingCampfire_StartsLitWithInitialFuel()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        var placed = world.TryPlace(x, y, ItemId.Campfire).Structure;

        Assert.True(placed.Lit);
        Assert.Equal(FireRules.InitialFuelTicks, placed.FuelTicks);
    }

    [Fact]
    public void PlacingWall_NeverBurns()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        var placed = world.TryPlace(x, y, ItemId.Wall).Structure;

        Assert.False(placed.Lit);
        Assert.Equal(0, placed.FuelTicks);
        Assert.False(FireRules.Burns(ItemId.Wall));
    }

    [Fact]
    public void Feeding_AddsFuelWithoutReignitingAnAlreadyLitFire()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Campfire);

        var fed = world.TryFeed(x, y);

        Assert.True(fed.Allowed);
        Assert.False(fed.Reignited);
        Assert.Equal(FireRules.InitialFuelTicks + FireRules.FuelTicksPerLog, fed.Structure.FuelTicks);
    }

    [Fact]
    public void Feeding_CapsAtMaxFuelAndThenRefuses()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Campfire);

        World.FeedResult last = default;
        for (int i = 0; i < 64 && (i == 0 || last.Allowed); i++)
            last = world.TryFeed(x, y);

        Assert.False(last.Allowed);
        Assert.Equal(FireRules.MaxFuelTicks, world.Structures.Single(s => s.TileX == x && s.TileY == y).FuelTicks);
    }

    [Fact]
    public void Feeding_NonBurningStructure_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Wall);

        Assert.False(world.TryFeed(x, y).Allowed);
    }

    [Fact]
    public void Feeding_EmptyTile_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        Assert.False(world.TryFeed(x, y).Allowed);
    }

    [Fact]
    public void AdvancingFires_DrainsFuelAndReportsExtinguishedOnlyOnce()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Campfire);

        Assert.Empty(world.AdvanceFires(FireRules.InitialFuelTicks - 1));
        var extinguished = world.AdvanceFires(1);

        var gone = Assert.Single(extinguished);
        Assert.Equal(x, gone.TileX);
        Assert.Equal(0, gone.FuelTicks);
        Assert.False(gone.Lit);

        // Already cold: draining further reports nothing new.
        Assert.Empty(world.AdvanceFires(FireRules.FuelTicksPerLog));
    }

    [Fact]
    public void ReignitingAColdFire_ReportsReignited()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Campfire);
        world.AdvanceFires(FireRules.InitialFuelTicks);

        var fed = world.TryFeed(x, y);

        Assert.True(fed.Allowed);
        Assert.True(fed.Reignited);
        Assert.True(fed.Structure.Lit);
    }

    [Fact]
    public void HasWarmthNear_IsTrueOnlyWhileLitAndInRadius()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        var placed = world.TryPlace(x, y, ItemId.Campfire).Structure;
        var radius = ItemCatalog.Of(ItemId.Campfire).WarmthRadiusMetres;
        double metres = TerrainGenerator.TileMetres;
        var near = new Vec3((x + 0.5) * metres, 0, (y + 0.5) * metres);
        var far = new Vec3(near.X + radius * 4, 0, near.Z);

        Assert.True(world.HasWarmthNear(near));
        Assert.False(world.HasWarmthNear(far));

        world.AdvanceFires(placed.FuelTicks);

        Assert.False(world.HasWarmthNear(near));
    }
}
