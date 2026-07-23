using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// What harvesting a tile yields and what it leaves behind. Shared so the
/// client can predict the outcome and the server can authorise it — the two
/// must never disagree.
///
/// The gatherable nodes are a data table (<see cref="Nodes"/>) rather than a
/// switch: adding a berry bush or an ore is a row here plus its item's
/// <see cref="ItemDef"/>. <see cref="HarvestNodeDef.PreferredTool"/> is the
/// class a future tool bonus will match against; harvesting bare-handed is still
/// allowed today.
/// </summary>
public static class HarvestRules
{
    public readonly record struct Harvest(bool Allowed, ItemId Item, int Amount, TileType Becomes);

    /// <summary>
    /// One gatherable tile: what it yields, what it turns into once gathered,
    /// and which tool class speeds it. In stable declaration order.
    /// </summary>
    public readonly record struct HarvestNodeDef(
        TileType Tile,
        ItemId Yields,
        int Amount,
        TileType Becomes,
        ToolClass PreferredTool);

    public static readonly IReadOnlyList<HarvestNodeDef> Nodes = new HarvestNodeDef[]
    {
        new(TileType.Forest, ItemId.Wood, 1, TileType.Grass, ToolClass.Axe),
        new(TileType.Rock, ItemId.Stone, 1, TileType.Sand, ToolClass.Pickaxe),
        new(TileType.Shrub, ItemId.Fiber, 1, TileType.Grass, ToolClass.None),
        new(TileType.BerryBush, ItemId.Berry, 1, TileType.Grass, ToolClass.None),
    };

    private static readonly IReadOnlyDictionary<TileType, HarvestNodeDef> ByTile =
        Nodes.ToDictionary(node => node.Tile);

    /// <summary>
    /// The outcome of harvesting <paramref name="tile"/> while holding a tool of
    /// class <paramref name="heldTool"/> and tier <paramref name="heldTier"/>.
    /// A tool matching the node's preferred class adds its tier to the base yield
    /// — bare hands still work (so tools stay bootstrappable), a matching tool
    /// just gathers more. A mismatched or absent tool grants no bonus.
    /// </summary>
    public static Harvest Evaluate(TileType tile, ToolClass heldTool = ToolClass.None, int heldTier = 0)
    {
        if (!ByTile.TryGetValue(tile, out var node))
            return new Harvest(false, ItemId.None, 0, tile);

        int bonus = node.PreferredTool != ToolClass.None && heldTool == node.PreferredTool
            ? Math.Max(0, heldTier)
            : 0;
        return new Harvest(true, node.Yields, node.Amount + bonus, node.Becomes);
    }

    public static bool IsHarvestable(TileType tile) => ByTile.ContainsKey(tile);

    /// <summary>The tool class that speeds this tile's harvest, or None.</summary>
    public static ToolClass PreferredTool(TileType tile) =>
        ByTile.TryGetValue(tile, out var node) ? node.PreferredTool : ToolClass.None;
}
