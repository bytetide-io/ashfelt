using Ashfall.Proto;
using Ashfall.SimCore;

namespace Ashfall.WorldServer;

/// <summary>
/// The solid things a player cannot walk through, read live off the world and its
/// build sites: a standing tree, a placed blocking structure, or a built wall on
/// an edge. Because it reads current state, a felled tree or a cancelled wall
/// stops blocking the instant it is gone — no snapshot to invalidate.
/// </summary>
public sealed class WorldObstacles : IMovementObstacles
{
    private readonly World _world;
    private readonly IReadOnlyDictionary<long, BuildSite> _sites;

    public WorldObstacles(World world, IReadOnlyDictionary<long, BuildSite> sites)
    {
        _world = world;
        _sites = sites;
    }

    public bool Walkable(int tileX, int tileY)
    {
        // A Forest tile is a standing tree; harvesting it turns the tile to grass
        // (a diff), so this reads walkable the moment the tree comes down.
        if (_world.TileAt(tileX, tileY) == TileType.Forest) return false;
        return !_world.HasBlockingStructureAt(tileX, tileY);
    }

    public bool EdgeBlocked(int fromX, int fromY, int toX, int toY)
    {
        if (EdgeSlot(fromX, fromY, toX, toY) is not { } slot) return false;
        foreach (var site in _sites.Values)
            if (site.IsSolidAt(slot)) return true;
        return false;
    }

    /// <summary>The canonical wall slot on the edge two adjacent tiles share, or null
    /// when the tiles are not orthogonally adjacent.</summary>
    private static PieceSlot? EdgeSlot(int ax, int ay, int bx, int by)
    {
        if (bx == ax && by == ay + 1) return PieceSlot.Canonical(ax, ay, 0, PieceLayer.WallNorth);
        if (bx == ax && by == ay - 1) return PieceSlot.Canonical(ax, ay, 0, PieceLayer.WallSouth);
        if (bx == ax + 1 && by == ay) return PieceSlot.Canonical(ax, ay, 0, PieceLayer.WallEast);
        if (bx == ax - 1 && by == ay) return PieceSlot.Canonical(ax, ay, 0, PieceLayer.WallWest);
        return null;
    }
}
