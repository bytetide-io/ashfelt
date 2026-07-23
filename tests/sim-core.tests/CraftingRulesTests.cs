using System.Linq;
using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// Crafting is authorised by the server and predicted by the client from the
/// same table, so both the accept and reject paths are pinned here, along with
/// the table's determinism — a reordered table would desync the two.
/// </summary>
public class CraftingRulesTests
{
    private static Dictionary<ItemId, int> Bag(params (ItemId Item, int Amount)[] items) =>
        items.ToDictionary(i => i.Item, i => i.Amount);

    [Fact]
    public void FundedCraft_SucceedsAndYieldsConsumeAndProduceDeltas()
    {
        var inventory = Bag((ItemId.Wood, 5));

        var result = CraftingRules.Evaluate(inventory, ItemId.Plank);

        Assert.True(result.Allowed);
        Assert.Equal(ItemId.Plank, result.Output);
        Assert.Contains(new CraftingRules.ItemDelta(ItemId.Wood, -2), result.Deltas);
        Assert.Contains(new CraftingRules.ItemDelta(ItemId.Plank, 1), result.Deltas);
    }

    [Fact]
    public void ApplyingDeltas_LeavesTheExpectedInventory()
    {
        var inventory = Bag((ItemId.Plank, 1), (ItemId.Stone, 3));

        var result = CraftingRules.Evaluate(inventory, ItemId.Pickaxe);
        Assert.True(result.Allowed);

        foreach (var delta in result.Deltas)
        {
            inventory[delta.Item] = inventory.GetValueOrDefault(delta.Item) + delta.Change;
        }

        Assert.Equal(0, inventory.GetValueOrDefault(ItemId.Plank));
        Assert.Equal(1, inventory.GetValueOrDefault(ItemId.Stone));
        Assert.Equal(1, inventory.GetValueOrDefault(ItemId.Pickaxe));
    }

    [Fact]
    public void UnderfundedCraft_IsRejectedWithNoDeltas()
    {
        var inventory = Bag((ItemId.Wood, 1)); // Plank needs 2.

        var result = CraftingRules.Evaluate(inventory, ItemId.Plank);

        Assert.False(result.Allowed);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void MissingIngredientEntirely_IsRejected()
    {
        var inventory = Bag((ItemId.Plank, 4)); // Campfire also needs Stone.

        Assert.False(CraftingRules.Evaluate(inventory, ItemId.Campfire).Allowed);
    }

    [Fact]
    public void EvaluatingAnUncraftableItem_IsRejected()
    {
        var inventory = Bag((ItemId.Wood, 99));

        Assert.False(CraftingRules.Evaluate(inventory, ItemId.Wood).Allowed);
    }

    [Fact]
    public void Evaluate_DoesNotMutateTheInventoryItReads()
    {
        var inventory = Bag((ItemId.Wood, 5));

        CraftingRules.Evaluate(inventory, ItemId.Plank);

        Assert.Equal(5, inventory[ItemId.Wood]);
        Assert.False(inventory.ContainsKey(ItemId.Plank));
    }

    [Fact]
    public void RecipeTable_IsStableAndDeterministic()
    {
        var outputs = CraftingRules.Recipes.Select(r => r.Output).ToArray();

        Assert.Equal(
            new[]
            {
                ItemId.Plank, ItemId.Rope, ItemId.Axe,
                ItemId.Pickaxe, ItemId.Wall, ItemId.Campfire,
            },
            outputs);

        // Re-reading the table yields the same order every time.
        Assert.Equal(outputs, CraftingRules.Recipes.Select(r => r.Output).ToArray());
    }

    [Fact]
    public void EveryRecipeOutput_IsResolvableAndConsistent()
    {
        foreach (var recipe in CraftingRules.Recipes)
        {
            Assert.Equal(recipe, CraftingRules.RecipeFor(recipe.Output));
            Assert.True(CraftingRules.IsCraftable(recipe.Output));
        }
    }
}
