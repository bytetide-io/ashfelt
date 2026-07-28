using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// Player-state broadcasts are filtered by these rules (architecture invariant
/// #6), so a bug here either leaks positions further than intended or drops a
/// remote player's avatar without telling the client to remove it.
/// </summary>
public class InterestRulesTests
{
    [Fact]
    public void WithinRadius_IsVisible()
    {
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(InterestRules.PlayerRadiusMetres - 1, 0, 0);

        Assert.True(InterestRules.IsVisible(viewer, subject));
    }

    [Fact]
    public void ExactlyAtRadius_IsVisible()
    {
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(InterestRules.PlayerRadiusMetres, 0, 0);

        Assert.True(InterestRules.IsVisible(viewer, subject));
    }

    [Fact]
    public void BeyondRadius_IsNotVisible()
    {
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(InterestRules.PlayerRadiusMetres + 1, 0, 0);

        Assert.False(InterestRules.IsVisible(viewer, subject));
    }

    [Fact]
    public void VerticalDistance_DoesNotAffectVisibility()
    {
        // Horizontal only, mirroring harvest reach and warmth radius: someone
        // on a ledge far above or below should not be hidden by that alone.
        var viewer = new Vec3(0, 0, 0);
        var subject = new Vec3(0, 500, 0);

        Assert.True(InterestRules.IsVisible(viewer, subject));
    }

    [Fact]
    public void VisibleIds_ReturnsOnlyThoseInRange()
    {
        var viewer = new Vec3(0, 0, 0);
        var others = new (int Id, Vec3 Position)[]
        {
            (1, new Vec3(10, 0, 0)),
            (2, new Vec3(InterestRules.PlayerRadiusMetres + 50, 0, 0)),
        };

        var visible = InterestRules.VisibleIds(viewer, others);

        Assert.Equal(new HashSet<int> { 1 }, visible);
    }

    [Fact]
    public void NewlyOutOfRange_FindsIdsThatDroppedOut()
    {
        var previously = new HashSet<int> { 1, 2, 3 };
        var currently = new HashSet<int> { 1, 3 };

        Assert.Equal(new[] { 2 }, InterestRules.NewlyOutOfRange(previously, currently));
    }

    [Fact]
    public void NewlyOutOfRange_EmptyWhenNothingLeft()
    {
        var previously = new HashSet<int> { 1, 2 };
        var currently = new HashSet<int> { 1, 2, 3 };

        Assert.Empty(InterestRules.NewlyOutOfRange(previously, currently));
    }
}
