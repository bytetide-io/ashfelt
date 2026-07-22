using Ashfall.Proto;
using Ashfall.SimCore;
using LiteNetLib;

namespace Ashfall.WorldServer;

/// <summary>
/// A connected player. Position here is authoritative; the client's own copy
/// is a prediction that gets corrected by the next state broadcast.
/// </summary>
public sealed class Player
{
    public Player(int id, NetPeer peer, float x, float y)
    {
        Id = id;
        Peer = peer;
        X = x;
        Y = y;
    }

    public int Id { get; }
    public NetPeer Peer { get; }

    /// <summary>Position in tile units (fractional).</summary>
    public float X { get; private set; }
    public float Y { get; private set; }

    /// <summary>Latest input direction, normalised, from the client.</summary>
    public float IntentX { get; set; }
    public float IntentY { get; set; }

    public Dictionary<ItemId, int> Inventory { get; } = new();
    public bool InventoryDirty { get; set; }

    public void Give(ItemId item, int amount)
    {
        Inventory[item] = Inventory.GetValueOrDefault(item) + amount;
        InventoryDirty = true;
    }

    /// <summary>
    /// Advances the player by one tick, refusing movement into unwalkable
    /// tiles. Axes resolve independently so sliding along a wall works.
    /// </summary>
    public void Tick(World world, float dt)
    {
        if (IntentX == 0 && IntentY == 0) return;

        float len = MathF.Sqrt(IntentX * IntentX + IntentY * IntentY);
        if (len > 1f) { IntentX /= len; IntentY /= len; }

        float step = Tuning.MoveTilesPerSecond * dt;
        float nx = X + IntentX * step;
        float ny = Y + IntentY * step;

        if (IsWalkable(world, nx, Y)) X = nx;
        if (IsWalkable(world, X, ny)) Y = ny;
    }

    private static bool IsWalkable(World world, float x, float y) =>
        TerrainGenerator.IsWalkable(world.TileAt((int)MathF.Floor(x), (int)MathF.Floor(y)));

    public bool IsWithinReach(int tileX, int tileY)
    {
        float dx = tileX + 0.5f - X;
        float dy = tileY + 0.5f - Y;
        return dx * dx + dy * dy <= Tuning.ChopRangeTiles * Tuning.ChopRangeTiles;
    }

    /// <summary>Finds a walkable spawn point near the origin.</summary>
    public static (float X, float Y) FindSpawn(World world)
    {
        for (int radius = 0; radius < 256; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                    if (TerrainGenerator.IsWalkable(world.TileAt(dx, dy)))
                        return (dx + 0.5f, dy + 0.5f);
                }
            }
        }
        return (0.5f, 0.5f);
    }
}
