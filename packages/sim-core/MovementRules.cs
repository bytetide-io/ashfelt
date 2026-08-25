namespace Ashfall.SimCore;

/// <summary>Minimal 3D vector, so sim-core stays free of engine types.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);

    public double HorizontalDistanceTo(Vec3 other)
    {
        double dx = other.X - X, dz = other.Z - Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public double DistanceTo(Vec3 other)
    {
        double dx = other.X - X, dy = other.Y - Y, dz = other.Z - Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public enum MoveRejection
{
    None,
    TooFast,
    RoseTooFast,
    FellTooFast,
    BelowTerrain,
    TooHighAboveTerrain,
    /// <summary>The move ended inside, or crossed into, a solid obstacle.</summary>
    Blocked,
}

/// <summary>
/// The solid things a player cannot move through, queried by tile. Kept an
/// abstraction so <see cref="MovementRules"/> stays free of world and structure
/// types: the server implements it over live terrain, structures and built
/// pieces, and a test can stub it. Both client prediction and server authority
/// read the same rule, so an honest player is never wrongly blocked.
/// </summary>
public interface IMovementObstacles
{
    /// <summary>False when a solid obstacle fills the tile (a tree, a blocking structure).</summary>
    bool Walkable(int tileX, int tileY);

    /// <summary>True when a built wall stands on the edge shared by two orthogonally
    /// adjacent tiles, so stepping straight from one to the other is refused.</summary>
    bool EdgeBlocked(int fromX, int fromY, int toX, int toY);
}

/// <summary>
/// The client simulates movement; the server checks it. Both use these rules,
/// so a legitimate player is never corrected and a cheating one always is.
///
/// This deliberately does not reproduce physics. It bounds what physics can
/// possibly produce — which is enough to reject speed hacks, teleports,
/// flying and walking through terrain, without a physics engine on the server.
/// </summary>
public static class MovementRules
{
    /// <summary>Metres per second on the flat, plus headroom for slope and slide.</summary>
    public const double MaxHorizontalSpeed = 9.0;

    /// <summary>A jump cannot lift you faster than this.</summary>
    public const double MaxRiseSpeed = 8.0;

    /// <summary>Terminal fall speed, generously bounded.</summary>
    public const double MaxFallSpeed = 60.0;

    /// <summary>How far below the surface a position may sit before it is clipping.</summary>
    public const double GroundTolerance = 1.5;

    /// <summary>How far above the surface a player may be before it is flight.</summary>
    public const double MaxAirborneHeight = 25.0;

    /// <summary>Ignore steps shorter than this; they are timing jitter, not cheating.</summary>
    private const double MinTimestep = 0.001;

    /// <summary>
    /// Checks a proposed move. <paramref name="delta"/> is the time since the
    /// player's last accepted position, so a client cannot gain distance by
    /// reporting more often.
    /// </summary>
    public static MoveRejection Check(
        TerrainGenerator terrain, Vec3 from, Vec3 to, double delta, IMovementObstacles? obstacles = null)
    {
        if (delta < MinTimestep) delta = MinTimestep;

        // Allowances are per-elapsed-time, so lag produces a larger budget
        // rather than a false rejection.
        double horizontal = from.HorizontalDistanceTo(to);
        if (horizontal > MaxHorizontalSpeed * delta + GroundTolerance)
            return MoveRejection.TooFast;

        double rise = to.Y - from.Y;
        if (rise > MaxRiseSpeed * delta + GroundTolerance)
            return MoveRejection.RoseTooFast;
        if (-rise > MaxFallSpeed * delta + GroundTolerance)
            return MoveRejection.FellTooFast;

        double surface = SurfaceHeight(terrain, to);
        if (to.Y < surface - GroundTolerance) return MoveRejection.BelowTerrain;
        if (to.Y > surface + MaxAirborneHeight) return MoveRejection.TooHighAboveTerrain;

        if (obstacles is not null && ObstructsMove(from, to, obstacles))
            return MoveRejection.Blocked;

        return MoveRejection.None;
    }

    /// <summary>
    /// Whether solid obstacles refuse the move: the destination tile is filled, or
    /// the move steps across a walled edge. Only orthogonally adjacent single-tile
    /// steps are edge-tested — a wider jump is bounded by the destination-tile
    /// check, and the client's own physics stops honest players short of a wall.
    /// </summary>
    private static bool ObstructsMove(Vec3 from, Vec3 to, IMovementObstacles obstacles)
    {
        var (fx, fy) = TileOf(from);
        var (tx, ty) = TileOf(to);

        if (!obstacles.Walkable(tx, ty)) return true;

        bool adjacent = Math.Abs(tx - fx) + Math.Abs(ty - fy) == 1;
        return adjacent && obstacles.EdgeBlocked(fx, fy, tx, ty);
    }

    /// <summary>Terrain height in metres beneath a world position.</summary>
    public static double SurfaceHeight(TerrainGenerator terrain, Vec3 position) =>
        terrain.HeightAt(position.X / TerrainGenerator.TileMetres,
                         position.Z / TerrainGenerator.TileMetres);

    /// <summary>Tile containing a world position, for harvesting and diffs.</summary>
    public static (int X, int Y) TileOf(Vec3 position) => (
        (int)Math.Floor(position.X / TerrainGenerator.TileMetres),
        (int)Math.Floor(position.Z / TerrainGenerator.TileMetres));

    /// <summary>Places a position exactly on the surface. Used to correct a rejected move.</summary>
    public static Vec3 SnapToSurface(TerrainGenerator terrain, Vec3 position) =>
        position with { Y = SurfaceHeight(terrain, position) };
}
