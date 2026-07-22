using Ashfall.Proto;
using Ashfall.SimCore;
using LiteNetLib;

namespace Ashfall.WorldServer;

/// <summary>
/// A connected player. The client simulates its own physics and reports where
/// it ended up; this class holds the last position the server was willing to
/// believe. That accepted position — never the client's claim — is what other
/// players see and what harvesting range is measured from.
/// </summary>
public sealed class Player
{
    public Player(int id, NetPeer peer, Vec3 position)
    {
        Id = id;
        Peer = peer;
        Position = position;
    }

    public int Id { get; }
    public NetPeer Peer { get; }

    /// <summary>Last accepted position, in metres.</summary>
    public Vec3 Position { get; private set; }

    /// <summary>Facing, in radians, for rendering other players.</summary>
    public float Yaw { get; private set; }

    /// <summary>Server time of the last accepted position, for speed budgeting.</summary>
    public double LastAcceptedAt { get; private set; }

    public int Rejections { get; private set; }

    public Dictionary<ItemId, int> Inventory { get; } = new();
    public bool InventoryDirty { get; set; }

    public void Give(ItemId item, int amount)
    {
        Inventory[item] = Inventory.GetValueOrDefault(item) + amount;
        InventoryDirty = true;
    }

    /// <summary>
    /// Validates a reported position. Returns the rejection reason, or None if
    /// the move was accepted and <see cref="Position"/> updated.
    /// </summary>
    public MoveRejection TryAccept(TerrainGenerator terrain, Vec3 reported, float yaw, double now)
    {
        // Budget is elapsed time since the last *accepted* position, so
        // spamming updates cannot buy extra distance.
        double delta = now - LastAcceptedAt;
        var rejection = MovementRules.Check(terrain, Position, reported, delta);

        if (rejection != MoveRejection.None)
        {
            Rejections++;
            return rejection;
        }

        Position = reported;
        Yaw = yaw;
        LastAcceptedAt = now;
        return MoveRejection.None;
    }

    public bool IsWithinReach(int tileX, int tileY)
    {
        double metres = TerrainGenerator.TileMetres;
        // Horizontal distance only: standing on a ledge above a tree should
        // still let you reach it, and height is already bounded by the
        // movement rules.
        var centre = new Vec3((tileX + 0.5) * metres, Position.Y, (tileY + 0.5) * metres);
        return Position.HorizontalDistanceTo(centre) <= Tuning.ChopRangeMetres;
    }

    public void ResetClock(double now) => LastAcceptedAt = now;

    /// <summary>Finds a walkable spawn point near the origin, in metres.</summary>
    public static Vec3 FindSpawn(World world, TerrainGenerator terrain)
    {
        double metres = TerrainGenerator.TileMetres;
        for (int radius = 0; radius < 256; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                    if (world.TileAt(dx, dy) is not (TileType.Grass or TileType.Sand)) continue;

                    return new Vec3(
                        (dx + 0.5) * metres,
                        terrain.HeightAt(dx + 0.5, dy + 0.5),
                        (dy + 0.5) * metres);
                }
            }
        }
        return Vec3.Zero;
    }
}
