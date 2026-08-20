using System;
using System.Linq;
using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// The catalog is the single source of item behaviour, read by both client and
/// server, so its completeness and consistency are pinned here: every real item
/// must have exactly one definition, and rules derived from it (placeability,
/// recipe outputs) must resolve against it.
/// </summary>
public class ItemCatalogTests
{
    [Fact]
    public void EveryItemId_ExceptNone_HasExactlyOneDefinition()
    {
        foreach (ItemId id in Enum.GetValues<ItemId>())
        {
            if (id == ItemId.None) continue;

            Assert.True(ItemCatalog.TryGet(id, out _), $"{id} has no ItemDef.");
            Assert.Single(ItemCatalog.All, def => def.Id == id);
        }
    }

    [Fact]
    public void None_HasNoDefinition()
    {
        Assert.False(ItemCatalog.TryGet(ItemId.None, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemCatalog.Of(ItemId.None));
    }

    [Fact]
    public void CatalogOrder_IsStableAcrossReads()
    {
        var first = ItemCatalog.All.Select(def => def.Id).ToArray();
        var second = ItemCatalog.All.Select(def => def.Id).ToArray();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Placeables_AreExactlyTheItemsFlaggedPlaceable()
    {
        var flagged = ItemCatalog.All.Where(def => def.Placeable).Select(def => def.Id);
        Assert.Equal(flagged, PlacementRules.Placeables);

        foreach (var id in PlacementRules.Placeables)
            Assert.True(PlacementRules.IsPlaceable(id));
    }

    [Fact]
    public void EveryRecipeOutputAndInput_IsACataloguedItem()
    {
        foreach (var recipe in CraftingRules.Recipes)
        {
            Assert.True(ItemCatalog.TryGet(recipe.Output, out _), $"{recipe.Output} is uncatalogued.");
            foreach (var input in recipe.Inputs)
                Assert.True(ItemCatalog.TryGet(input.Item, out _), $"{input.Item} is uncatalogued.");
        }
    }

    [Fact]
    public void EveryHarvestNode_YieldsACataloguedItem_AndBecomesADifferentTile()
    {
        foreach (var node in HarvestRules.Nodes)
        {
            Assert.True(ItemCatalog.TryGet(node.Yields, out _), $"{node.Yields} is uncatalogued.");
            Assert.NotEqual(node.Tile, node.Becomes);
            Assert.True(node.Amount > 0);
            Assert.True(HarvestRules.IsHarvestable(node.Tile));
        }
    }

    [Fact]
    public void Tools_ReportTheirClass_AndNonToolsDoNot()
    {
        Assert.True(ItemCatalog.IsTool(ItemId.Axe));
        Assert.True(ItemCatalog.IsTool(ItemId.Pickaxe));
        Assert.False(ItemCatalog.IsTool(ItemId.Wood));
        Assert.Equal(ToolClass.Axe, ItemCatalog.Of(ItemId.Axe).Tool);
    }

    [Fact]
    public void Berry_IsFood_AndEatingItRestoresExactlyItsFoodValue()
    {
        Assert.True(ItemCatalog.IsFood(ItemId.Berry));

        int foodValue = ItemCatalog.Of(ItemId.Berry).FoodValue;
        var hungry = SurvivalRules.FromPoints(hunger: 40, stamina: 100, health: 100);

        var fed = SurvivalRules.Eat(hungry, foodValue);

        Assert.Equal(40 + foodValue, fed.HungerPoints);
    }

    [Fact]
    public void Campfire_ProvidesWarmth_AndReachesOnlyWithinItsRadius()
    {
        Assert.True(ItemCatalog.ProvidesWarmth(ItemId.Campfire));
        Assert.False(ItemCatalog.ProvidesWarmth(ItemId.Wall));

        var world = new World(1337u);
        var structure = new Structure(1, TileX: 10, TileY: 10, ItemId.Campfire);
        world.LoadStructure(structure);
        world.FeedFuel(structure, woodSpent: 1); // a placed campfire starts unlit

        double metres = TerrainGenerator.TileMetres;
        double radius = ItemCatalog.Of(ItemId.Campfire).WarmthRadiusMetres;
        var centre = new Vec3((10 + 0.5) * metres, 0, (10 + 0.5) * metres);

        Assert.True(world.HasWarmthNear(centre));
        Assert.True(world.HasWarmthNear(centre with { X = centre.X + radius - 0.1 }));
        Assert.False(world.HasWarmthNear(centre with { X = centre.X + radius + metres }));
    }

    [Fact]
    public void BerryBush_IsHarvestable_YieldsBerry_AndIsWalkable()
    {
        var harvest = HarvestRules.Evaluate(TileType.BerryBush);

        Assert.True(harvest.Allowed);
        Assert.Equal(ItemId.Berry, harvest.Item);
        Assert.True(TerrainGenerator.IsWalkable(TileType.BerryBush));
    }

    [Fact]
    public void MatchingTool_AddsItsTierToTheYield_MismatchedToolDoesNot()
    {
        int axeTier = ItemCatalog.Of(ItemId.Axe).ToolTier;

        var bareHanded = HarvestRules.Evaluate(TileType.Forest);
        var withAxe = HarvestRules.Evaluate(TileType.Forest, ToolClass.Axe, axeTier);
        var withPick = HarvestRules.Evaluate(TileType.Forest, ToolClass.Pickaxe, axeTier);

        Assert.Equal(bareHanded.Amount + axeTier, withAxe.Amount);
        Assert.Equal(bareHanded.Amount, withPick.Amount); // wrong class: no bonus
        Assert.True(bareHanded.Amount >= 1);               // still gatherable bare-handed
    }

    [Fact]
    public void ToollessNode_IgnoresAnyHeldTool()
    {
        // Shrubs have no preferred tool, so a tool never changes the fiber yield.
        var bare = HarvestRules.Evaluate(TileType.Shrub);
        var withAxe = HarvestRules.Evaluate(TileType.Shrub, ToolClass.Axe, 5);

        Assert.Equal(bare.Amount, withAxe.Amount);
    }
}
