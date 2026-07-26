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

    /// <summary>
    /// Commit a designed blueprint into a build site the player then supplies.
    /// Layout: ushort pieceCount, then each piece as byte kind
    /// (<see cref="BuildPieceKind"/>), byte material (<see cref="BuildMaterial"/>),
    /// int x, int y, int level, byte layer (<see cref="PieceLayer"/>). The server
    /// canonicalises each slot, validates the whole plan against the shared
    /// <c>BuildingRules</c> and the terrain, and on success creates a build site
    /// owned by this character and replies with <see cref="BlueprintState"/>. An
    /// illegal or empty plan is dropped.
    /// </summary>
    CommitBlueprint = 9,

    /// <summary>
    /// Deposit materials from inventory into a build site's on-site storage.
    /// Layout: long siteId, byte item id, int amount. The server takes only what
    /// the site's pending pieces still need and only what the player actually
    /// holds; the reduced inventory and updated site come back as the usual
    /// inventory and <see cref="BlueprintState"/> updates. Only the owner may
    /// supply their own site.
    /// </summary>
    DepositRequest = 10,

    /// <summary>
    /// Advance construction at a build site by one strike on its next buildable
    /// piece. Layout: long siteId. The server picks the lowest, most foundational
    /// pending piece whose supports are built and whose materials are on site,
    /// checks the player is within reach of it, and strikes it — broadcasting
    /// <see cref="BuildProgress"/>. Only the owner may build their own site.
    /// </summary>
    BuildRequest = 11,

    /// <summary>
    /// Tear down a build site: refund its stockpiled materials to the owner and
    /// remove every piece, built or pending. Layout: long siteId. The server
    /// broadcasts <see cref="BuildSiteRemoved"/>. Only the owner may cancel.
    /// </summary>
    CancelBlueprint = 12,

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

    /// <summary>
    /// The full state of one build site, sent only to its owner: the private
    /// hologram plus construction and stockpile progress. Layout: long siteId,
    /// ushort pieceCount, then each piece as byte kind, byte material, int x,
    /// int y, int level, byte layer, byte built; then byte storageCount, then
    /// each stored line as byte item id, int amount. Sent on commit, on deposit
    /// and on a piece completing, and backfilled for the owner's sites on join.
    /// </summary>
    BlueprintState = 112,

    /// <summary>
    /// One construction strike landed on a piece. Layout: long siteId, byte kind,
    /// byte material, int x, int y, int level, byte layer, byte strikesLeft, byte
    /// strikesTotal, byte completed. A completed strike is broadcast to everyone so
    /// the built piece appears in the shared world; a partial strike goes only to
    /// the owner, since unbuilt pieces are the owner's private plan.
    /// </summary>
    BuildProgress = 113,

    /// <summary>
    /// A build site was torn down; clients drop all of its pieces. Layout: long
    /// siteId. Broadcast, because others may have seen its built pieces.
    /// </summary>
    BuildSiteRemoved = 114,
}

public static class ProtocolVersion
{
    /// <summary>Bumped whenever message layout changes. Mismatched peers are rejected.</summary>
    public const int Current = 11;
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

/// <summary>
/// A category of building piece in the modular blueprint system. Values are
/// wire-stable — append only, never renumber — because a committed blueprint
/// stores them and streams them to the client.
/// </summary>
public enum BuildPieceKind : byte
{
    None = 0,
    /// <summary>Sits on the ground; everything else builds off it.</summary>
    Foundation = 1,
    /// <summary>A solid wall on a cell edge; blocks movement, encloses.</summary>
    Wall = 2,
    /// <summary>A wall with a passable gap; encloses without blocking.</summary>
    Doorway = 3,
    /// <summary>A wall with a glazed opening; blocks movement, encloses.</summary>
    Window = 4,
    /// <summary>A walkable surface at an upper level; the floor of a storey above.</summary>
    Floor = 5,
    /// <summary>A vertical post; carries a roof or upper floor without a full wall.</summary>
    Pillar = 6,
    /// <summary>A cover over a cell; the piece that makes a space count as sheltered.</summary>
    Roof = 7,
}

/// <summary>
/// The material a piece is built from — its tier and look. Wire-stable, append
/// only. The concrete item cost of a (<see cref="BuildPieceKind"/>,
/// <see cref="BuildMaterial"/>) pair lives in the sim-core structure catalog, so
/// the wire only ever carries the pair, never the cost.
/// </summary>
public enum BuildMaterial : byte
{
    None = 0,
    Wood = 1,
    Stone = 2,
    /// <summary>Woven reeds/fiber — a cheap early roof, raised before planks exist.</summary>
    Thatch = 3,
}

/// <summary>
/// Where in a cell a piece sits. A cell holds at most one piece per layer, which
/// is what lets four walls, a floor, a roof and a post coexist on one tile
/// without ambiguity. Wall layers name a cell *edge*; the edge shared by two
/// cells is one physical wall, so slots are canonicalised (see the sim-core
/// <c>PieceSlot</c>) to a single owner. Wire-stable, append only.
/// </summary>
public enum PieceLayer : byte
{
    None = 0,
    /// <summary>Foundation or floor — the walkable base of the cell.</summary>
    Ground = 1,
    WallNorth = 2,
    WallEast = 3,
    WallSouth = 4,
    WallWest = 5,
    /// <summary>A roof covering the cell.</summary>
    Cover = 6,
    /// <summary>A pillar/post at the cell centre.</summary>
    Post = 7,
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

    /// <summary>A full day/night cycle, in real seconds.</summary>
    public const int SecondsPerGameDay = 600;

    /// <summary>Heartbeat cadence for survival meters when nothing changed.</summary>
    public const int StatsHeartbeatTicks = TicksPerSecond * 2;

    /// <summary>
    /// Max time to wait on a gateway HTTP call before giving up. Join and voyage
    /// handling deliberately block the world-server's single tick loop (see
    /// Program.cs), so this bounds how long a gateway hiccup can stall every
    /// connected player, rather than the ~100s HttpClient default.
    /// </summary>
    public const int GatewayTimeoutSeconds = 5;
}
