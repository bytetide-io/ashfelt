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
    /// The placeable items, in stable declaration order, derived from the item
    /// catalog so the two never drift. Iterate this for a deterministic
    /// placement menu instead of hand-listing the ids.
    /// </summary>
    public static readonly IReadOnlyList<ItemId> Placeables =
        ItemCatalog.All.Where(def => def.Placeable).Select(def => def.Id).ToArray();

    /// <summary>
    /// True when spending one <paramref name="item"/> places a structure on the
    /// world — the single source of truth is <see cref="ItemDef.Placeable"/>.
    /// </summary>
    public static bool IsPlaceable(ItemId item) => ItemCatalog.TryGet(item, out var def) && def.Placeable;

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
