using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// What harvesting a tile yields and what it leaves behind. Shared so the
/// client can predict the outcome and the server can authorise it — the two
/// must never disagree.
/// </summary>
public static class HarvestRules
{
    public readonly record struct Harvest(bool Allowed, ItemId Item, int Amount, TileType Becomes);

    public static Harvest Evaluate(TileType tile) => tile switch
    {
        TileType.Forest => new Harvest(true, ItemId.Wood, 1, TileType.Grass),
        TileType.Rock => new Harvest(true, ItemId.Stone, 1, TileType.Sand),
        _ => new Harvest(false, ItemId.None, 0, tile),
    };

    public static bool IsHarvestable(TileType tile) => Evaluate(tile).Allowed;
}
