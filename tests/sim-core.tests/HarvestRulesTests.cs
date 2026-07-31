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
        // A hair past the boundary, not exactly on it: `last + cooldown - last`
        // is not guaranteed to equal `cooldown` bit-for-bit in floating point,
        // and real callers never land exactly on the boundary either — `now`
        // comes from a continuously advancing clock, not from adding the
        // constant back to itself.
        double afterCooldown = last + HarvestRules.StrikeCooldownSeconds + 0.001;

        Assert.True(HarvestRules.CanStrike(last, afterCooldown));
    }

    [Fact]
    public void SpammingRequests_CannotBuyStrikesFasterThanTheCooldown()
    {
        // Mirrors MovementRulesTests.ReportingFaster_DoesNotGrantMoreDistance:
        // the budget follows elapsed time, not how many requests arrive.
        double lastStrikeAt = double.NegativeInfinity;
        double now = 0.0;
        int accepted = 0;
        int rejected = 0;

        // A flood of requests, ten per cooldown window, over ten windows.
        for (int i = 0; i < 100; i++)
        {
            now += HarvestRules.StrikeCooldownSeconds / 10;
            if (HarvestRules.CanStrike(lastStrikeAt, now))
            {
                accepted++;
                lastStrikeAt = now;
            }
            else
            {
                rejected++;
            }
        }

        // Roughly one strike per cooldown window should land, however many
        // requests were sent inside it — allow +/-1 for floating-point
        // accumulation across 100 additions rather than asserting an exact
        // boundary count, which isn't the property this test cares about.
        Assert.InRange(accepted, 9, 10);
        Assert.True(rejected >= 89, "spam should be rejected, not merely slowed");
    }
}
