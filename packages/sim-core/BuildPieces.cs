using Ashfall.Proto;

namespace Ashfall.SimCore;

/// <summary>A cell in the building grid: a tile column at a vertical storey.</summary>
public readonly record struct BuildCoord(int X, int Y, int Level);

/// <summary>
/// The canonical address of one piece: a cell, a storey, and the layer it fills.
/// Two adjacent cells share one physical edge, so a wall on the north edge of a
/// cell is the very same slot as the wall on the south edge of the cell above.
/// <see cref="Canonical"/> collapses those aliases to a single owner (always the
/// north or east form) so a plan can never hold two pieces for one wall, and the
/// server can key built pieces without ambiguity.
/// </summary>
public readonly record struct PieceSlot(int X, int Y, int Level, PieceLayer Layer)
{
    /// <summary>
    /// The slot for <paramref name="layer"/> of cell (<paramref name="x"/>,
    /// <paramref name="y"/>) at <paramref name="level"/>, folded to its canonical
    /// owner. South/West edges are rewritten to the North/East edge of the
    /// neighbour they share, so aliasing edges resolve to one slot.
    /// </summary>
    public static PieceSlot Canonical(int x, int y, int level, PieceLayer layer) => layer switch
    {
        // +Y is north: the south edge of (x,y) is the north edge of (x, y-1).
        PieceLayer.WallSouth => new PieceSlot(x, y - 1, level, PieceLayer.WallNorth),
        // +X is east: the west edge of (x,y) is the east edge of (x-1, y).
        PieceLayer.WallWest => new PieceSlot(x - 1, y, level, PieceLayer.WallEast),
        _ => new PieceSlot(x, y, level, layer),
    };

    public BuildCoord Cell => new(X, Y, Level);

    public bool IsWall => Layer is PieceLayer.WallNorth or PieceLayer.WallEast
        or PieceLayer.WallSouth or PieceLayer.WallWest;

    /// <summary>
    /// The two cells a wall slot separates. Only defined for the canonical wall
    /// layers (North/East); other layers return the slot's own cell twice.
    /// </summary>
    public (BuildCoord A, BuildCoord B) BorderCells() => Layer switch
    {
        PieceLayer.WallNorth => (new BuildCoord(X, Y, Level), new BuildCoord(X, Y + 1, Level)),
        PieceLayer.WallEast => (new BuildCoord(X, Y, Level), new BuildCoord(X + 1, Y, Level)),
        _ => (Cell, Cell),
    };

    /// <summary>
    /// The four canonical wall slots that ring a cell, in a fixed order. Used to
    /// ask whether a cell is walled enough to carry a roof.
    /// </summary>
    public static IEnumerable<PieceSlot> WallsAround(BuildCoord cell)
    {
        yield return Canonical(cell.X, cell.Y, cell.Level, PieceLayer.WallNorth);
        yield return Canonical(cell.X, cell.Y, cell.Level, PieceLayer.WallEast);
        yield return Canonical(cell.X, cell.Y, cell.Level, PieceLayer.WallSouth);
        yield return Canonical(cell.X, cell.Y, cell.Level, PieceLayer.WallWest);
    }
}

/// <summary>
/// One piece as it appears in a plan or on the world: its kind, its material and
/// the canonical slot it fills. Construction state (pending vs built) lives with
/// the blueprint, not here — a <see cref="PlannedPiece"/> is the immutable "what
/// and where", reused for the draft, the committed hologram and the built result.
/// </summary>
public readonly record struct PlannedPiece(BuildPieceKind Kind, BuildMaterial Material, PieceSlot Slot);
