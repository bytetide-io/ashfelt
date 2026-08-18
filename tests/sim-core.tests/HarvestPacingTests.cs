using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// This is the entire anti-cheat surface for gather rate: reach alone does not
/// bound how fast a client can strike, so a rejected strike here is the only
/// thing standing between a real tap cadence and a bot felling every node in
/// reach in one network burst.
/// </summary>
public class HarvestPacingTests
{
    [Fact]
    public void FirstStrikeEver_IsAllowed()
    {
        Assert.True(HarvestPacing.IsAllowed(double.NegativeInfinity, now: 0));
    }

    [Fact]
    public void StrikeBeforeMinInterval_IsRejected()
    {
        double last = 10.0;
        double now = last + HarvestPacing.MinIntervalSeconds - 0.01;
        Assert.False(HarvestPacing.IsAllowed(last, now));
    }

    [Fact]
    public void StrikeAfterMinInterval_IsAllowed()
    {
        double last = 10.0;
        double now = last + HarvestPacing.MinIntervalSeconds + 0.001;
        Assert.True(HarvestPacing.IsAllowed(last, now));
    }

    [Fact]
    public void SpammedStrikes_AreThrottledToTheMinInterval()
    {
        double lastAccepted = double.NegativeInfinity;
        int accepted = 0;

        // A bot sending 200 requests across one simulated second — far faster
        // than any tap gesture — must still only land the paced number of
        // strikes, never one per request.
        for (double now = 0; now < 1.0; now += 1.0 / 200.0)
        {
            if (!HarvestPacing.IsAllowed(lastAccepted, now)) continue;
            lastAccepted = now;
            accepted++;
        }

        int maxLegitimateStrikes = (int)(1.0 / HarvestPacing.MinIntervalSeconds) + 1;
        Assert.True(accepted <= maxLegitimateStrikes,
            $"expected at most {maxLegitimateStrikes} strikes, got {accepted}");
    }
}
