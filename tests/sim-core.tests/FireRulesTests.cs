using Xunit;

namespace Ashfall.SimCore.Tests;

public class FireRulesTests
{
    [Fact]
    public void ZeroFuel_IsUnlit()
    {
        Assert.False(FireRules.IsLit(0));
        Assert.True(FireRules.IsLit(1));
    }

    [Fact]
    public void Feeding_AddsOnTopOfWhatRemains()
    {
        int fed = FireRules.Feed(fuelTicks: 10, woodSpent: 2);
        Assert.Equal(10 + 2 * FireRules.FuelTicksPerWood, fed);
    }

    [Fact]
    public void Advancing_BurnsOneTickAndNeverGoesNegative()
    {
        Assert.Equal(4, FireRules.Advance(5));
        Assert.Equal(0, FireRules.Advance(1));
        Assert.Equal(0, FireRules.Advance(0));
    }
}
