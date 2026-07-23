using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>
/// A committed blueprint under construction: the owner's plan, which of its
/// pieces are already built, and the materials stockpiled on site to build the
/// rest. This is the authoritative "buildground" — the server owns one per
/// blueprint and drives it; the client only predicts.
///
/// Construction is deliberately the harvest loop in reverse. A piece takes a
/// fixed number of strikes to *raise* (mirroring how many it takes to fell a
/// tree); partial strike progress is transient and never persisted; only the
/// completing strike flips a piece to built and consumes its materials from the
/// on-site storage. Pieces build in support order because a piece is only
/// buildable once the things that hold it up are themselves *built* — so a wall
/// waits for its foundation and a roof waits for its walls, with no explicit
/// ordering code beyond the support rule already in <see cref="BuildingRules"/>.
///
/// Single-threaded by contract: the world-server touches a site only from its
/// packet/tick loop, so plain dictionaries suffice and keep iteration
/// deterministic.
/// </summary>
public sealed class BuildSite
{
    private readonly Dictionary<PieceSlot, PlannedPiece> _pieces;
    private readonly HashSet<PieceSlot> _built = new();
    private readonly Dictionary<PieceSlot, int> _strikes = new();
    private readonly Dictionary<ItemId, int> _storage = new();

    public BuildSite(long id, Guid owner, IEnumerable<PlannedPiece> pieces)
    {
        Id = id;
        Owner = owner;
        _pieces = new Dictionary<PieceSlot, PlannedPiece>();
        foreach (var piece in pieces) _pieces[piece.Slot] = piece;
    }

    public long Id { get; }
    public Guid Owner { get; }

    public IReadOnlyCollection<PlannedPiece> Pieces => _pieces.Values;
    public IReadOnlyDictionary<ItemId, int> Storage => _storage;

    public bool IsBuilt(PieceSlot slot) => _built.Contains(slot);

    /// <summary>
    /// True when a built roof covers cell (<paramref name="x"/>, <paramref name="y"/>)
    /// at any storey — so standing under it counts as sheltered. This is what earns
    /// a finished building its keep: a roof answers the night's cold the way a
    /// campfire does, but for a whole footprint rather than a radius. A pending
    /// (unbuilt) roof shelters no one, so the player must actually raise it.
    /// </summary>
    public bool HasBuiltRoofOver(int x, int y)
    {
        foreach (var slot in _built)
            if (slot.Layer == PieceLayer.Cover && slot.X == x && slot.Y == y)
                return true;
        return false;
    }

    /// <summary>True when a built, solid piece (a wall or window) fills the slot —
    /// what the movement rules read to stop a player at a raised wall.</summary>
    public bool IsSolidAt(PieceSlot slot) =>
        _built.Contains(slot)
        && _pieces.TryGetValue(slot, out var piece)
        && StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)
        && def.Solid;

    public int PieceCount => _pieces.Count;
    public int BuiltCount => _built.Count;
    public bool IsComplete => _built.Count == _pieces.Count;

    public IEnumerable<PlannedPiece> BuiltPieces => _pieces.Values.Where(p => _built.Contains(p.Slot));
    public IEnumerable<PlannedPiece> PendingPieces => _pieces.Values.Where(p => !_built.Contains(p.Slot));

    /// <summary>Records a built piece with no validation. Used when replaying from storage.</summary>
    public void LoadBuilt(PieceSlot slot)
    {
        if (_pieces.ContainsKey(slot)) _built.Add(slot);
    }

    /// <summary>Records stockpiled materials with no validation. Used when replaying from storage.</summary>
    public void LoadStorage(ItemId item, int amount)
    {
        if (amount != 0) _storage[item] = amount;
    }

    /// <summary>The full material cost of the whole blueprint, built or not.</summary>
    public IReadOnlyList<MaterialCost> TotalBom() => BuildingRules.BillOfMaterials(_pieces.Values);

    /// <summary>The material cost of just the pieces still to be built.</summary>
    public IReadOnlyList<MaterialCost> RemainingBom() => BuildingRules.BillOfMaterials(PendingPieces);

    /// <summary>
    /// What the site still needs delivered: the remaining bill of materials minus
    /// what is already stockpiled, in catalog order. Empty when every pending
    /// piece can be paid for from storage.
    /// </summary>
    public IReadOnlyList<MaterialCost> MissingMaterials()
    {
        var missing = new List<MaterialCost>();
        foreach (var line in RemainingBom())
        {
            int shortfall = line.Amount - _storage.GetValueOrDefault(line.Item);
            if (shortfall > 0) missing.Add(new MaterialCost(line.Item, shortfall));
        }
        return missing;
    }

    /// <summary>
    /// How much of <paramref name="offered"/> the site will take: never more than
    /// the pieces still need beyond what is already stockpiled, and only for items
    /// the blueprint actually uses. The caller consumes exactly the accepted amount
    /// from the player's inventory, so a player is never overcharged for a wall
    /// they only need three planks for.
    /// </summary>
    public int AcceptableDeposit(ItemId item, int offered)
    {
        if (offered <= 0) return 0;
        int needed = 0;
        foreach (var line in RemainingBom())
            if (line.Item == item) { needed = line.Amount; break; }

        int room = needed - _storage.GetValueOrDefault(item);
        return Math.Clamp(offered, 0, Math.Max(0, room));
    }

    /// <summary>
    /// Deposits up to <paramref name="offered"/> of <paramref name="item"/> into
    /// on-site storage and returns the amount actually taken. Anything the site
    /// cannot use is left with the caller.
    /// </summary>
    public int Deposit(ItemId item, int offered)
    {
        int taken = AcceptableDeposit(item, offered);
        if (taken > 0) _storage[item] = _storage.GetValueOrDefault(item) + taken;
        return taken;
    }

    /// <summary>The outcome of one construction strike, mirroring <see cref="World.HarvestStrike"/>.</summary>
    public readonly record struct BuildStrike(
        bool Acted, PlannedPiece Piece, bool Completed, int StrikesLeft, int StrikesTotal);

    /// <summary>
    /// Applies one construction strike to the next buildable piece — the lowest,
    /// most foundational pending piece whose supports are built and whose materials
    /// are on site. Returns <c>Acted=false</c> when nothing can be built yet
    /// (missing supports or materials). Every strike wears the piece up; the
    /// completing strike consumes its materials and marks it built. The caller
    /// persists the completion; partial strikes are transient.
    /// </summary>
    public BuildStrike TryBuildNext(Func<int, int, bool> groundBuildable)
    {
        var next = NextBuildable(groundBuildable);
        return next is { } piece ? Strike(piece) : default;
    }

    /// <summary>
    /// Applies one construction strike to a specific piece, if it is pending,
    /// supported by what is already built, and paid for from storage. Lets a
    /// client target a ghost directly rather than take the server's next pick.
    /// </summary>
    public BuildStrike TryBuildAt(PieceSlot slot, Func<int, int, bool> groundBuildable)
    {
        if (!_pieces.TryGetValue(slot, out var piece)) return default;
        if (_built.Contains(slot)) return default;
        if (!IsBuildable(piece, groundBuildable)) return default;
        return Strike(piece);
    }

    /// <summary>
    /// The piece the site would build on the next strike, without striking it —
    /// so the caller can reach-check the player against that piece's cell before
    /// authorising the build. Null when nothing can be built yet.
    /// </summary>
    public PlannedPiece? PeekNextBuildable(Func<int, int, bool> groundBuildable) =>
        NextBuildable(groundBuildable);

    private BuildStrike Strike(PlannedPiece piece)
    {
        int total = BuildingRules.HitsToBuild(piece.Kind, piece.Material);
        int struck = _strikes.GetValueOrDefault(piece.Slot) + 1;
        bool completed = struck >= total;

        if (completed)
        {
            _strikes.Remove(piece.Slot);
            _built.Add(piece.Slot);
            ConsumeCost(piece);
        }
        else
        {
            _strikes[piece.Slot] = struck;
        }

        return new BuildStrike(true, piece, completed, Math.Max(0, total - struck), total);
    }

    private void ConsumeCost(PlannedPiece piece)
    {
        if (!StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)) return;
        foreach (var line in def.Cost)
        {
            int left = _storage.GetValueOrDefault(line.Item) - line.Amount;
            if (left > 0) _storage[line.Item] = left;
            else _storage.Remove(line.Item);
        }
    }

    /// <summary>
    /// The next pending piece the site would build, in a fixed order: lower storeys
    /// first, then by structural layer (ground, posts, walls, roof), then by
    /// coordinate. Deterministic so client prediction and server authority agree on
    /// what a tap builds.
    /// </summary>
    private PlannedPiece? NextBuildable(Func<int, int, bool> groundBuildable)
    {
        PlannedPiece? best = null;
        foreach (var piece in _pieces.Values)
        {
            if (_built.Contains(piece.Slot)) continue;
            if (!IsBuildable(piece, groundBuildable)) continue;
            if (best is null || Precedes(piece.Slot, best.Value.Slot)) best = piece;
        }
        return best;
    }

    private bool IsBuildable(PlannedPiece piece, Func<int, int, bool> groundBuildable)
    {
        if (!StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)) return false;
        if (!BuildingRules.IsSupported(piece, BuiltLookup(), groundBuildable)) return false;
        foreach (var line in def.Cost)
            if (_storage.GetValueOrDefault(line.Item) < line.Amount) return false;
        return true;
    }

    private Dictionary<PieceSlot, PlannedPiece> BuiltLookup()
    {
        var lookup = new Dictionary<PieceSlot, PlannedPiece>();
        foreach (var slot in _built)
            if (_pieces.TryGetValue(slot, out var piece)) lookup[slot] = piece;
        return lookup;
    }

    private static bool Precedes(PieceSlot a, PieceSlot b)
    {
        if (a.Level != b.Level) return a.Level < b.Level;
        int ra = LayerRank(a.Layer), rb = LayerRank(b.Layer);
        if (ra != rb) return ra < rb;
        if (a.X != b.X) return a.X < b.X;
        if (a.Y != b.Y) return a.Y < b.Y;
        return a.Layer < b.Layer;
    }

    private static int LayerRank(PieceLayer layer) => layer switch
    {
        PieceLayer.Ground => 0,
        PieceLayer.Post => 1,
        PieceLayer.WallNorth or PieceLayer.WallEast or PieceLayer.WallSouth or PieceLayer.WallWest => 2,
        PieceLayer.Cover => 3,
        _ => 4,
    };
}
