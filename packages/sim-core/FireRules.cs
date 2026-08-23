using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Which placed structures burn fuel to provide warmth, and how a
/// <see cref="ItemId.Wood"/> log feeds one. Shared so the client can predict a
/// feed and the server can authorise it — the two must never disagree.
///
/// A campfire is not permanent heat: it starts alight with the wood spent
/// crafting it, drains a tick at a time, and goes cold once spent. Warmth then
/// depends on a group actually tending the fire, not merely having built one —
/// the same cooperation-or-free-ride tension the survival meters already
/// create around food and shelter.
/// </summary>
public static class FireRules
{
    /// <summary>True when <paramref name="kind"/> is a structure that burns fuel to
    /// stay lit. Only a Campfire does; other placeables have no fuel state.</summary>
    public static bool Burns(ItemId kind) => kind == ItemId.Campfire;

    /// <summary>Ticks of burn one fed <see cref="ItemId.Wood"/> log adds — 90 seconds,
    /// long enough to be worth the walk back to the woodpile, short enough that a
    /// fire left untended through a game-night still goes cold.</summary>
    public const int FuelTicksPerLog = Tuning.TicksPerSecond * 90;

    /// <summary>
    /// A freshly placed campfire starts alight with exactly the 3 Wood spent
    /// crafting it (<see cref="CraftingRules.Recipes"/>), already burning — no
    /// separate "light it" step.
    /// </summary>
    public const int InitialFuelTicks = FuelTicksPerLog * 3;

    /// <summary>
    /// Fuel never stockpiles past this many logs' worth, so tending a fire stays a
    /// rhythm through the night rather than a one-time top-up that lasts the
    /// whole game.
    /// </summary>
    public const int MaxFuelTicks = FuelTicksPerLog * 6;
}
