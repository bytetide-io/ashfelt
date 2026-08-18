namespace Ashfall.SimCore;

/// <summary>
/// Server-side pacing for harvest strikes. Mirrors <see cref="MovementRules"/>:
/// a strike is only valid once at least <see cref="MinIntervalSeconds"/> has
/// passed since the player's last accepted strike, budgeted by elapsed server
/// time so a client cannot buy extra strikes by sending requests faster.
///
/// Without this, <see cref="World.TryHarvest"/> has no time cost at all — only
/// a reach check gates it — so a scripted client could fell every node within
/// reach in a single network burst instead of one strike per tap.
/// </summary>
public static class HarvestPacing
{
    /// <summary>Minimum real time between accepted strikes, in seconds.</summary>
    public const double MinIntervalSeconds = 0.2;

    /// <summary>
    /// True once enough time has passed since <paramref name="lastAcceptedAt"/>
    /// for another strike to be legitimate.
    /// </summary>
    public static bool IsAllowed(double lastAcceptedAt, double now) =>
        now - lastAcceptedAt >= MinIntervalSeconds;
}
