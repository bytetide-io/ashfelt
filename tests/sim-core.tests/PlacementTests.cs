using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class PlacementTests
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
    public void PlacingWallOnGrass_Succeeds()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        var result = world.TryPlace(x, y, ItemId.Wall);

        Assert.True(result.Allowed);
        Assert.Equal(ItemId.Wall, result.Structure.Kind);
        Assert.Equal(x, result.Structure.TileX);
        Assert.Equal(y, result.Structure.TileY);
        Assert.Equal(1, world.StructureCount);
    }

    [Fact]
    public void PlacingOnWater_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Water);

        var result = world.TryPlace(x, y, ItemId.Campfire);

        Assert.False(result.Allowed);
        Assert.Equal(0, world.StructureCount);
    }

    [Fact]
    public void PlacingOnForest_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Forest);

        var result = world.TryPlace(x, y, ItemId.Wall);

        Assert.False(result.Allowed);
        Assert.Equal(0, world.StructureCount);
    }

    [Fact]
    public void PlacingOnOccupiedTile_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        Assert.True(world.TryPlace(x, y, ItemId.Wall).Allowed);
        var second = world.TryPlace(x, y, ItemId.Campfire);

        Assert.False(second.Allowed);
        Assert.Equal(1, world.StructureCount);
    }

    [Fact]
    public void PlacingUncraftableItem_IsRefused()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);

        var result = world.TryPlace(x, y, ItemId.Wood);

        Assert.False(result.Allowed);
        Assert.Equal(0, world.StructureCount);
    }

    [Fact]
    public void LoadStructure_RoundTripsFromStorage()
    {
        var original = new World(2024);
        var (x, y) = FindTile(original, TileType.Grass);
        var placed = original.TryPlace(x, y, ItemId.Campfire).Structure;

        // Simulates a server restart: structures replayed from storage.
        var restored = new World(2024);
        foreach (var structure in original.Structures) restored.LoadStructure(structure);

        Assert.Equal(1, restored.StructureCount);
        var replayed = Assert.Single(restored.Structures);
        Assert.Equal(placed, replayed);
    }

    [Fact]
    public void RemoveStructure_FreesTheTileForReplacement()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Wall);

        Assert.True(world.RemoveStructure(x, y));
        Assert.Equal(0, world.StructureCount);
        Assert.True(world.TryPlace(x, y, ItemId.Campfire).Allowed);
    }

    [Fact]
    public void FreshlyPlacedCampfire_StartsUnlit()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        var campfire = world.TryPlace(x, y, ItemId.Campfire).Structure;

        Assert.False(world.IsLit(campfire.Id));
    }

    [Fact]
    public void FeedingWood_CatchesTheFireAlight_ButOnlyReportsTheTransition()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        var campfire = world.TryPlace(x, y, ItemId.Campfire).Structure;

        Assert.True(world.FeedFuel(campfire, woodSpent: 1)); // unlit -> lit
        Assert.True(world.IsLit(campfire.Id));

        Assert.False(world.FeedFuel(campfire, woodSpent: 1)); // already lit, just tops up
        Assert.True(world.IsLit(campfire.Id));
    }

    [Fact]
    public void FeedingAWall_HasNoEffect()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        var wall = world.TryPlace(x, y, ItemId.Wall).Structure;

        Assert.False(world.FeedFuel(wall, woodSpent: 1));
        Assert.False(world.IsLit(wall.Id));
    }

    [Fact]
    public void AdvancingFuelToZero_ExtinguishesAndStopsProvidingWarmth()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        var campfire = world.TryPlace(x, y, ItemId.Campfire).Structure;
        world.FeedFuel(campfire, woodSpent: 1);

        double metres = TerrainGenerator.TileMetres;
        var centre = new Vec3((x + 0.5) * metres, 0, (y + 0.5) * metres);
        Assert.True(world.HasWarmthNear(centre));

        int ticks = 0;
        IReadOnlyList<long> extinguished = System.Array.Empty<long>();
        while (extinguished.Count == 0 && ticks < FireRules.FuelTicksPerWood + 1)
        {
            extinguished = world.AdvanceFuel();
            ticks++;
        }

        Assert.Equal(FireRules.FuelTicksPerWood, ticks);
        Assert.Equal(campfire.Id, Assert.Single(extinguished));
        Assert.False(world.IsLit(campfire.Id));
        Assert.False(world.HasWarmthNear(centre));
    }

    [Fact]
    public void AdvancingFuel_OnAnUnfedWorld_ReportsNothing()
    {
        var world = new World(1337);
        var (x, y) = FindTile(world, TileType.Grass);
        world.TryPlace(x, y, ItemId.Campfire);

        Assert.Empty(world.AdvanceFuel());
    }
}
