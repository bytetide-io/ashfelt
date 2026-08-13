using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Interest management: whether one point in the world is close enough to
/// another that a player standing at the first should be told about the
/// second. Horizontal distance only, mirroring harvest reach and warmth
/// checks — a ledge above something doesn't put it out of interest range.
/// </summary>
public static class InterestRules
{
    public static bool IsWithinRange(Vec3 viewer, Vec3 subject) =>
        viewer.HorizontalDistanceTo(subject) <= Tuning.InterestRadiusMetres;
}
