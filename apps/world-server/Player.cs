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

    /// <summary>
    /// The device UUID this player's character is stored under in the gateway.
    /// Unknown until the Hello handshake arrives, so it is settable.
    /// </summary>
    public Guid CharacterId { get; set; }

    /// <summary>Last accepted position, in metres.</summary>
    public Vec3 Position { get; private set; }

    /// <summary>Facing, in radians, for rendering other players.</summary>
    public float Yaw { get; private set; }

    /// <summary>Server time of the last accepted position, for speed budgeting.</summary>
    public double LastAcceptedAt { get; private set; }

    public int Rejections { get; private set; }

    public Dictionary<ItemId, int> Inventory { get; } = new();
    public bool InventoryDirty { get; set; }

    /// <summary>Authoritative survival meters. Spawns full; drains on the tick.</summary>
    public SurvivalRules.SurvivalState Survival { get; private set; } = SurvivalRules.SurvivalState.Full;

    public void Give(ItemId item, int amount)
    {
        Inventory[item] = Inventory.GetValueOrDefault(item) + amount;
        InventoryDirty = true;
    }

    /// <summary>True when the player holds at least one of <paramref name="item"/>.</summary>
    public bool Has(ItemId item, int amount = 1) => Inventory.GetValueOrDefault(item) >= amount;

    /// <summary>
    /// The tier of the best held tool of class <paramref name="cls"/>, or 0 when
    /// the player holds none (or <paramref name="cls"/> is None). Used to award a
    /// harvest bonus for the right tool.
    /// </summary>
    public int ToolTierFor(ToolClass cls)
    {
        if (cls == ToolClass.None) return 0;

        int best = 0;
        foreach (var (item, count) in Inventory)
        {
            if (count <= 0 || !ItemCatalog.TryGet(item, out var def) || def.Tool != cls) continue;
            if (def.ToolTier > best) best = def.ToolTier;
        }
        return best;
    }

    /// <summary>
    /// Removes one of <paramref name="item"/>. The caller has already checked the
    /// player holds it, so an empty stack here would be a server-side invariant
    /// violation — fail loudly rather than clamp.
    /// </summary>
    public void ConsumeOne(ItemId item)
    {
        int remaining = Inventory.GetValueOrDefault(item) - 1;
        if (remaining < 0)
            throw new InvalidOperationException(
                $"Player {Id} spent {item} they did not hold.");

        if (remaining == 0) Inventory.Remove(item);
        else Inventory[item] = remaining;
        InventoryDirty = true;
    }

    /// <summary>
    /// Removes <paramref name="amount"/> of <paramref name="item"/>. The caller has
    /// already checked the player holds at least that many — depositing more than
    /// is held would be a server-side invariant violation, so fail loudly.
    /// </summary>
    public void Take(ItemId item, int amount)
    {
        if (amount <= 0) return;
        int remaining = Inventory.GetValueOrDefault(item) - amount;
        if (remaining < 0)
            throw new InvalidOperationException(
                $"Player {Id} spent {amount} {item} they did not hold.");

        if (remaining == 0) Inventory.Remove(item);
        else Inventory[item] = remaining;
        InventoryDirty = true;
    }

    /// <summary>
    /// Drains survival meters by whole ticks using the shared rules.
    /// <paramref name="warm"/> is whether the player is warm this interval —
    /// false when exposed at night, which drains warmth and, once it empties,
    /// health.
    /// </summary>
    public void AdvanceSurvival(int ticks, bool warm) =>
        Survival = SurvivalRules.Advance(Survival, ticks, warm: warm);

    /// <summary>
    /// Eats one <paramref name="item"/> if the player holds it and it is edible,
    /// restoring hunger by the catalogued food value. Returns whether anything was
    /// eaten, so the caller can ignore a non-food or absent item without effect.
    /// </summary>
    public bool Eat(ItemId item)
    {
        if (!ItemCatalog.IsFood(item) || !Has(item)) return false;

        ConsumeOne(item);
        Survival = SurvivalRules.Eat(Survival, ItemCatalog.Of(item).FoodValue);
        return true;
    }

    /// <summary>
    /// Seeds this player from a character loaded out of the gateway: its stored
    /// inventory and survival meters replace the defaults. Called once at Hello.
    /// </summary>
    public void LoadCharacter(CharacterState character)
    {
        Inventory.Clear();
        foreach (var (name, amount) in character.Inventory)
        {
            if (amount > 0 && Enum.TryParse<ItemId>(name, out var item))
                Inventory[item] = amount;
        }
        Survival = SurvivalRules.FromPoints(
            character.Hunger, character.Stamina, character.Health, character.Warmth);
        InventoryDirty = true;
    }

    /// <summary>Snapshots the persistent character state for saving to the gateway.</summary>
    public CharacterState ToCharacterState() => new()
    {
        Inventory = Inventory.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        Hunger = Survival.HungerPoints,
        Stamina = Survival.StaminaPoints,
        Health = Survival.HealthPoints,
        Warmth = Survival.WarmthPoints,
    };

    /// <summary>
    /// Folds crafting deltas into the inventory: negative entries consume inputs,
    /// the positive entry adds the output. The caller has already checked the
    /// craft is affordable, so a negative result would be a server-side invariant
    /// violation — fail loudly rather than clamp.
    /// </summary>
    public void ApplyCraft(IReadOnlyList<CraftingRules.ItemDelta> deltas)
    {
        foreach (var delta in deltas)
        {
            int remaining = Inventory.GetValueOrDefault(delta.Item) + delta.Change;
            if (remaining < 0)
                throw new InvalidOperationException(
                    $"Craft consumed more {delta.Item} than player {Id} held.");

            if (remaining == 0) Inventory.Remove(delta.Item);
            else Inventory[delta.Item] = remaining;
        }
        InventoryDirty = true;
    }

    /// <summary>
    /// Validates a reported position. Returns the rejection reason, or None if
    /// the move was accepted and <see cref="Position"/> updated.
    /// </summary>
    public MoveRejection TryAccept(
        TerrainGenerator terrain, Vec3 reported, float yaw, double now, IMovementObstacles? obstacles = null)
    {
        // Budget is elapsed time since the last *accepted* position, so
        // spamming updates cannot buy extra distance.
        double delta = now - LastAcceptedAt;
        var rejection = MovementRules.Check(terrain, Position, reported, delta, obstacles);

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
