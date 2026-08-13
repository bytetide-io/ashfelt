using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class InterestRulesTests
{
    [Fact]
    public void SamePosition_IsWithinRange()
    {
        var viewer = new Vec3(0, 0, 0);
        Assert.True(InterestRules.IsWithinRange(viewer, viewer));
    }

    [Fact]
    public void JustInsideRadius_IsWithinRange()
    {
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(Tuning.InterestRadiusMetres - 1, 0, 0);
        Assert.True(InterestRules.IsWithinRange(viewer, subject));
    }

    [Fact]
    public void JustOutsideRadius_IsNotWithinRange()
    {
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(Tuning.InterestRadiusMetres + 1, 0, 0);
        Assert.False(InterestRules.IsWithinRange(viewer, subject));
    }

    [Fact]
    public void VerticalDistanceAlone_DoesNotAffectRange()
    {
        // A viewer far below or above the subject, but horizontally on top of
        // it, is still in range — mirrors HasWarmthNear and IsWithinReach.
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(0, 500, 0);
        Assert.True(InterestRules.IsWithinRange(viewer, subject));
    }
}
