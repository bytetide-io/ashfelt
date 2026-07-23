using Ashfall.SimCore;
using Xunit;

namespace Ashfall.SimCore.Tests;

public class WorldClockTests
{
    [Fact]
    public void MidnightAtZero()
    {
        Assert.Equal(0.0, WorldClock.TimeOfDay(0), 6);
    }

    [Fact]
    public void NoonAtHalfDay()
    {
        Assert.Equal(0.5, WorldClock.TimeOfDay(WorldClock.TicksPerDay / 2), 6);
    }

    [Fact]
    public void WrapsEveryDay()
    {
        Assert.Equal(WorldClock.TimeOfDay(3), WorldClock.TimeOfDay(WorldClock.TicksPerDay + 3), 6);
    }

    [Fact]
    public void NegativeTicksWrapForward()
    {
        double t = WorldClock.TimeOfDay(-1);
        Assert.InRange(t, 0.0, 1.0);
    }

    [Fact]
    public void NoonIsDaytimeMidnightIsNot()
    {
        Assert.True(WorldClock.IsDaytime(0.5));
        Assert.False(WorldClock.IsDaytime(0.0));
    }
}
