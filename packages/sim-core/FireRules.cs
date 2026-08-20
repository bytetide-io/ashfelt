using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Deterministic fuel bookkeeping for warmth-providing structures (today,
/// only the campfire). A structure with zero fuel is unlit and gives no
/// warmth — <see cref="World.HasWarmthNear"/> is what makes that matter, and
/// is what turns "place a campfire once" into "keep it fed."
///
/// Fuel is counted in whole ticks, like <see cref="SurvivalRules"/>, so it
/// advances identically regardless of frame rate and never drifts.
/// </summary>
public static class FireRules
{
    /// <summary>Ticks of fuel one Wood adds. A few minutes per log, so a fire
    /// wants occasional feeding rather than one log lasting the whole night —
    /// that's what makes tending it a real, shared chore.</summary>
    public const int FuelTicksPerWood = Tuning.TicksPerSecond * 60 * 3;

    public static bool IsLit(int fuelTicks) => fuelTicks > 0;

    /// <summary>Fuel after feeding <paramref name="woodSpent"/> logs in, on top
    /// of whatever fuel remained.</summary>
    public static int Feed(int fuelTicks, int woodSpent) => fuelTicks + woodSpent * FuelTicksPerWood;

    /// <summary>Fuel after one tick's burn, floored at zero (never negative).</summary>
    public static int Advance(int fuelTicks) => Math.Max(0, fuelTicks - 1);
}
