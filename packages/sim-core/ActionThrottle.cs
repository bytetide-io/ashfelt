namespace Ashfall.SimCore;

/// <summary>
/// Gates economy-affecting requests (harvest, craft, place, eat) to at most one
/// accepted action per <see cref="MinIntervalSeconds"/> of real time. Nothing
/// else in those handlers is time-bounded — a strike always yields, a craft or
/// placement is checked only against inventory — so a client that blasts
/// requests without this floor can farm resources arbitrarily faster than any
/// tap-driven client ever could. Shared so both the throttle and its tests live
/// next to the other anti-cheat rules, even though it enforces server time
/// rather than a movement bound.
/// </summary>
public static class ActionThrottle
{
    /// <summary>
    /// Minimum real seconds between two accepted actions from one player. Well
    /// above the tightest interval a real tap gesture can repeat at, so a
    /// legitimate player never feels it.
    /// </summary>
    public const double MinIntervalSeconds = 0.15;

    /// <summary>
    /// True when enough time has passed since <paramref name="lastActionAt"/> to
    /// accept another action at <paramref name="now"/>.
    /// </summary>
    public static bool Ready(double lastActionAt, double now) =>
        now - lastActionAt >= MinIntervalSeconds;
}
