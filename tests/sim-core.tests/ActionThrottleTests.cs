using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// This is the entire anti-spam surface for ChopRequest/CraftRequest/
/// PlaceRequest/EatRequest, so both the reject and the accept path are tested
/// — a false rejection would throttle legitimate rapid taps.
/// </summary>
public class ActionThrottleTests
{
    [Fact]
    public void ImmediateRepeat_IsNotReady()
    {
        Assert.False(ActionThrottle.Ready(lastActionAt: 10.0, now: 10.0));
    }

    [Fact]
    public void WithinTheMinInterval_IsNotReady()
    {
        double now = 10.0 + ActionThrottle.MinIntervalSeconds - 0.01;
        Assert.False(ActionThrottle.Ready(lastActionAt: 10.0, now: now));
    }

    [Fact]
    public void AtOrAfterTheMinInterval_IsReady()
    {
        double now = 10.0 + ActionThrottle.MinIntervalSeconds;
        Assert.True(ActionThrottle.Ready(lastActionAt: 10.0, now: now));
    }

    [Fact]
    public void NoPriorAction_IsReady()
    {
        Assert.True(ActionThrottle.Ready(lastActionAt: double.NegativeInfinity, now: 0.0));
    }
}
