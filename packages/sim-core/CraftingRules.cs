using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// What a recipe consumes and produces. Shared so the client can predict a
/// craft and the server can authorise it — the two must never disagree.
///
/// Recipes are pure data: an ordered list of typed ingredients plus the output.
/// Ordering is fixed at declaration and never depends on runtime iteration, so
/// evaluation is deterministic across client and server.
/// </summary>
public static class CraftingRules
{
    public readonly record struct Ingredient(ItemId Item, int Amount);

    public readonly record struct Recipe(ItemId Output, int OutputAmount, IReadOnlyList<Ingredient> Inputs);

    /// <summary>
    /// A single applied delta the caller folds into an inventory: negative for
    /// consumed inputs, positive for the produced output. The caller mutates its
    /// own state — <see cref="Evaluate"/> never touches the inventory it reads.
    /// </summary>
    public readonly record struct ItemDelta(ItemId Item, int Change);

    public readonly record struct Craft(bool Allowed, ItemId Output, IReadOnlyList<ItemDelta> Deltas)
    {
        public static readonly Craft Denied = new(false, ItemId.None, Array.Empty<ItemDelta>());
    }

    /// <summary>
    /// The full recipe table, in stable declaration order. Index into it with
    /// <see cref="ItemId"/> via <see cref="RecipeFor"/>; iterate it directly for
    /// a deterministic crafting menu.
    /// </summary>
    public static readonly IReadOnlyList<Recipe> Recipes = new Recipe[]
    {
        new(ItemId.Plank, 1, new Ingredient[] { new(ItemId.Wood, 2) }),
        new(ItemId.Rope, 1, new Ingredient[] { new(ItemId.Fiber, 3) }),
        new(ItemId.Axe, 1, new Ingredient[] { new(ItemId.Plank, 1), new(ItemId.Stone, 1) }),
        new(ItemId.Pickaxe, 1, new Ingredient[] { new(ItemId.Plank, 1), new(ItemId.Stone, 2) }),
        new(ItemId.Wall, 1, new Ingredient[] { new(ItemId.Plank, 4) }),
        new(ItemId.Campfire, 1, new Ingredient[] { new(ItemId.Wood, 3), new(ItemId.Stone, 2) }),
    };

    public static Recipe? RecipeFor(ItemId output)
    {
        foreach (var recipe in Recipes)
        {
            if (recipe.Output == output) return recipe;
        }
        return null;
    }

    public static bool CanCraft(IReadOnlyDictionary<ItemId, int> inventory, Recipe recipe)
    {
        foreach (var input in recipe.Inputs)
        {
            if (inventory.GetValueOrDefault(input.Item) < input.Amount) return false;
        }
        return true;
    }

    /// <summary>
    /// Decide whether <paramref name="inventory"/> can produce <paramref name="output"/>
    /// and, if so, the deltas to apply. Reads the inventory but never mutates it —
    /// mirrors <see cref="HarvestRules.Evaluate"/>: the caller applies the result.
    /// </summary>
    public static Craft Evaluate(IReadOnlyDictionary<ItemId, int> inventory, ItemId output)
    {
        if (RecipeFor(output) is not { } recipe) return Craft.Denied;
        if (!CanCraft(inventory, recipe)) return Craft.Denied;

        var deltas = new ItemDelta[recipe.Inputs.Count + 1];
        for (int i = 0; i < recipe.Inputs.Count; i++)
        {
            var input = recipe.Inputs[i];
            deltas[i] = new ItemDelta(input.Item, -input.Amount);
        }
        deltas[recipe.Inputs.Count] = new ItemDelta(recipe.Output, recipe.OutputAmount);

        return new Craft(true, recipe.Output, deltas);
    }

    public static bool IsCraftable(ItemId output) => RecipeFor(output) is not null;
}
