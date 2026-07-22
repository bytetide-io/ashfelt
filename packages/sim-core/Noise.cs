namespace Ashfall.SimCore;

/// <summary>
/// Deterministic value noise. Integer hashing only — no platform-dependent
/// RNG — so client and world-server derive identical terrain from a seed.
/// </summary>
public static class Noise
{
    public static uint Hash(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed;
            h ^= (uint)x * 0x9E3779B1u;
            h ^= (uint)y * 0x85EBCA77u;
            h ^= h >> 15;
            h *= 0x2545F491u;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Hash mapped to [0,1).</summary>
    public static double Unit(int x, int y, uint seed) => Hash(x, y, seed) / 4294967296.0;

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

    /// <summary>Bilinearly interpolated value noise at the given cell size.</summary>
    public static double Value(double x, double y, uint seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = Smooth(x - x0), fy = Smooth(y - y0);

        double n00 = Unit(x0, y0, seed), n10 = Unit(x0 + 1, y0, seed);
        double n01 = Unit(x0, y0 + 1, seed), n11 = Unit(x0 + 1, y0 + 1, seed);

        double top = n00 + (n10 - n00) * fx;
        double bottom = n01 + (n11 - n01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Fractal (summed octaves) value noise in [0,1].</summary>
    public static double Fbm(double x, double y, uint seed, int octaves = 4, double frequency = 1.0 / 48.0)
    {
        double sum = 0, amplitude = 1, norm = 0, f = frequency;
        for (int o = 0; o < octaves; o++)
        {
            sum += Value(x * f, y * f, seed + (uint)o * 0x9E3779B1u) * amplitude;
            norm += amplitude;
            amplitude *= 0.5;
            f *= 2.0;
        }
        return sum / norm;
    }
}
