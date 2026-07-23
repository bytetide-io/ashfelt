using System;
using System.Linq;
using Ashfall.Proto;
using Xunit;

namespace Ashfall.SimCore.Tests;

/// <summary>
/// The structure catalog is the single source of building-piece behaviour, read
/// by both client and server, so its internal consistency is pinned here: every
/// cost references a real item, every piece sits on a layer its kind allows, and
/// the (kind, material) keys are unique.
/// </summary>
public class StructureCatalogTests
{
    [Fact]
    public void EveryCostLine_ReferencesACataloguedItem_WithAPositiveAmount()
    {
        foreach (var def in StructureCatalog.All)
            foreach (var line in def.Cost)
            {
                Assert.True(ItemCatalog.TryGet(line.Item, out _), $"{def.Kind}/{def.Material} costs uncatalogued {line.Item}.");
                Assert.True(line.Amount > 0, $"{def.Kind}/{def.Material} has non-positive {line.Item} cost.");
            }
    }

    [Fact]
    public void EveryPiece_SitsOnALayerItsKindAllows()
    {
        foreach (var def in StructureCatalog.All)
            Assert.True(BuildingRules.KindMatchesLayer(def.Kind, def.Layer),
                $"{def.Kind} declared on incompatible layer {def.Layer}.");
    }

    [Fact]
    public void EveryPiece_TakesAtLeastOneStrike_AndHasAPositiveTier()
    {
        foreach (var def in StructureCatalog.All)
        {
            Assert.True(def.BuildStrikes >= 1, $"{def.Kind}/{def.Material} builds in zero strikes.");
            Assert.True(def.Tier >= 1, $"{def.Kind}/{def.Material} has a non-positive tier.");
        }
    }

    [Fact]
    public void KeysAreUnique_AcrossKindAndMaterial()
    {
        var keys = StructureCatalog.All.Select(def => (def.Kind, def.Material)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Of_ThrowsForAnUncataloguedPair()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureCatalog.Of(BuildPieceKind.Roof, BuildMaterial.Stone));
    }

    [Fact]
    public void CatalogOrder_IsStableAcrossReads()
    {
        var first = StructureCatalog.All.Select(def => (def.Kind, def.Material)).ToArray();
        var second = StructureCatalog.All.Select(def => (def.Kind, def.Material)).ToArray();
        Assert.Equal(first, second);
    }

    [Fact]
    public void SolidPieces_AreWallsAndWindows_NotOpenings()
    {
        // A doorway must never block movement — it is the way in.
        Assert.False(StructureCatalog.Of(BuildPieceKind.Doorway, BuildMaterial.Wood).Solid);
        Assert.True(StructureCatalog.Of(BuildPieceKind.Wall, BuildMaterial.Wood).Solid);
    }

    [Fact]
    public void AThatchRoof_IsAnEarlyShelter_BuiltFromFiber()
    {
        Assert.True(StructureCatalog.TryGet(BuildPieceKind.Roof, BuildMaterial.Thatch, out var def));
        Assert.True(def.ProvidesShelter);
        Assert.Contains(def.Cost, line => line.Item == ItemId.Fiber);
        Assert.DoesNotContain(def.Cost, line => line.Item == ItemId.Plank);
    }

    [Fact]
    public void MaterialsFor_OffersOnlyWhatAKindSupports()
    {
        var roofMaterials = StructureCatalog.MaterialsFor(BuildPieceKind.Roof);
        Assert.Contains(BuildMaterial.Thatch, roofMaterials);
        Assert.Contains(BuildMaterial.Wood, roofMaterials);

        var wallMaterials = StructureCatalog.MaterialsFor(BuildPieceKind.Wall);
        Assert.Contains(BuildMaterial.Wood, wallMaterials);
        Assert.Contains(BuildMaterial.Stone, wallMaterials);
        Assert.DoesNotContain(BuildMaterial.Thatch, wallMaterials); // no flimsy thatch walls
    }

    [Fact]
    public void ShelteringPieces_IncludeWallsAndRoof_NotFoundations()
    {
        Assert.True(StructureCatalog.Of(BuildPieceKind.Roof, BuildMaterial.Wood).ProvidesShelter);
        Assert.True(StructureCatalog.Of(BuildPieceKind.Wall, BuildMaterial.Wood).ProvidesShelter);
        Assert.False(StructureCatalog.Of(BuildPieceKind.Foundation, BuildMaterial.Wood).ProvidesShelter);
    }
}
