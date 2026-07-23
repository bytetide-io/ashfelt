using Ashfall.Proto;
using Xunit;
using static Ashfall.SimCore.SurvivalRules;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// The survival meters are shared prediction: the client runs them every frame
/// and the server holds the truth, so depletion must be an exact function of
/// elapsed ticks. Each rule is tested for its rate, its ordering (health only
/// after hunger) and — the load-bearing one — that time composes.
/// </summary>
public class SurvivalRulesTests
{
    private const int TicksPerMinute = FixedPerPoint;

    [Fact]
    public void Hunger_DepletesAtTheSpecifiedRate()
    {
        var after = Advance(SurvivalState.Full, TicksPerMinute);

        Assert.Equal(MaxPoints - HungerLossPerMinute, after.HungerPoints);
    }

    [Fact]
    public void Hunger_DrainsToEmptyAndClampsThere()
    {
        int minutesToEmpty = MaxPoints / HungerLossPerMinute;
        var after = Advance(SurvivalState.Full, TicksPerMinute * (minutesToEmpty + 5));

        Assert.Equal(0, after.Hunger);
        Assert.True(after.IsStarving);
    }

    [Fact]
    public void Health_HoldsWhileThereIsAnyHunger()
    {
        var fed = FromPoints(hunger: MaxPoints, stamina: 0, health: MaxPoints);
        var after = Advance(fed, TicksPerMinute * 10);

        Assert.Equal(MaxPoints, after.HealthPoints);
    }

    [Fact]
    public void Health_DecaysOnlyOnceHungerIsExhausted()
    {
        var starving = FromPoints(hunger: 0, stamina: 0, health: MaxPoints);
        var after = Advance(starving, TicksPerMinute);

        Assert.Equal(MaxPoints - StarvationHealthLossPerMinute, after.HealthPoints);
    }

    [Fact]
    public void Stamina_RegeneratesWhenNotExertingAndClampsAtFull()
    {
        var drained = FromPoints(hunger: MaxPoints, stamina: 0, health: MaxPoints);

        var afterAMinute = Advance(drained, TicksPerMinute);
        Assert.Equal(StaminaRegenPerMinute, afterAMinute.StaminaPoints);

        var afterLong = Advance(drained, TicksPerMinute * 10);
        Assert.Equal(MaxPoints, afterLong.StaminaPoints);
    }

    [Fact]
    public void Stamina_DoesNotRegenerateWhileExerting()
    {
        var drained = FromPoints(hunger: MaxPoints, stamina: 20, health: MaxPoints);
        var after = Advance(drained, TicksPerMinute, exerting: true);

        Assert.Equal(20, after.StaminaPoints);
    }

    [Fact]
    public void AdvancingByTicks_EqualsRepeatedSingleTickAdvances()
    {
        // Start already starving so the health-decay branch is exercised too.
        var start = FromPoints(hunger: 3, stamina: 10, health: MaxPoints);

        const int Ticks = 500;
        var bulk = Advance(start, Ticks);

        var stepwise = start;
        for (int i = 0; i < Ticks; i++) stepwise = Advance(stepwise, 1);

        Assert.Equal(stepwise, bulk);
    }

    [Fact]
    public void Eating_RestoresHungerAndClampsToFull()
    {
        var hungry = FromPoints(hunger: 40, stamina: 0, health: MaxPoints);

        Assert.Equal(70, Eat(hungry, 30).HungerPoints);
        Assert.Equal(MaxPoints, Eat(hungry, 90).HungerPoints);
    }

    [Fact]
    public void SpendingStamina_SucceedsWhenAffordableAndDeducts()
    {
        var rested = FromPoints(hunger: MaxPoints, stamina: 50, health: MaxPoints);

        Assert.True(TrySpendStamina(rested, 30, out var after));
        Assert.Equal(20, after.StaminaPoints);
    }

    [Fact]
    public void SpendingStamina_FailsAndLeavesStateUntouchedWhenTooCostly()
    {
        var rested = FromPoints(hunger: MaxPoints, stamina: 10, health: MaxPoints);

        Assert.False(TrySpendStamina(rested, 30, out var after));
        Assert.Equal(rested, after);
    }
}
