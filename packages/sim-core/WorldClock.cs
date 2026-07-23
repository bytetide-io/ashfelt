using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// Maps a server tick count to a time-of-day. Shared so the client can light
/// the sky from the same clock the server counts by — the two never disagree,
/// and a client that reconnects sees the correct hour immediately.
///
/// Deterministic: a pure function of the integer tick, with no wall-clock read.
/// </summary>
public static class WorldClock
{
    /// <summary>Ticks in one full day/night cycle.</summary>
    public const long TicksPerDay = (long)Tuning.TicksPerSecond * Tuning.SecondsPerGameDay;

    /// <summary>
    /// Time-of-day in [0,1): 0 is midnight, 0.5 is noon. Wraps every
    /// <see cref="TicksPerDay"/> ticks.
    /// </summary>
    public static double TimeOfDay(long tick)
    {
        long phase = ((tick % TicksPerDay) + TicksPerDay) % TicksPerDay;
        return phase / (double)TicksPerDay;
    }

    /// <summary>Whether the given time-of-day is in the lit half of the cycle.</summary>
    public static bool IsDaytime(double timeOfDay) => timeOfDay is >= 0.25 and < 0.75;
}
