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

    /// <summary>A reported position was rejected; snap back to this one.</summary>
    Correction = 106,
}

public static class ProtocolVersion
{
    /// <summary>Bumped whenever message layout changes. Mismatched peers are rejected.</summary>
    public const int Current = 3;
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

    /// <summary>How often the client reports its position.</summary>
    public const int ClientStateHz = 15;

    /// <summary>How far a player may be from a tile to harvest it, in metres.</summary>
    public const float ChopRangeMetres = 5.0f;

    /// <summary>Radius, in chunks, of the area a client is kept informed about.</summary>
    public const int InterestRadiusChunks = 1;
}
