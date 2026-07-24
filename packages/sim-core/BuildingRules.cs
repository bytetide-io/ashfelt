using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>Why a single piece cannot stand where it was placed.</summary>
public enum PieceProblem : byte
{
    None = 0,
    /// <summary>Another piece already fills this exact slot.</summary>
    DuplicateSlot,
    /// <summary>The piece kind cannot sit on the layer it was given.</summary>
    LayerMismatch,
    /// <summary>Nothing beneath or beside it holds it up.</summary>
    Unsupported,
    /// <summary>No such (kind, material) piece exists in the catalog.</summary>
    UnknownPiece,
}

/// <summary>
/// The deterministic rules that decide whether a set of pieces forms a legal
/// building and what it costs to raise. Shared so the architect view can red-flag
/// an illegal ghost exactly as the server will reject it at commit (invariant #4);
/// pure integer logic with no iteration-order dependence (invariant #5).
/// </summary>
public static class BuildingRules
{
    /// <summary>True when a piece kind is allowed on <paramref name="layer"/>.</summary>
    public static bool KindMatchesLayer(BuildPieceKind kind, PieceLayer layer) => kind switch
    {
        BuildPieceKind.Foundation or BuildPieceKind.Floor => layer == PieceLayer.Ground,
        BuildPieceKind.Wall or BuildPieceKind.Doorway or BuildPieceKind.Window =>
            layer is PieceLayer.WallNorth or PieceLayer.WallEast,
        BuildPieceKind.Pillar => layer == PieceLayer.Post,
        BuildPieceKind.Roof => layer == PieceLayer.Cover,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="piece"/> is held up, given every other piece in the
    /// plan (<paramref name="present"/>) and a test for whether the terrain under a
    /// cell can be built on (<paramref name="groundBuildable"/>). Support is judged
    /// against the whole plan, not the build order — the server enforces order at
    /// construction time by refusing a piece whose supports are not yet *built*.
    /// </summary>
    public static bool IsSupported(
        PlannedPiece piece,
        IReadOnlyDictionary<PieceSlot, PlannedPiece> present,
        Func<int, int, bool> groundBuildable)
    {
        if (!StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)) return false;
        var slot = piece.Slot;

        switch (def.Support)
        {
            case SupportKind.Ground:
                return slot.Level == 0 && groundBuildable(slot.X, slot.Y);

            case SupportKind.Below:
                return slot.Level == 0
                    ? groundBuildable(slot.X, slot.Y)
                    : HasGroundPiece(present, new BuildCoord(slot.X, slot.Y, slot.Level - 1));

            case SupportKind.Foundation:
            {
                var (a, b) = slot.BorderCells();
                return HasGroundPiece(present, a) || HasGroundPiece(present, b);
            }

            case SupportKind.WallOrPost:
            {
                var cell = slot.Cell;
                if (present.ContainsKey(new PieceSlot(cell.X, cell.Y, cell.Level, PieceLayer.Post))) return true;
                foreach (var wall in PieceSlot.WallsAround(cell))
                    if (present.ContainsKey(wall)) return true;
                return false;
            }

            default:
                return false;
        }
    }

    private static bool HasGroundPiece(IReadOnlyDictionary<PieceSlot, PlannedPiece> present, BuildCoord cell) =>
        present.ContainsKey(new PieceSlot(cell.X, cell.Y, cell.Level, PieceLayer.Ground));

    /// <summary>The outcome of validating a whole plan: legal only when no piece has a problem.</summary>
    public readonly record struct PlanValidation(bool Ok, IReadOnlyList<KeyValuePair<PlannedPiece, PieceProblem>> Problems);

    /// <summary>
    /// Validate an entire plan: every piece must be a real catalog piece on a
    /// layer its kind allows, occupy a unique slot, and be supported by the rest of
    /// the plan. Returns each offending piece with its reason so the client can
    /// paint just the bad ghosts red.
    /// </summary>
    public static PlanValidation Validate(
        IReadOnlyList<PlannedPiece> pieces,
        Func<int, int, bool> groundBuildable)
    {
        var bySlot = new Dictionary<PieceSlot, PlannedPiece>();
        var duplicates = new List<PlannedPiece>();
        foreach (var piece in pieces)
        {
            if (!bySlot.TryAdd(piece.Slot, piece)) duplicates.Add(piece);
        }

        var problems = new List<KeyValuePair<PlannedPiece, PieceProblem>>();
        foreach (var piece in duplicates)
            problems.Add(new(piece, PieceProblem.DuplicateSlot));

        foreach (var piece in pieces)
        {
            if (!StructureCatalog.TryGet(piece.Kind, piece.Material, out _))
            {
                problems.Add(new(piece, PieceProblem.UnknownPiece));
                continue;
            }
            if (!KindMatchesLayer(piece.Kind, piece.Slot.Layer))
            {
                problems.Add(new(piece, PieceProblem.LayerMismatch));
                continue;
            }
            if (!IsSupported(piece, bySlot, groundBuildable))
                problems.Add(new(piece, PieceProblem.Unsupported));
        }

        return new PlanValidation(problems.Count == 0, problems);
    }

    /// <summary>
    /// The total materials a set of pieces consumes, aggregated by item and
    /// returned in catalog order — the build site's bill of materials. Order is
    /// fixed by <see cref="ItemCatalog.All"/>, so the same pieces always produce
    /// the same manifest regardless of the order they were placed.
    /// </summary>
    public static IReadOnlyList<MaterialCost> BillOfMaterials(IEnumerable<PlannedPiece> pieces)
    {
        var totals = new Dictionary<ItemId, int>();
        foreach (var piece in pieces)
        {
            if (!StructureCatalog.TryGet(piece.Kind, piece.Material, out var def)) continue;
            foreach (var line in def.Cost)
                totals[line.Item] = totals.GetValueOrDefault(line.Item) + line.Amount;
        }

        var bom = new List<MaterialCost>();
        foreach (var def in ItemCatalog.All)
            if (totals.TryGetValue(def.Id, out var amount) && amount > 0)
                bom.Add(new MaterialCost(def.Id, amount));
        return bom;
    }

    /// <summary>Strikes to raise a single piece, from its catalog definition.</summary>
    public static int HitsToBuild(BuildPieceKind kind, BuildMaterial material) =>
        StructureCatalog.TryGet(kind, material, out var def) ? def.BuildStrikes : 0;
}
