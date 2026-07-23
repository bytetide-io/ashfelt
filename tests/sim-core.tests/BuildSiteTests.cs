using System;
using System.Collections.Generic;
using System.Linq;
using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// The build site is the construction half of the loop: deposit materials, then
/// strike pieces up in support order. Its rules — pay only for what is needed,
/// build a piece only when its supports stand and its materials are on site,
/// consume on completion — are the authoritative behaviour the server runs, so
/// they are pinned here.
/// </summary>
public class BuildSiteTests
{
    private static bool AnyGround(int x, int y) => true;

    private static PlannedPiece Foundation(int x, int y) =>
        new(BuildPieceKind.Foundation, BuildMaterial.Wood, new PieceSlot(x, y, 0, PieceLayer.Ground));

    private static PlannedPiece Wall(int x, int y, PieceLayer edge) =>
        new(BuildPieceKind.Wall, BuildMaterial.Wood, PieceSlot.Canonical(x, y, 0, edge));

    private static PlannedPiece Roof(int x, int y) =>
        new(BuildPieceKind.Roof, BuildMaterial.Wood, new PieceSlot(x, y, 0, PieceLayer.Cover));

    private static List<PlannedPiece> Hut() => new()
    {
        Foundation(0, 0),
        Wall(0, 0, PieceLayer.WallNorth),
        Wall(0, 0, PieceLayer.WallEast),
        Wall(0, 0, PieceLayer.WallSouth),
        Wall(0, 0, PieceLayer.WallWest),
        Roof(0, 0),
    };

    private static void DepositAll(BuildSite site)
    {
        foreach (var line in site.TotalBom())
            site.Deposit(line.Item, line.Amount);
    }

    /// <summary>Strikes the site until nothing more can be built, returning pieces in completion order.</summary>
    private static List<PlannedPiece> BuildToExhaustion(BuildSite site)
    {
        var completed = new List<PlannedPiece>();
        for (int guard = 0; guard < 1000; guard++)
        {
            var strike = site.TryBuildNext(AnyGround);
            if (!strike.Acted) break;
            if (strike.Completed) completed.Add(strike.Piece);
        }
        return completed;
    }

    [Fact]
    public void Deposit_TakesOnlyWhatThePiecesNeed_AndNoMore()
    {
        var site = new BuildSite(1, Guid.NewGuid(), new[] { Foundation(0, 0) }); // needs Wood 4

        Assert.Equal(4, site.Deposit(ItemId.Wood, 10)); // only 4 taken
        Assert.Equal(0, site.Deposit(ItemId.Wood, 5));  // already full on wood
        Assert.Equal(4, site.Storage[ItemId.Wood]);
    }

    [Fact]
    public void Deposit_RefusesItemsTheBlueprintDoesNotUse()
    {
        var site = new BuildSite(1, Guid.NewGuid(), new[] { Foundation(0, 0) }); // uses only Wood
        Assert.Equal(0, site.Deposit(ItemId.Stone, 10));
        Assert.False(site.Storage.ContainsKey(ItemId.Stone));
    }

    [Fact]
    public void APiece_CannotBeBuilt_WithoutItsMaterials()
    {
        var site = new BuildSite(1, Guid.NewGuid(), new[] { Foundation(0, 0) });
        var strike = site.TryBuildNext(AnyGround);
        Assert.False(strike.Acted);
    }

    [Fact]
    public void AFoundation_BuildsOnceItsMaterialsAreDeposited_ConsumingThem()
    {
        var site = new BuildSite(1, Guid.NewGuid(), new[] { Foundation(0, 0) });
        site.Deposit(ItemId.Wood, 4);

        var completed = BuildToExhaustion(site);

        Assert.Single(completed);
        Assert.Equal(BuildPieceKind.Foundation, completed[0].Kind);
        Assert.True(site.IsComplete);
        Assert.False(site.Storage.ContainsKey(ItemId.Wood)); // materials consumed
    }

    [Fact]
    public void AWall_WaitsForItsFoundationToBeBuilt_NotMerelyPlanned()
    {
        // Deposit only the wall's planks, none of the foundation's wood. The wall
        // must not build, because its foundation is not yet standing.
        var site = new BuildSite(1, Guid.NewGuid(), new[]
        {
            Foundation(0, 0),
            Wall(0, 0, PieceLayer.WallNorth),
        });
        site.Deposit(ItemId.Plank, 3);

        var strike = site.TryBuildNext(AnyGround);
        Assert.False(strike.Acted); // foundation unfunded, wall unsupported -> nothing builds
    }

    [Fact]
    public void AWholeHut_BuildsFoundationFirst_RoofLast()
    {
        var site = new BuildSite(1, Guid.NewGuid(), Hut());
        DepositAll(site);

        var order = BuildToExhaustion(site);

        Assert.Equal(6, order.Count);
        Assert.Equal(BuildPieceKind.Foundation, order.First().Kind);
        Assert.Equal(BuildPieceKind.Roof, order.Last().Kind);
        Assert.True(site.IsComplete);

        // The foundation completes before any wall; the roof after every wall.
        int foundationAt = order.FindIndex(p => p.Kind == BuildPieceKind.Foundation);
        int roofAt = order.FindIndex(p => p.Kind == BuildPieceKind.Roof);
        Assert.All(order.Select((p, i) => (p, i)).Where(t => t.p.Kind == BuildPieceKind.Wall),
            t => Assert.True(t.i > foundationAt && t.i < roofAt));
    }

    [Fact]
    public void MissingMaterials_ShrinksAsMaterialsAreDeposited()
    {
        var site = new BuildSite(1, Guid.NewGuid(), Hut());

        var before = site.MissingMaterials();
        Assert.Contains(before, m => m.Item == ItemId.Wood && m.Amount == 4);

        site.Deposit(ItemId.Wood, 4);

        Assert.DoesNotContain(site.MissingMaterials(), m => m.Item == ItemId.Wood);
    }

    [Fact]
    public void PartialStrikes_AreTransient_AndReportProgress()
    {
        var site = new BuildSite(1, Guid.NewGuid(), new[] { Foundation(0, 0) });
        site.Deposit(ItemId.Wood, 4);

        var first = site.TryBuildNext(AnyGround); // foundation takes 3 strikes
        Assert.True(first.Acted);
        Assert.False(first.Completed);
        Assert.Equal(first.StrikesTotal - 1, first.StrikesLeft);
        Assert.Equal(0, site.BuiltCount); // nothing built until the final strike
    }

    [Fact]
    public void RemainingBom_IgnoresBuiltPieces()
    {
        var site = new BuildSite(1, Guid.NewGuid(), Hut());
        DepositAll(site);
        BuildToExhaustion(site);

        Assert.Empty(site.RemainingBom());
        Assert.Empty(site.MissingMaterials());
    }

    [Fact]
    public void LoadBuilt_RestoresConstructionState_FromStorage()
    {
        var pieces = Hut();
        var original = new BuildSite(7, Guid.NewGuid(), pieces);
        var foundationSlot = pieces[0].Slot;

        // Simulate a restart: the foundation was built before the crash.
        var restored = new BuildSite(7, original.Owner, pieces);
        restored.LoadBuilt(foundationSlot);

        Assert.True(restored.IsBuilt(foundationSlot));
        Assert.Equal(1, restored.BuiltCount);
        // A wall is now buildable straight away, given its planks.
        restored.Deposit(ItemId.Plank, 3);
        Assert.True(restored.TryBuildNext(AnyGround).Acted);
    }
}
