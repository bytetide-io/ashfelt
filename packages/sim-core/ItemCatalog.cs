using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>Broad role of an item, for grouping menus and gating behaviour.</summary>
public enum ItemCategory : byte
{
    Resource,
    Material,
    Tool,
    Placeable,
    Food,
}

/// <summary>
/// The kind of gathering a tool assists. <see cref="None"/> is "not a tool";
/// harvest rules match a node's preferred tool class against the one held.
/// </summary>
public enum ToolClass : byte
{
    None,
    Axe,
    Pickaxe,
}

/// <summary>
/// The static, world-independent facts about one item: what it is called, how it
/// stacks, and the data other rules read to decide its behaviour — food value,
/// tool class/tier, whether it can be placed. Keyed by the wire-stable
/// <see cref="ItemId"/>.
///
/// This is the single source of item behaviour. Adding a gatherable food or a
/// new tool is a row here plus (for a food) a harvest node — no server,
/// protocol or client code change. Rules like <see cref="PlacementRules"/> read
/// this table instead of hand-listing ids, so the two can never drift.
/// </summary>
public readonly record struct ItemDef(
    ItemId Id,
    string Name,
    int StackSize,
    ItemCategory Category,
    ToolClass Tool = ToolClass.None,
    int ToolTier = 0,
    int FoodValue = 0,
    bool Placeable = false,
    double WarmthRadiusMetres = 0);

/// <summary>
/// The item definition table, in stable declaration order. Shared so the client
/// and server read identical facts (invariant #4) and covered by a test that
/// asserts every <see cref="ItemId"/> — except <see cref="ItemId.None"/> — has
/// exactly one entry.
///
/// Determinism: a static array in fixed declaration order plus a lookup built
/// once at type-load. Iterate <see cref="All"/> for a deterministic menu; index
/// with <see cref="Of"/>.
/// </summary>
public static class ItemCatalog
{
    /// <summary>Default stack ceiling for items with no special limit.</summary>
    public const int DefaultStackSize = 99;

    private const int ToolStackSize = 1;

    public static readonly IReadOnlyList<ItemDef> All = new ItemDef[]
    {
        new(ItemId.Wood, "Wood", DefaultStackSize, ItemCategory.Resource),
        new(ItemId.Stone, "Stone", DefaultStackSize, ItemCategory.Resource),
        new(ItemId.Fiber, "Fiber", DefaultStackSize, ItemCategory.Resource),
        new(ItemId.Plank, "Plank", DefaultStackSize, ItemCategory.Material),
        new(ItemId.Rope, "Rope", DefaultStackSize, ItemCategory.Material),
        new(ItemId.Axe, "Axe", ToolStackSize, ItemCategory.Tool, Tool: ToolClass.Axe, ToolTier: 1),
        new(ItemId.Pickaxe, "Pickaxe", ToolStackSize, ItemCategory.Tool, Tool: ToolClass.Pickaxe, ToolTier: 1),
        new(ItemId.Wall, "Wall", DefaultStackSize, ItemCategory.Placeable, Placeable: true),
        new(ItemId.Campfire, "Campfire", DefaultStackSize, ItemCategory.Placeable,
            Placeable: true, WarmthRadiusMetres: CampfireWarmthRadius),
        new(ItemId.Berry, "Berry", DefaultStackSize, ItemCategory.Food, FoodValue: BerryFoodValue),
    };

    /// <summary>How far a campfire's warmth reaches, in metres — a few tiles, so a
    /// player must actually shelter by the fire at night, not merely own one.</summary>
    private const double CampfireWarmthRadius = 6.0;

    /// <summary>Hunger points one berry restores. Four minutes of hunger drain
    /// (<see cref="SurvivalRules.HungerLossPerMinute"/>), so foraging is a real
    /// but modest reprieve, not a full meal.</summary>
    private const int BerryFoodValue = 20;

    private static readonly IReadOnlyDictionary<ItemId, ItemDef> ById =
        All.ToDictionary(def => def.Id);

    /// <summary>
    /// The definition for <paramref name="id"/>. Throws for an unknown id —
    /// every real item must be catalogued, so a miss is a content bug, not a
    /// runtime condition to clamp. <see cref="ItemId.None"/> has no definition.
    /// </summary>
    public static ItemDef Of(ItemId id) =>
        ById.TryGetValue(id, out var def)
            ? def
            : throw new ArgumentOutOfRangeException(nameof(id), id, "No ItemDef for this id.");

    public static bool TryGet(ItemId id, out ItemDef def) => ById.TryGetValue(id, out def);

    public static bool IsTool(ItemId id) => TryGet(id, out var def) && def.Tool != ToolClass.None;

    public static bool IsFood(ItemId id) => TryGet(id, out var def) && def.FoodValue > 0;

    public static bool ProvidesWarmth(ItemId id) => TryGet(id, out var def) && def.WarmthRadiusMetres > 0;
}
