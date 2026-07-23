using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// The player's survival meters and the pure rules that move them. Shared so
/// the client can predict depletion frame-to-frame and the server can hold the
/// authoritative value — the two must never disagree.
///
/// Determinism: every meter is an integer in fixed-point units and time is
/// counted in whole ticks. Depletion is a pure function of elapsed ticks, so
/// advancing by N ticks equals N single-tick advances exactly (there is no
/// float accumulation to drift and no wall-clock to read). Rates are written
/// as points-per-minute for legibility; the fixed-point scale is deliberately
/// one whole minute of ticks, which makes each per-tick delta an exact integer.
/// </summary>
public static class SurvivalRules
{
    /// <summary>Full value of any meter, in display points.</summary>
    public const int MaxPoints = 100;

    private const int SecondsPerMinute = 60;

    /// <summary>
    /// Ticks in a minute. Doubling as the fixed-point scale means a rate of
    /// R points-per-minute is exactly R fixed units drained per tick, so no
    /// per-tick division ever truncates.
    /// </summary>
    public const int FixedPerPoint = Tuning.TicksPerSecond * SecondsPerMinute;

    private const int MaxFixed = MaxPoints * FixedPerPoint;

    /// <summary>Hunger falls this many points every minute lived.</summary>
    public const int HungerLossPerMinute = 5;

    /// <summary>Once hunger is empty, health bleeds this many points per minute.</summary>
    public const int StarvationHealthLossPerMinute = 10;

    /// <summary>Stamina recovers this many points per minute while not exerting.</summary>
    public const int StaminaRegenPerMinute = 60;

    // Per-tick deltas equal the per-minute rates because FixedPerPoint is one
    // minute of ticks (see the type remarks).
    private const int HungerLossPerTick = HungerLossPerMinute;
    private const int StarvationHealthLossPerTick = StarvationHealthLossPerMinute;
    private const int StaminaRegenPerTick = StaminaRegenPerMinute;

    /// <summary>
    /// A player's survival meters, in fixed-point units (0..<see cref="MaxFixed"/>).
    /// Immutable: every rule returns a fresh state rather than mutating in place.
    /// </summary>
    public readonly record struct SurvivalState(int Hunger, int Stamina, int Health)
    {
        /// <summary>A well-fed, rested, healthy player — the spawn state.</summary>
        public static SurvivalState Full => FromPoints(MaxPoints, MaxPoints, MaxPoints);

        public int HungerPoints => Hunger / FixedPerPoint;
        public int StaminaPoints => Stamina / FixedPerPoint;
        public int HealthPoints => Health / FixedPerPoint;

        public bool IsStarving => Hunger == 0;
        public bool IsDead => Health == 0;
    }

    public static SurvivalState FromPoints(int hunger, int stamina, int health) => new(
        ClampFixed(hunger * FixedPerPoint),
        ClampFixed(stamina * FixedPerPoint),
        ClampFixed(health * FixedPerPoint));

    /// <summary>
    /// Advance the meters by <paramref name="ticks"/> whole ticks. Hunger drains
    /// with time; when it is empty, health decays; stamina recovers unless the
    /// player is <paramref name="exerting"/> (sprinting or mid-action).
    ///
    /// Steps one tick at a time so the starvation threshold is honoured exactly
    /// as it is crossed — this is what makes advancing by N identical to N
    /// single advances, the property the client and server rely on to agree.
    /// </summary>
    public static SurvivalState Advance(SurvivalState state, int ticks, bool exerting = false)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks), "Time only moves forward.");

        for (int i = 0; i < ticks; i++) state = Step(state, exerting);
        return state;
    }

    private static SurvivalState Step(SurvivalState state, bool exerting)
    {
        int hunger = Math.Max(0, state.Hunger - HungerLossPerTick);

        int health = hunger == 0
            ? Math.Max(0, state.Health - StarvationHealthLossPerTick)
            : state.Health;

        int stamina = exerting
            ? state.Stamina
            : Math.Min(MaxFixed, state.Stamina + StaminaRegenPerTick);

        return state with { Hunger = hunger, Stamina = stamina, Health = health };
    }

    /// <summary>Eat an item: restore hunger by its food value, clamped to full.</summary>
    public static SurvivalState Eat(SurvivalState state, int restorePoints)
    {
        if (restorePoints < 0) throw new ArgumentOutOfRangeException(nameof(restorePoints));

        return state with { Hunger = ClampFixed(state.Hunger + restorePoints * FixedPerPoint) };
    }

    /// <summary>
    /// Spend stamina on an action. Fails (returning the unchanged state) when the
    /// player cannot afford it, so callers gate the action on the returned bool
    /// rather than letting stamina silently clamp to a false success.
    /// </summary>
    public static bool TrySpendStamina(SurvivalState state, int costPoints, out SurvivalState result)
    {
        if (costPoints < 0) throw new ArgumentOutOfRangeException(nameof(costPoints));

        int cost = costPoints * FixedPerPoint;
        if (state.Stamina < cost)
        {
            result = state;
            return false;
        }

        result = state with { Stamina = state.Stamina - cost };
        return true;
    }

    private static int ClampFixed(int fixedValue) => Math.Clamp(fixedValue, 0, MaxFixed);
}
