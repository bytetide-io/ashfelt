using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Which items may be placed as structures and how a placed structure behaves.
/// Shared so the client can predict a placement and the server can authorise
/// it — the two must never disagree.
/// </summary>
public static class PlacementRules
{
    /// <summary>
    /// The placeable items, in stable declaration order. Iterate this for a
    /// deterministic placement menu instead of hand-listing the ids.
    /// </summary>
    public static readonly IReadOnlyList<ItemId> Placeables = new[]
    {
        ItemId.Wall,
        ItemId.Campfire,
    };

    /// <summary>
    /// True when spending one <paramref name="item"/> places a structure on the
    /// world. Only a small, wire-stable set is placeable.
    /// </summary>
    public static bool IsPlaceable(ItemId item) => item switch
    {
        ItemId.Wall => true,
        ItemId.Campfire => true,
        _ => false,
    };

    /// <summary>
    /// True when the placed structure blocks movement. A Wall is solid; a
    /// Campfire is walked up to, not walked through — but it does not obstruct
    /// the tile it sits on.
    /// </summary>
    public static bool Blocks(ItemId kind) => kind switch
    {
        ItemId.Wall => true,
        _ => false,
    };
}
