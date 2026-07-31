using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// <see cref="HarvestRules.CanStrike"/> is the entire anti-farming surface for
/// harvesting: it is what stops a client that spams <c>ChopRequest</c> from
/// felling nodes and collecting resources far faster than a human tapping can.
/// </summary>
public class HarvestRulesTests
{
    [Fact]
    public void FirstStrikeEver_IsAlwaysAllowed()
    {
        Assert.True(HarvestRules.CanStrike(double.NegativeInfinity, now: 0.0));
    }

    [Fact]
    public void StrikingAgain_BeforeCooldownElapses_IsRejected()
    {
        double last = 10.0;
        double tooSoon = last + HarvestRules.StrikeCooldownSeconds - 0.01;

        Assert.False(HarvestRules.CanStrike(last, tooSoon));
    }

    [Fact]
    public void StrikingAgain_AfterCooldownElapses_IsAllowed()
    {
        double last = 10.0;
        double onTime = last + HarvestRules.StrikeCooldownSeconds;

        Assert.True(HarvestRules.CanStrike(last, onTime));
    }

    [Fact]
    public void SpammingRequests_CannotBuyStrikesFasterThanTheCooldown()
    {
        // Mirrors MovementRulesTests.ReportingFaster_DoesNotGrantMoreDistance:
        // the budget follows elapsed time, not how many requests arrive.
        double lastStrikeAt = double.NegativeInfinity;
        double now = 0.0;
        int accepted = 0;

        // A flood of requests, ten per cooldown window, over ten windows.
        for (int i = 0; i < 100; i++)
        {
            now += HarvestRules.StrikeCooldownSeconds / 10;
            if (HarvestRules.CanStrike(lastStrikeAt, now))
            {
                accepted++;
                lastStrikeAt = now;
            }
        }

        // Only one strike per cooldown window should land, however many
        // requests were sent inside it.
        Assert.Equal(10, accepted);
    }
}
