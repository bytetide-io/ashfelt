namespace Ashfall.Proto;

/// <summary>
/// Wire message identifiers. Client and server both read these from this
/// package — never redeclare them locally.
/// </summary>
public enum MessageId : byte
{
    // client -> server
    Hello = 1,
    RequestChunk = 2,
    /// <summary>Player input. A request, never a state change — the server decides.</summary>
    MoveIntent = 3,
    /// <summary>Ask to harvest the tile at the given world coordinate.</summary>
    ChopRequest = 4,

    // server -> client
    Welcome = 100,
    ChunkData = 101,
    /// <summary>Authoritative snapshot of every player in interest range.</summary>
    PlayerStates = 102,
    PlayerLeft = 103,
    /// <summary>A tile diff was applied; clients overlay it on generated terrain.</summary>
    TileChanged = 104,
    /// <summary>Authoritative inventory for the receiving player.</summary>
    InventoryUpdate = 105,
}

public static class ProtocolVersion
{
    /// <summary>Bumped whenever message layout changes. Mismatched peers are rejected.</summary>
    public const int Current = 2;
}

/// <summary>Item kinds. Values are wire-stable — append only, never renumber.</summary>
public enum ItemId : byte
{
    None = 0,
    Wood = 1,
    Stone = 2,
}

public static class Tuning
{
    /// <summary>Server tick rate for simulation and state broadcast.</summary>
    public const int TicksPerSecond = 15;

    /// <summary>Player movement speed, in tiles per second.</summary>
    public const float MoveTilesPerSecond = 4.0f;

    /// <summary>How far a player may be from a tile to harvest it, in tiles.</summary>
    public const float ChopRangeTiles = 2.0f;

    /// <summary>Radius, in chunks, of the area a client is kept informed about.</summary>
    public const int InterestRadiusChunks = 1;
}
