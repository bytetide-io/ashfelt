using System.Collections.Generic;
using System.Linq;
using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// The building rules decide, deterministically, whether a plan stands and what
/// it costs. These are the shared truth the architect view predicts against and
/// the server enforces, so support, canonical slots and the bill of materials are
/// pinned here.
/// </summary>
public class BuildingRulesTests
{
    private static PieceSlot GroundSlot(int x, int y, int level = 0) => new(x, y, level, PieceLayer.Ground);

    private static PlannedPiece Foundation(int x, int y, BuildMaterial mat = BuildMaterial.Wood) =>
        new(BuildPieceKind.Foundation, mat, GroundSlot(x, y));

    private static PlannedPiece WallOn(int x, int y, PieceLayer edge, BuildMaterial mat = BuildMaterial.Wood) =>
        new(BuildPieceKind.Wall, mat, PieceSlot.Canonical(x, y, 0, edge));

    /// <summary>Everything is buildable ground unless a test says otherwise.</summary>
    private static bool AnyGround(int x, int y) => true;

    [Fact]
    public void SouthEdge_CanonicalisesToTheNorthEdgeOfTheCellBelow()
    {
        var south = PieceSlot.Canonical(5, 5, 0, PieceLayer.WallSouth);
        var northBelow = PieceSlot.Canonical(5, 4, 0, PieceLayer.WallNorth);
        Assert.Equal(northBelow, south);
    }

    [Fact]
    public void WestEdge_CanonicalisesToTheEastEdgeOfTheCellToTheLeft()
    {
        var west = PieceSlot.Canonical(5, 5, 0, PieceLayer.WallWest);
        var eastLeft = PieceSlot.Canonical(4, 5, 0, PieceLayer.WallEast);
        Assert.Equal(eastLeft, west);
    }

    [Fact]
    public void TwoWalls_OnTheSameSharedEdge_AreTheSameSlot_SoAPlanRejectsTheDuplicate()
    {
        // A wall on the north edge of (0,0) and the south edge of (0,1) are one wall.
        var pieces = new List<PlannedPiece>
        {
            Foundation(0, 0),
            WallOn(0, 0, PieceLayer.WallNorth),
            WallOn(0, 1, PieceLayer.WallSouth),
        };

        var result = BuildingRules.Validate(pieces, AnyGround);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Value == PieceProblem.DuplicateSlot);
    }

    [Fact]
    public void Foundation_OnBuildableGround_IsSupported()
    {
        var present = new Dictionary<PieceSlot, PlannedPiece>();
        Assert.True(BuildingRules.IsSupported(Foundation(2, 2), present, AnyGround));
    }

    [Fact]
    public void Foundation_OnUnbuildableGround_IsNotSupported()
    {
        var present = new Dictionary<PieceSlot, PlannedPiece>();
        bool NoGround(int x, int y) => false;
        Assert.False(BuildingRules.IsSupported(Foundation(2, 2), present, NoGround));
    }

    [Fact]
    public void Wall_NeedsAFoundationInOneOfTheCellsItBorders()
    {
        var wall = WallOn(0, 0, PieceLayer.WallNorth); // borders (0,0) and (0,1)

        var without = new Dictionary<PieceSlot, PlannedPiece>();
        Assert.False(BuildingRules.IsSupported(wall, without, AnyGround));

        var with = new Dictionary<PieceSlot, PlannedPiece> { [GroundSlot(0, 1)] = Foundation(0, 1) };
        Assert.True(BuildingRules.IsSupported(wall, with, AnyGround));
    }

    [Fact]
    public void Roof_NeedsAWallOrPostOnItsCell()
    {
        var roof = new PlannedPiece(BuildPieceKind.Roof, BuildMaterial.Wood, new PieceSlot(0, 0, 0, PieceLayer.Cover));

        var bare = new Dictionary<PieceSlot, PlannedPiece>();
        Assert.False(BuildingRules.IsSupported(roof, bare, AnyGround));

        var walled = new Dictionary<PieceSlot, PlannedPiece>
        {
            [PieceSlot.Canonical(0, 0, 0, PieceLayer.WallNorth)] = WallOn(0, 0, PieceLayer.WallNorth),
        };
        Assert.True(BuildingRules.IsSupported(roof, walled, AnyGround));

        var posted = new Dictionary<PieceSlot, PlannedPiece>
        {
            [new PieceSlot(0, 0, 0, PieceLayer.Post)] =
                new PlannedPiece(BuildPieceKind.Pillar, BuildMaterial.Wood, new PieceSlot(0, 0, 0, PieceLayer.Post)),
        };
        Assert.True(BuildingRules.IsSupported(roof, posted, AnyGround));
    }

    [Fact]
    public void AWholeHut_FoundationWallsAndRoof_ValidatesClean()
    {
        var pieces = new List<PlannedPiece>
        {
            Foundation(0, 0),
            WallOn(0, 0, PieceLayer.WallNorth),
            WallOn(0, 0, PieceLayer.WallEast),
            WallOn(0, 0, PieceLayer.WallSouth),
            WallOn(0, 0, PieceLayer.WallWest),
            new(BuildPieceKind.Roof, BuildMaterial.Wood, new PieceSlot(0, 0, 0, PieceLayer.Cover)),
        };

        var result = BuildingRules.Validate(pieces, AnyGround);

        Assert.True(result.Ok, string.Join(", ", result.Problems.Select(p => $"{p.Key.Kind}:{p.Value}")));
    }

    [Fact]
    public void AWallWithNoFoundationAnywhere_IsFlaggedUnsupported()
    {
        var pieces = new List<PlannedPiece> { WallOn(0, 0, PieceLayer.WallNorth) };

        var result = BuildingRules.Validate(pieces, AnyGround);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Value == PieceProblem.Unsupported);
    }

    [Fact]
    public void AKindOnTheWrongLayer_IsFlaggedLayerMismatch()
    {
        // A foundation forced onto a wall edge is nonsense.
        var bogus = new PlannedPiece(BuildPieceKind.Foundation, BuildMaterial.Wood,
            new PieceSlot(0, 0, 0, PieceLayer.WallNorth));

        var result = BuildingRules.Validate(new List<PlannedPiece> { bogus }, AnyGround);

        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Value == PieceProblem.LayerMismatch);
    }

    [Fact]
    public void BillOfMaterials_AggregatesByItem_InCatalogOrder()
    {
        var pieces = new List<PlannedPiece>
        {
            Foundation(0, 0),                          // Wood 4
            WallOn(0, 0, PieceLayer.WallNorth),        // Plank 3
            WallOn(0, 0, PieceLayer.WallEast),         // Plank 3
        };

        var bom = BuildingRules.BillOfMaterials(pieces);

        // Aggregated: Wood 4, Plank 6. Catalog order puts Wood before Plank.
        Assert.Equal(new[] { ItemId.Wood, ItemId.Plank }, bom.Select(c => c.Item).ToArray());
        Assert.Equal(4, bom.Single(c => c.Item == ItemId.Wood).Amount);
        Assert.Equal(6, bom.Single(c => c.Item == ItemId.Plank).Amount);
    }

    [Fact]
    public void BillOfMaterials_IsIndependentOfPieceOrder()
    {
        var a = new List<PlannedPiece> { Foundation(0, 0), WallOn(0, 0, PieceLayer.WallNorth) };
        var b = new List<PlannedPiece> { WallOn(0, 0, PieceLayer.WallNorth), Foundation(0, 0) };

        Assert.Equal(BuildingRules.BillOfMaterials(a), BuildingRules.BillOfMaterials(b));
    }

    [Fact]
    public void HitsToBuild_ReportsTheDefinitionsStrikeCount()
    {
        Assert.Equal(StructureCatalog.Of(BuildPieceKind.Wall, BuildMaterial.Wood).BuildStrikes,
            BuildingRules.HitsToBuild(BuildPieceKind.Wall, BuildMaterial.Wood));
    }
}
