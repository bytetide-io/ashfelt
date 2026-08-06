namespace Ashfall.Proto;

/// <summary>
/// Wire message identifiers. Client and server both read these from this
/// package — never redeclare them locally.
/// </summary>
public enum MessageId : byte
{
    // client -> server
    /// <summary>
    /// First message on connect. Layout: int protocol version, then the 16-byte
    /// character UUID (the client's device id, in <c>Guid.ToByteArray</c> order),
    /// then a length-prefixed voyage ticket string. The UUID tells the
    /// world-server which character to load from the gateway; the ticket is empty
    /// on a normal join and non-empty when arriving via a voyage — in which case
    /// the server validates and consumes it (POST /voyage/claim) to take ownership
    /// before loading the character. An invalid ticket disconnects the client.
    /// </summary>
    Hello = 1,
    RequestChunk = 2,
    /// <summary>
    /// Reported position after client-side physics. The server validates it
    /// against the shared height field and corrects when implausible.
    ///
    /// Position is the body *centre*, not the feet — remote clients render a
    /// capsule at that point directly, so a feet-anchored value renders half
    /// underground.
    /// </summary>
    ClientState = 3,
    /// <summary>Ask to harvest the tile at the given world coordinate.</summary>
    ChopRequest = 4,
    /// <summary>
    /// Ask to craft the given <see cref="ItemId"/>. The server authorises it
    /// against the shared <c>CraftingRules</c> and the player's inventory.
    /// </summary>
    CraftRequest = 5,
    /// <summary>
    /// Ask to place a structure at a world tile by spending one placeable
    /// <see cref="ItemId"/> from inventory. Layout: byte kind, int tileX,
    /// int tileY. The server authorises it against <c>PlacementRules</c>, the
    /// world state and the player's inventory.
    /// </summary>
    PlaceRequest = 6,

    /// <summary>
    /// Ask the current world-server to release this character so it can voyage to
    /// another world. Layout: length-prefixed target world id string. The server
    /// synchronously saves the authoritative character to the gateway, marks it
    /// in-transit and mints a ticket (POST /voyage), removes the entity, and
    /// replies with <see cref="ReleaseGranted"/> — or <see cref="ReleaseDenied"/>
    /// on any failure, in which case the player stays in this world.
    /// </summary>
    RequestRelease = 7,

    /// <summary>
    /// Ask to eat one edible <see cref="ItemId"/> from inventory. Layout: byte
    /// item id. The server authorises it against the item's food value in the
    /// shared <c>ItemCatalog</c>, consumes one, and restores hunger via
    /// <c>SurvivalRules.Eat</c>. A non-food or absent item is ignored.
    /// </summary>
    EatRequest = 8,

    // server -> client
    Welcome = 100,
    ChunkData = 101,
    /// <summary>Authoritative snapshot of every player in interest range.</summary>
    PlayerStates = 102,
    PlayerLeft = 103,
    /// <summary>
    /// A node was struck but not yet felled. Layout: int tileX, int tileY, byte
    /// strikes remaining, byte strikes total. Broadcast on every non-felling
    /// harvest strike so watchers can show the node wearing down; the felling
    /// strike sends <see cref="TileChanged"/> instead. Purely presentational —
    /// partial progress is transient server state, never a persisted diff.
    /// </summary>
    HarvestProgress = 111,

    /// <summary>A tile diff was applied; clients overlay it on generated terrain.</summary>
    TileChanged = 104,
    /// <summary>Authoritative inventory for the receiving player.</summary>
    InventoryUpdate = 105,

    /// <summary>A reported position was rejected; snap back to this one.</summary>
    Correction = 106,

    /// <summary>
    /// The receiving player's authoritative survival meters (hunger, stamina,
    /// health, warmth) in display points, plus the world time-of-day. Layout:
    /// four ints then a float. Sent on change and on a slow heartbeat so a dropped
    /// packet self-heals.
    /// </summary>
    StatsUpdate = 107,

    /// <summary>
    /// A structure exists in the world; the client renders it. Sent both as the
    /// per-structure backfill when a player joins and as a live broadcast when
    /// one is placed. Layout: long id, byte kind, int tileX, int tileY.
    /// </summary>
    StructurePlaced = 108,

    /// <summary>
    /// The voyage was authorised: the character is now in-transit toward the
    /// target world and no longer owned by this server. Layout: length-prefixed
    /// target host string, int target port, length-prefixed single-use ticket.
    /// The client disconnects and reconnects to the target, sending the ticket in
    /// its <see cref="Hello"/>. The character state has already been persisted to
    /// the gateway; the client carries only the ticket, never an inventory.
    /// </summary>
    ReleaseGranted = 109,

    /// <summary>
    /// The voyage could not be authorised (unknown target world, or the gateway
    /// was unreachable). Layout: length-prefixed reason string. The player remains
    /// connected to and owned by this world-server — nothing was released.
    /// </summary>
    ReleaseDenied = 110,
}

public static class ProtocolVersion
{
    /// <summary>Bumped whenever message layout changes. Mismatched peers are rejected.</summary>
    public const int Current = 10;
}

/// <summary>Item kinds. Values are wire-stable — append only, never renumber.</summary>
public enum ItemId : byte
{
    None = 0,
    Wood = 1,
    Stone = 2,
    Fiber = 3,
    Plank = 4,
    Rope = 5,
    Axe = 6,
    Pickaxe = 7,
    Wall = 8,
    Campfire = 9,
    /// <summary>Edible forage from a berry bush; restores hunger when eaten.</summary>
    Berry = 10,
}

public static class Tuning
{
    /// <summary>Server tick rate for simulation and state broadcast.</summary>
    public const int TicksPerSecond = 15;

    /// <summary>How often the client reports its position.</summary>
    public const int ClientStateHz = 15;

    /// <summary>How far a player may be from a tile to harvest it, in metres.</summary>
    public const float ChopRangeMetres = 5.0f;

    /// <summary>Radius, in chunks, of the area a client is kept informed about.</summary>
    public const int InterestRadiusChunks = 1;

    /// <summary>
    /// How far, in metres, one player's position/yaw is broadcast to another.
    /// InterestRadiusChunks(1) * ChunkSize(32) * TileMetres(2) — kept as a literal
    /// here because shared-proto cannot reference sim-core's TerrainGenerator
    /// without a circular project reference.
    /// </summary>
    public const double InterestRadiusMetres = 64.0;

    /// <summary>A full day/night cycle, in real seconds.</summary>
    public const int SecondsPerGameDay = 600;

    /// <summary>Heartbeat cadence for survival meters when nothing changed.</summary>
    public const int StatsHeartbeatTicks = TicksPerSecond * 2;
}
