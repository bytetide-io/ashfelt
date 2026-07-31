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
    /// One gatherable tile: what it yields per strike, how many strikes fell it,
    /// what it turns into once felled, and which tool class speeds it. In stable
    /// declaration order.
    /// </summary>
    public readonly record struct HarvestNodeDef(
        TileType Tile,
        ItemId Yields,
        int Amount,
        int Hits,
        TileType Becomes,
        ToolClass PreferredTool);

    /// <summary>
    /// Strikes to fell a node bare-handed. Foraged plants come away in one grab;
    /// a tree or a rock is real work, so mashing a sapling and felling an oak no
    /// longer cost the same tap. A matching tool shaves strikes (see
    /// <see cref="HitsToFell"/>), which is what finally makes an axe worth its wood.
    /// </summary>
    public static readonly IReadOnlyList<HarvestNodeDef> Nodes = new HarvestNodeDef[]
    {
        new(TileType.Forest, ItemId.Wood, 1, 4, TileType.Grass, ToolClass.Axe),
        new(TileType.Rock, ItemId.Stone, 1, 5, TileType.Sand, ToolClass.Pickaxe),
        new(TileType.Shrub, ItemId.Fiber, 1, 1, TileType.Grass, ToolClass.None),
        new(TileType.BerryBush, ItemId.Berry, 1, 1, TileType.Grass, ToolClass.None),
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

    /// <summary>
    /// How many strikes it takes to fell <paramref name="tile"/> holding a tool of
    /// class <paramref name="heldTool"/> and tier <paramref name="heldTier"/>. A
    /// matching tool removes one strike per tier — never below a single strike, so
    /// the best axe still swings once — while a mismatched or absent tool leaves the
    /// bare-handed cost. Non-harvestable tiles report zero.
    /// </summary>
    public static int HitsToFell(TileType tile, ToolClass heldTool = ToolClass.None, int heldTier = 0)
    {
        if (!ByTile.TryGetValue(tile, out var node)) return 0;

        int reduction = node.PreferredTool != ToolClass.None && heldTool == node.PreferredTool
            ? Math.Max(0, heldTier)
            : 0;
        return Math.Max(1, node.Hits - reduction);
    }

    public static bool IsHarvestable(TileType tile) => ByTile.ContainsKey(tile);

    /// <summary>The tool class that speeds this tile's harvest, or None.</summary>
    public static ToolClass PreferredTool(TileType tile) =>
        ByTile.TryGetValue(tile, out var node) ? node.PreferredTool : ToolClass.None;

    /// <summary>
    /// Minimum real time between one player's accepted harvest strikes, on any
    /// node. Without this, a strike costs nothing but a packet: a client that
    /// sends <c>ChopRequest</c> faster than a human taps can fell nodes and
    /// collect resources at unbounded speed, since <see cref="Evaluate"/> and
    /// <see cref="HitsToFell"/> only ever look at what tool is held, never at
    /// how quickly strikes arrive.
    /// </summary>
    public const double StrikeCooldownSeconds = 0.35;

    /// <summary>
    /// True once <see cref="StrikeCooldownSeconds"/> have elapsed since
    /// <paramref name="lastStrikeAt"/>. Mirrors <see cref="MovementRules"/>'s
    /// elapsed-time budget: gated by time since the last *accepted* strike, so
    /// spamming requests cannot buy extra strikes the way spamming updates
    /// cannot buy extra movement distance. The caller records <paramref
    /// name="now"/> as the new last-strike time only when this returns true.
    /// </summary>
    public static bool CanStrike(double lastStrikeAt, double now) =>
        now - lastStrikeAt >= StrikeCooldownSeconds;
}
