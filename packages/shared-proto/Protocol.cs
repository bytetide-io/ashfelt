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

    // server -> client
    Welcome = 100,
    ChunkData = 101,
}

public static class ProtocolVersion
{
    public const int Current = 1;
}
